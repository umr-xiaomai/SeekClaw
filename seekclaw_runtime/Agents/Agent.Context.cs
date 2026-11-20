using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Verification;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

public sealed partial class Agent
{
    // ---------------------------------------------------------------- llm streaming

    /// <summary>
    /// History copy with image payloads removed, used for text-only turns so earlier
    /// attachments are not re-uploaded on every follow-up message (slow) and do not
    /// force the non-streaming provider path.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> WithoutImages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.All(message => message.Images is not { Count: > 0 }))
            return messages;
        return messages
            .Select(message => message.Images is not { Count: > 0 }
                ? message
                : new ChatMessage
                {
                    Role = message.Role,
                    Text = message.Text,
                    ModelRef = message.ModelRef,
                    FinishReason = message.FinishReason,
                    ErrorMessage = message.ErrorMessage,
                    Thinking = message.Thinking,
                    ThinkingSignature = message.ThinkingSignature,
                    ViewedImages = message.ViewedImages,
                    ToolCalls = message.ToolCalls,
                    ToolCallId = message.ToolCallId,
                    ToolName = message.ToolName,
                    ToolSuccess = message.ToolSuccess,
                    ToolDiff = message.ToolDiff,
                    ToolFilePath = message.ToolFilePath,
                    ToolContent = message.ToolContent, ToolStructuredContent = message.ToolStructuredContent, ToolMetadata = message.ToolMetadata,
                    ToolWidgets = message.ToolWidgets,
                })
            .ToList();
    }

    private async Task<bool> CompactContextAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        ModelInfo model,
        string systemPrompt,
        int currentTurnStartIndex,
        int step,
        bool requiresVision,
        CancellationToken ct,
        ModelInfo? pinnedModel)
    {
        var source = session.Messages;
        var (old, recent) = ContextPlanner.SplitForCompaction(source, model.Model, systemPrompt);
        var stage1Pruned = false;
        if (configStore.Config.Routing.DeepSeekOptimizationEnabled
            && DeepSeekOptimizationPolicy.IsDeepSeek(model.Provider, model.Model)
            && old.Count > 0)
        {
            var prunedHistory = new List<ChatMessage>(source);
            var prunedCount = ContextPlanner.PruneToolResults(prunedHistory, old.Count);
            if (prunedCount > 0)
            {
                try
                {
                    sessionStore.ReplaceHistory(session, prunedHistory);
                    stage1Pruned = true;
                    source = session.Messages;
                }
                catch (Exception ex)
                {
                    events.Publish(new WarningEvent($"Could not persist tool result pruning: {ex.Message}"));
                }

                if (stage1Pruned)
                {
                    var fitCandidate = requiresVision ? source : WithoutImages(source);
                    var fitted = ContextPlanner.FitToWindow(fitCandidate, model.Model, systemPrompt);
                    if (fitted.Count + 4 > source.Count)
                    {
                        PublishWorkflow(step, "prune", "裁剪工具输出");
                        events.Publish(new StatusEvent("Context pruned", "Oversized tool outputs trimmed"));
                        return true;
                    }

                    (old, recent) = ContextPlanner.SplitForCompaction(source, model.Model, systemPrompt);
                }
            }
        }

        if (currentTurnStartIndex >= 0 && old.Count > currentTurnStartIndex)
        {
            // Never summarize the user message or any tool exchange from the turn that
            // is currently executing. Besides losing instructions, summarizing image
            // payloads here would make them disappear from the persisted history.
            old = source.Take(currentTurnStartIndex).ToList();
            recent = source.Skip(currentTurnStartIndex).ToList();
        }
        if (old.Count == 0
            || (old.Count == 1 && old[0].Text.StartsWith(">>> [Context compaction]", StringComparison.Ordinal)))
            return stage1Pruned;

        PublishWorkflow(step, "compact", "压缩记忆");
        events.Publish(new StatusEvent("Compacting context", $"{old.Count} earlier messages"));
        string? summary;
        try
        {
            summary = await SummarizeContextAsync(workspace, systemPrompt, old, ct, pinnedModel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Compaction must never break the turn: fall back to the plain window trim.
            events.Publish(new WarningEvent($"Context compaction failed: {ex.Message}"));
            return stage1Pruned;
        }

        if (string.IsNullOrWhiteSpace(summary)) return stage1Pruned;

        var summaryMessage = ChatMessage.User(
            ">>> [Context compaction] 之前的对话已被压缩以保持上下文可容纳，以下是阶段性总结：\n\n"
            + summary.Trim());

        if (configStore.Config.Routing.DeepSeekOptimizationEnabled
            && DeepSeekOptimizationPolicy.IsDeepSeek(model.Provider, model.Model))
        {
            var compactedTokens = old.Sum(ContextPlanner.EstimateTokens);
            var summaryTokens = ContextPlanner.EstimateTokens(summaryMessage);
            if (summaryTokens >= compactedTokens)
            {
                events.Publish(new WarningEvent("Context summary did not shrink history; skipping compaction."));
                return stage1Pruned;
            }
        }

        var compacted = new List<ChatMessage>(recent.Count + 1) { summaryMessage };
        compacted.AddRange(recent);

        try
        {
            sessionStore.ReplaceHistory(session, compacted);
        }
        catch (Exception ex)
        {
            events.Publish(new WarningEvent($"Could not persist context compaction: {ex.Message}"));
        }

        events.Publish(new StatusEvent("Context compacted", $"{old.Count} earlier messages summarized"));
        return true;
    }

    private async Task<string?> SummarizeContextAsync(
        WorkspaceInfo workspace,
        string systemPrompt,
        IReadOnlyList<ChatMessage> old,
        CancellationToken ct,
        ModelInfo? pinnedModel)
    {
        // Image payloads are not needed for the summary; dropping them keeps the call small.
        var instruction = promptProvider.TryGet("builtin/summarize");
        if (instruction is null)
        {
            events.Publish(new WarningEvent("Compaction prompt 'builtin/summarize' is missing; falling back to plain history trimming."));
            return null;
        }
        var withoutImages = WithoutImages(old);
        var completion = await CollectCompletionAsync(
            candidate =>
            {
                var optimizeDeepSeek = configStore.Config.Routing.DeepSeekOptimizationEnabled;
                var applyDeepSeekCompaction = optimizeDeepSeek
                    && DeepSeekOptimizationPolicy.IsDeepSeek(candidate.Provider, candidate.Model);
                var requestSystem = applyDeepSeekCompaction ? systemPrompt : instruction;
                var budgetSystem = applyDeepSeekCompaction ? $"{systemPrompt}\n\n{instruction}" : instruction;
                var input = ContextPlanner.FitToWindow(withoutImages, candidate.Model, budgetSystem).ToList();
                if (applyDeepSeekCompaction)
                {
                    input.Add(ChatMessage.User(instruction));
                }
                else if (input.Count == 0 || input[^1].Role != ChatRole.User)
                {
                    input.Add(ChatMessage.User("请根据以上对话历史生成阶段性总结。"));
                }
                return new LlmRequest
                {
                    Provider = candidate.Provider,
                    Model = candidate.Model,
                    Messages = input,
                    System = requestSystem,
                    MaxTokens = Math.Min(4_096, candidate.Model.EffectiveMaxOutput),
                    EnableThinking = false,
                    ReasoningLevel = ReasoningLevel.None,
                    OptimizeDeepSeek = optimizeDeepSeek,
                    Purpose = applyDeepSeekCompaction ? "compaction" : null,
                };
            },
            workspace, ct, pinnedModel).ConfigureAwait(false);
        return completion?.Text;
    }
}
