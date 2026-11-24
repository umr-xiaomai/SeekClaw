using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Agents;

/// <summary>
/// Adapts context usage to the active model: history budget, tool output budget and
/// summary sizes all derive from the model's context window — nothing is hard-coded.
/// </summary>
public static class ContextPlanner
{
    private const double CharsPerToken = 4.0;
    private const int PerMessageOverheadTokens = 8;
    private const string ContextCheckpointMarker = ">>> [Context compaction]";
    private const string HistoryTrimNotice = "[Earlier conversation history was trimmed to fit the model's context window.]";

    internal static ChatMessage CheckpointMessage(string summary) => ChatMessage.User(
        $"{ContextCheckpointMarker} Summary of earlier conversation. Use it with the messages that follow.\n\n{summary}");

    /// <summary>
    /// Hard cap for any single repository fragment injected into model context
    /// (AGENTS.md, MEMORY.md, skill prompts, MCP prompts). Mirrors Codex's rule that
    /// every injected fragment must be bounded and never unbounded.
    /// </summary>
    public const int MaxInjectedFragmentTokens = 10_000;

    /// <summary>Upper bound for the assembled system prompt before history budgeting starts.</summary>
    public const int MaxSystemPromptTokens = 24_000;

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling(text.Length / CharsPerToken);

    public static int EstimateTokens(ChatMessage message) =>
        EstimateTokens(message.Text)
        + EstimateTokens(message.Thinking ?? "")
        + (message.ToolCalls?.Sum(c => EstimateTokens(c.ArgumentsJson) + EstimateTokens(c.Name)) ?? 0)
        + (message.Images?.Count * 1_200 ?? 0)
        + PerMessageOverheadTokens;

