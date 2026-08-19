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
                    Thinking = message.Thinking,
                    ViewedImages = message.ViewedImages,
                    ToolCalls = message.ToolCalls,
                    ToolCallId = message.ToolCallId,
                    ToolName = message.ToolName,
                    ToolSuccess = message.ToolSuccess,
                    ToolDiff = message.ToolDiff,
                    ToolFilePath = message.ToolFilePath,
                })
            .ToList();
    }

    private async Task CompactContextAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        ModelInfo model,
        IReadOnlyList<ChatMessage> source,
        string systemPrompt,
        CancellationToken ct)
    {
        var (old, recent) = ContextPlanner.SplitForCompaction(source, model.Model, systemPrompt);
        if (old.Count == 0) return;

        events.Publish(new StatusEvent("Compacting context", $"{old.Count} earlier messages"));
        string? summary;
        try
        {
            summary = await SummarizeContextAsync(model, workspace, old, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Compaction must never break the turn: fall back to the plain window trim.
            events.Publish(new WarningEvent($"Context compaction failed: {ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(summary)) return;

        var summaryMessage = ChatMessage.User(
            ">>> [Context compaction] 之前的对话已被压缩以保持上下文可容纳，以下是阶段性总结：\n\n"
            + summary.Trim());

        session.Messages.Clear();
        session.Messages.Add(summaryMessage);
        session.Messages.AddRange(recent);

        try
        {
            sessionStore.ReplaceHistory(session, session.Messages);
        }
        catch (Exception ex)
        {
            // Keep the in-memory compacted view even if persistence fails.
            events.Publish(new WarningEvent($"Could not persist context compaction: {ex.Message}"));
        }

        events.Publish(new StatusEvent("Context compacted", $"{old.Count} earlier messages summarized"));
    }

    private async Task<string?> SummarizeContextAsync(
        ModelInfo model, WorkspaceInfo workspace, IReadOnlyList<ChatMessage> old, CancellationToken ct)
    {
        // Image payloads are not needed for the summary; dropping them keeps the call small.
        var instruction = promptProvider.TryGet("builtin/summarize");
        if (instruction is null)
        {
            events.Publish(new WarningEvent("Compaction prompt 'builtin/summarize' is missing; falling back to plain history trimming."));
            return null;
        }
        var input = ContextPlanner.FitToWindow(WithoutImages(old), model.Model, instruction);
        var completion = await CollectCompletionAsync(
            candidate => new LlmRequest
            {
                Provider = candidate.Provider,
                Model = candidate.Model,
                Messages = input,
                System = instruction,
                MaxTokens = 4_096,
                EnableThinking = false,
                ReasoningLevel = ReasoningLevel.None,
            },
            workspace, ct).ConfigureAwait(false);
        return completion?.Text;
    }
}
