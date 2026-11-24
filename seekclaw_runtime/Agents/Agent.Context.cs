using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
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
                    Timestamp = message.Timestamp,
                })
            .ToList();
    }

    private const int MaximumBatchesPerCompaction = 4;

    private static IReadOnlyList<ChatMessage> BuildModelHistory(AgentSession session, bool includeImages,
        IReadOnlySet<ChatMessage> turnInstructions)
    {
        IReadOnlyList<ChatMessage> history = session.Messages;
        if (session.ContextCheckpoint is { } checkpoint)
        {
            var withSummary = new List<ChatMessage>(session.Messages.Count - checkpoint.CoveredMessageCount + 1)
            {
                ContextPlanner.CheckpointMessage(checkpoint.Summary),
            };
            // Replay the live request and guidance exactly while this turn continues.
            // After the turn finishes, their summary is sufficient for later requests.
            withSummary.AddRange(session.Messages.Take(checkpoint.CoveredMessageCount).Where(turnInstructions.Contains));
            withSummary.AddRange(session.Messages.Skip(checkpoint.CoveredMessageCount));
            history = withSummary;
        }
        return includeImages ? history : WithoutImages(history);
    }

    private async Task<IReadOnlyList<ChatMessage>> CompactContextAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        ModelInfo model,
        string systemPrompt,
        IReadOnlyList<ChatMessage> source,
        IReadOnlySet<ChatMessage> turnInstructions,
        int step,
        CancellationToken ct)
    {
        var previouslyCovered = session.ContextCheckpoint?.CoveredMessageCount ?? 0;
        var firstUncovered = source.Count - (session.Messages.Count - previouslyCovered);
        var end = ContextPlanner.SplitForCompaction(source, model.Model, systemPrompt).Old.Count;
        if (end <= firstUncovered) return source;

        // The generation call can overlap with a rewind. Use the original
        // transcript entries as proof of coverage when committing the summary.
        var transcript = session.Messages.ToArray();
        source = source.ToArray();

        var optimizeDeepSeek = configStore.Config.Routing.DeepSeekOptimizationEnabled;
        var deepSeekCompaction = optimizeDeepSeek && DeepSeekOptimizationPolicy.IsDeepSeek(model.Provider, model.Model);
        if (deepSeekCompaction)
        {
            var pruned = source.ToList();
            if (ContextPlanner.PruneToolResults(pruned, end, preserve: turnInstructions) > 0)
            {
                source = pruned;
                if (source.Sum(ContextPlanner.EstimateTokens) <= ContextPlanner.WindowBudget(model.Model, systemPrompt))
                {
                    PublishWorkflow(step, "prune", "裁剪工具输出");
                    events.Publish(new StatusEvent("Context pruned", "Oversized tool outputs trimmed"));
                    return source;
                }
                end = ContextPlanner.SplitForCompaction(source, model.Model, systemPrompt).Old.Count;
                if (end <= firstUncovered) return source;
            }
        }

        if (source.Skip(firstUncovered).Take(end - firstUncovered).All(turnInstructions.Contains))
            return source;

        var instruction = promptProvider.TryGet("builtin/summarize");
        if (instruction is null)
        {
            events.Publish(new WarningEvent("Compaction prompt 'builtin/summarize' is missing; falling back to plain history trimming."));
            return source;
        }

        var requestSystem = deepSeekCompaction ? systemPrompt : instruction;
        var suffix = ChatMessage.User(deepSeekCompaction ? instruction : "请根据以上对话历史生成阶段性总结。");
        var maxSummaryTokens = Math.Min(model.Model.EffectiveMaxOutput,
            Math.Clamp(ContextPlanner.WindowBudget(model.Model, systemPrompt) / 4, 32, 4_096));
        var inputBudget = ContextPlanner.WindowBudget(model.Model, requestSystem, maxSummaryTokens)
                          - ContextPlanner.EstimateTokens(suffix);
        var textOnly = WithoutImages(source);
        var workingSummary = firstUncovered != 0 ? source[0] : null;
        var savedSummary = workingSummary;
        var replacedTokens = savedSummary is null ? 0 : ContextPlanner.EstimateTokens(savedSummary);
        var next = firstUncovered;
        var savedThrough = firstUncovered;
        PublishWorkflow(step, "compact", "压缩记忆");
        events.Publish(new StatusEvent("Compacting context", $"{end - firstUncovered} earlier messages"));

        // Feed the oldest unsummarized entries in order. A request that does not
        // fit may stop early, but can never skip ahead and claim older entries.
        for (var batch = 0; batch < MaximumBatchesPerCompaction && next < end; batch++)
        {
            ct.ThrowIfCancellationRequested();
            var (input, batchEnd) = BuildSummaryBatch(textOnly, next, end, workingSummary, inputBudget);
            if (batchEnd == next) break;
            input.Add(suffix);

            try
            {
                // Keep the selected model and budget together. If compaction fails,
                // the regular request can still use fallback routing and window trimming.
                var completion = await CollectCompletionAsync(candidate => new LlmRequest
                {
                    Provider = candidate.Provider,
                    Model = candidate.Model,
                    Messages = input,
                    System = requestSystem,
                    MaxTokens = maxSummaryTokens,
                    EnableThinking = false,
                    ReasoningLevel = ReasoningLevel.None,
                    OptimizeDeepSeek = optimizeDeepSeek,
                    Purpose = deepSeekCompaction ? "compaction" : null,
                }, workspace, ct, model).ConfigureAwait(false);

                if (completion?.ErrorMessage is not null || IsOutputTruncated(completion?.FinishReason)
                    || string.IsNullOrWhiteSpace(completion?.Text)) break;

                var summary = completion.Text.Trim();
                if (ContextPlanner.EstimateTokens(summary) > maxSummaryTokens) break;
                workingSummary = ContextPlanner.CheckpointMessage(summary);
                for (var index = next; index < batchEnd; index++)
                    if (!turnInstructions.Contains(source[index]))
                        replacedTokens += ContextPlanner.EstimateTokens(source[index]);
                next = batchEnd;

                // A batch containing live instructions cannot save space on its own.
                // Carry its summary forward and persist only after completed work can be removed.
                var summaryTokens = ContextPlanner.EstimateTokens(workingSummary);
                if (summaryTokens >= replacedTokens) continue;

                var covered = previouslyCovered + next - firstUncovered;
                if (!sessionStore.TrySaveContextCheckpoint(session, covered, transcript[covered - 1], summary)) break;
                savedSummary = workingSummary;
                savedThrough = next;
                replacedTokens = summaryTokens;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                events.Publish(new WarningEvent($"Context compaction failed: {ex.Message}"));
                break;
            }
        }

        if (savedThrough == firstUncovered || savedSummary is null) return source;
        events.Publish(new StatusEvent("Context compacted", $"{savedThrough - firstUncovered} earlier messages summarized"));
        var compacted = new List<ChatMessage>(source.Count - savedThrough + 1) { savedSummary };
        compacted.AddRange(source.Take(savedThrough).Where(turnInstructions.Contains));
        compacted.AddRange(source.Skip(savedThrough));
        return compacted;
    }

    private static (List<ChatMessage> Input, int End) BuildSummaryBatch(IReadOnlyList<ChatMessage> source,
        int start, int end, ChatMessage? summary, int budget)
    {
        var input = new List<ChatMessage>();
        if (summary is not null)
        {
            input.Add(summary);
            budget -= ContextPlanner.EstimateTokens(summary);
        }
        var index = start;
        while (index < end && budget > 0)
        {
            var groupEnd = ContextPlanner.EndOfToolExchange(source, index, end);
            var group = ContextPlanner.EnsureCompleteToolTurns(source.Skip(index).Take(groupEnd - index).ToList());
            var tokens = group.Sum(ContextPlanner.EstimateTokens);
            if (tokens > budget)
            {
                if (index != start) break;
                var excerpt = OversizedHistoryExcerpt(group, budget);
                if (excerpt is null) break;
                input.Add(excerpt);
                return (input, groupEnd);
            }
            input.AddRange(group);
            budget -= tokens;
            index = groupEnd;
        }
        return (input, index);
    }

    private static ChatMessage? OversizedHistoryExcerpt(IReadOnlyList<ChatMessage> group, int budget)
    {
        const string prefix = "[Excerpt from an oversized earlier history entry. The full record is saved in the conversation.]\n";
        const int trimMarkerReserve = 32;
        var textBudget = budget - ContextPlanner.EstimateTokens(ChatMessage.User(prefix)) - trimMarkerReserve;
        if (textBudget <= 0) return null;

        var parts = group.Select(message =>
        {
            var calls = message.ToolCalls is { Count: > 0 }
                ? string.Join("\n", message.ToolCalls.Select(call => $"Tool call {call.Name} {call.ArgumentsJson}"))
                : "";
            var target = message.Role == ChatRole.Tool ? $" {message.ToolName} {message.ToolFilePath}" : "";
            return $"[{message.Role}{target}]\n{calls}\n{message.Text}";
        });
        var excerpt = ChatMessage.User(prefix + ContextPlanner.FitInjectedText(string.Join("\n\n", parts), textBudget));
        return ContextPlanner.EstimateTokens(excerpt) <= budget ? excerpt : null;
    }
}