    /// <summary>
    /// Truncates a single injected text fragment to <paramref name="maxTokens"/>, keeping
    /// both the head and tail so file paths / constraints and final decisions stay visible.
    /// </summary>
    public static string FitInjectedText(string text, int maxTokens = MaxInjectedFragmentTokens)
    {
        if (string.IsNullOrWhiteSpace(text) || EstimateTokens(text) <= maxTokens)
            return text;

        var budgetChars = Math.Max(1, (int)(maxTokens * CharsPerToken));
        var headChars = budgetChars / 2;
        var tailChars = budgetChars - headChars;
        if (headChars > 0 && char.IsHighSurrogate(text[headChars - 1])) headChars--;
        var tailStart = text.Length - tailChars;
        if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart])) tailStart++;
        var head = text[..headChars];
        var tail = text[tailStart..];
        return head
               + "\n\n… [middle section trimmed: injected context exceeded the token budget] …\n\n"
               + tail;
    }

    /// <summary>Character budget for a single tool result, scaled to the context window.</summary>
    public static int ToolOutputBudget(ModelConfig model, AgentConfig agent)
    {
        var byWindow = (int)(model.EffectiveContextWindow * CharsPerToken * 0.05); // ≤5% of the window per tool call
        return Math.Clamp(byWindow, 4_000, agent.MaxToolOutputChars);
    }

    /// <summary>
    /// Trims history so system prompt + messages + reply head-room fit the window.
    /// Oldest messages drop first; tool results shrink before user/assistant text is touched.
    /// </summary>
    /// <summary>Token budget available for conversation history inside the active model window.</summary>
    public static int WindowBudget(ModelConfig model, string systemPrompt, int? reservedOutputTokens = null)
    {
        var available = model.EffectiveContextWindow
                        - (reservedOutputTokens ?? model.EffectiveMaxOutput)
                        - EstimateTokens(systemPrompt);
        // Scale the wire overhead reserve for small context windows.
        var budget = available - Math.Clamp(model.EffectiveContextWindow / 20, 64, 1_500);
        if (budget > 0) return budget;
        return available > 0
            ? Math.Max(256, Math.Min(available, model.EffectiveContextWindow / 2))
            : Math.Max(256, model.EffectiveContextWindow / 2);
    }

    /// <summary>
    /// Splits history into the portion to summarize (Old) and the recent tail to keep
    /// verbatim (Recent). The tail is capped at half the window budget and always starts
    /// on a complete tool-turn boundary so no tool call is ever orphaned.
    /// </summary>
    public static (IReadOnlyList<ChatMessage> Old, IReadOnlyList<ChatMessage> Recent) SplitForCompaction(
        IReadOnlyList<ChatMessage> messages, ModelConfig model, string systemPrompt)
    {
        var tailBudget = WindowBudget(model, systemPrompt) / 2;

        var split = messages.Count;
        var total = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var cost = EstimateTokens(messages[i]);
            if (split < messages.Count && total + cost > tailBudget) break;
            split = i;
            total += cost;
        }

        // Keep tool-call pairs intact. A tail must never begin with a tool result: move the
        // split back to the assistant message that owns the entire result block, otherwise
        // the summarization request would carry tool results without their tool_calls.
        if (split < messages.Count && messages[split].Role == ChatRole.Tool)
        {
            var assistantIndex = split - 1;
            while (assistantIndex >= 0 && messages[assistantIndex].Role == ChatRole.Tool) assistantIndex--;
            if (assistantIndex >= 0
                && messages[assistantIndex].Role == ChatRole.Assistant
                && messages[assistantIndex].ToolCalls is { Count: > 0 })
            {
                split = assistantIndex;
            }
            else
            {
                // Malformed history without an owning assistant: keep results out of the tail.
                while (split < messages.Count && messages[split].Role == ChatRole.Tool) split++;
            }
        }

        return (messages.Take(split).ToList(), messages.Skip(split).ToList());
    }

    public static IReadOnlyList<ChatMessage> FitToWindow(
        IReadOnlyList<ChatMessage> messages, ModelConfig model, string systemPrompt,
        IReadOnlySet<ChatMessage>? preserve = null)
    {
        messages = EnsureCompleteToolTurns(messages);
        var budget = WindowBudget(model, systemPrompt);

        var total = messages.Sum(EstimateTokens);
        if (total <= budget) return messages;

        var result = new List<ChatMessage>(messages);
        var trimStart = messages[0].Role == ChatRole.User
                        && messages[0].Text.StartsWith(ContextCheckpointMarker, StringComparison.Ordinal) ? 1 : 0;
        var latestUser = result.LastOrDefault(message => message.Role == ChatRole.User
            && message.Text != HistoryTrimNotice && !message.Text.StartsWith(ContextCheckpointMarker, StringComparison.Ordinal));
        var trimNotice = ChatMessage.User(HistoryTrimNotice);
        var noticeCost = EstimateTokens(trimNotice);
        var cursor = trimStart;
        var trimmed = false;
        while (total + noticeCost > budget && cursor < result.Count - 1)
        {
            var next = EndOfToolExchange(result, cursor, result.Count);
            if (ReferenceEquals(result[cursor], latestUser) || ContainsPreservedMessage(result, cursor, next, preserve))
            {
                cursor = next;
                continue;
            }
            // Keep the most recent message or complete tool exchange intact.
            if (next == result.Count) break;

            for (var i = cursor; i < next; i++) total -= EstimateTokens(result[i]);
            result.RemoveRange(cursor, next - cursor);
            trimmed = true;
        }

        // Large tool payloads can exceed a small window even after trimming old
        // messages. Shorten only the model's copy and keep every required result.
        var overflow = total + (trimmed ? noticeCost : 0) - budget;
        var trimmableTools = result.Select((message, index) => (Message: message, Index: index))
            .Where(entry => entry.Message.Role == ChatRole.Tool && preserve?.Contains(entry.Message) != true)
            .OrderByDescending(entry => entry.Message.Text.Length);
        foreach (var (message, index) in trimmableTools.ToArray())
        {
            if (overflow <= 0) break;
            var original = EstimateTokens(message.Text);
            var excerptBudget = original - overflow - 32; // reserve the excerpt marker
            var text = excerptBudget > 0
                ? FitInjectedText(message.Text, excerptBudget)
                : "[Tool result shortened to fit the model's context window.]";
            var trimmedCost = EstimateTokens(text);
            if (trimmedCost >= original) continue;
            result[index] = CloneWithText(message, text);
            overflow -= original - trimmedCost;
        }

        if (trimmed && !result.Any(message => message.Role == ChatRole.User && message.Text == HistoryTrimNotice))
            result.Insert(trimStart, trimNotice);

        return result;
    }

    private static bool ContainsPreservedMessage(IReadOnlyList<ChatMessage> messages, int start, int end,
        IReadOnlySet<ChatMessage>? preserve)
    {
        if (preserve is null) return false;
        for (var index = start; index < end; index++)
            if (preserve.Contains(messages[index])) return true;
        return false;
    }

    internal static int EndOfToolExchange(IReadOnlyList<ChatMessage> messages, int index, int limit)
    {
        var end = index + 1;
        while (end < limit && messages[end].Role == ChatRole.Tool) end++;
        return end;
    }

    /// <summary>
    /// Repairs interrupted or legacy history before it is sent to a provider. Every assistant
    /// tool call must be followed immediately by exactly one result for each requested call.
    /// Orphan results are dropped and missing results become explicit failed executions.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> EnsureCompleteToolTurns(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        var changed = false;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == ChatRole.Tool)
            {
                changed = true;
                continue;
            }

            result.Add(message);
            if (message.Role != ChatRole.Assistant || message.ToolCalls is not { Count: > 0 } calls)
                continue;

            var followingResults = new List<ChatMessage>();
            var next = index + 1;
            while (next < messages.Count && messages[next].Role == ChatRole.Tool)
            {
                followingResults.Add(messages[next]);
                next++;
            }

            var byCallId = followingResults
                .Where(item => !string.IsNullOrWhiteSpace(item.ToolCallId))
                .GroupBy(item => item.ToolCallId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var alreadyComplete = followingResults.Count == calls.Count
                                  && calls.Select(call => call.Id)
                                      .SequenceEqual(followingResults.Select(item => item.ToolCallId), StringComparer.Ordinal);
            changed |= !alreadyComplete;

            foreach (var call in calls)
            {
                result.Add(byCallId.TryGetValue(call.Id, out var toolResult)
                    ? toolResult
                    : ChatMessage.ToolResult(
                        call.Id,
                        call.Name,
                        "Tool execution did not complete. Retry the tool if its result is still needed.",
                        false));
            }
            index = next - 1;
        }

        return changed ? result : messages;
    }

    public const string PrunedToolResultMarker = "[... pruned by SeekClaw: ";

    /// <summary>
    /// Compacts oversized <see cref="ChatRole.Tool"/> results prior to <paramref name="endExclusive"/>
    /// by preserving leading and trailing context while removing middle output, saving context
    /// without invoking a model call.
    /// </summary>
    public static int PruneToolResults(
        IList<ChatMessage> messages,
        int endExclusive,
        int thresholdChars = 8_192,
        int headChars = 4_096,
        int tailChars = 1_024,
        IReadOnlySet<ChatMessage>? preserve = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var limit = Math.Min(endExclusive, messages.Count);
        var pruned = 0;

        for (var i = 0; i < limit; i++)
        {
            var message = messages[i];
            if (message.Role != ChatRole.Tool || preserve?.Contains(message) == true) continue;
            var text = message.Text;
            if (string.IsNullOrEmpty(text) || text.Length <= thresholdChars) continue;
            if (text.Contains(PrunedToolResultMarker, StringComparison.Ordinal)) continue;

            var headEnd = Math.Clamp(headChars, 0, text.Length);
            if (headEnd > 0 && headEnd < text.Length && char.IsHighSurrogate(text[headEnd - 1]))
                headEnd--;

            var tailStart = Math.Clamp(text.Length - Math.Max(0, tailChars), headEnd, text.Length);
            if (tailStart < text.Length && tailStart > headEnd && char.IsLowSurrogate(text[tailStart]))
                tailStart++;

            var omittedChars = tailStart - headEnd;
            if (omittedChars <= 0) continue;

            var marker = $"\n\n{PrunedToolResultMarker}omitted {omittedChars} chars to fit context ...]\n\n";
            var shortened = string.Concat(text.AsSpan(0, headEnd), marker, text.AsSpan(tailStart));
            if (shortened.Length >= text.Length) continue;

            messages[i] = CloneWithText(message, shortened);
            pruned++;
        }

        return pruned;
    }

    private static ChatMessage CloneWithText(ChatMessage message, string text) => new()
    {
        Role = message.Role,
        Text = text,
        Images = message.Images,
        Thinking = message.Thinking,
        ThinkingSignature = message.ThinkingSignature,
        ModelRef = message.ModelRef,
        FinishReason = message.FinishReason,
        ErrorMessage = message.ErrorMessage,
        ViewedImages = message.ViewedImages,
        ToolCalls = message.ToolCalls,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        ToolSuccess = message.ToolSuccess,
        ToolDiff = message.ToolDiff,
        ToolFilePath = message.ToolFilePath,
        ToolContent = message.ToolContent,
        ToolStructuredContent = message.ToolStructuredContent,
        ToolMetadata = message.ToolMetadata,
        ToolWidgets = message.ToolWidgets,
        Timestamp = message.Timestamp,
    };
}
