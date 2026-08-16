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
        var head = text[..headChars];
        var tail = text[^tailChars..];
        return head
               + "\n\n… [middle section trimmed: injected context exceeded the token budget] …\n\n"
               + tail;
    }

    /// <summary>Character budget for a single tool result, scaled to the context window.</summary>
    public static int ToolOutputBudget(ModelConfig model, AgentConfig agent)
    {
        var byWindow = (int)(model.ContextWindow * CharsPerToken * 0.05); // ≤5% of the window per tool call
        return Math.Clamp(byWindow, 4_000, agent.MaxToolOutputChars);
    }

    /// <summary>
    /// Trims history so system prompt + messages + reply head-room fit the window.
    /// Oldest messages drop first; tool results shrink before user/assistant text is touched.
    /// </summary>
    /// <summary>Token budget available for conversation history inside the active model window.</summary>
    public static int WindowBudget(ModelConfig model, string systemPrompt)
    {
        var budget = model.ContextWindow
                     - model.MaxOutput
                     - EstimateTokens(systemPrompt)
                     - 1_500; // safety reserve for wire format overhead
        return budget > 0 ? budget : model.ContextWindow / 2;
    }

    /// <summary>
    /// Splits history into the portion to summarize (Old) and the recent tail to keep
    /// verbatim (Recent). The tail is capped at half the window budget and always starts
    /// on a complete tool-turn boundary so no tool call is ever orphaned.
    /// </summary>
    public static (IReadOnlyList<ChatMessage> Old, IReadOnlyList<ChatMessage> Recent) SplitForCompaction(
        IReadOnlyList<ChatMessage> messages, ModelConfig model, string systemPrompt)
    {
        var tailBudget = Math.Max(1_024, WindowBudget(model, systemPrompt) / 2);

        var split = messages.Count;
        var total = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var cost = EstimateTokens(messages[i]);
            if (split < messages.Count && total + cost > tailBudget) break;
            split = i;
            total += cost;
        }

        // Keep tool-call pairs intact: if the tail would open with a tool result, fold
        // those results back into the summarized old part (their assistant turn is there).
        while (split < messages.Count && messages[split].Role == ChatRole.Tool) split++;

        return (messages.Take(split).ToList(), messages.Skip(split).ToList());
    }

    public static IReadOnlyList<ChatMessage> FitToWindow(
        IReadOnlyList<ChatMessage> messages, ModelConfig model, string systemPrompt)
    {
        messages = EnsureCompleteToolTurns(messages);
        var budget = WindowBudget(model, systemPrompt);

        var total = messages.Sum(EstimateTokens);
        if (total <= budget) return messages;

        var result = new List<ChatMessage>(messages);

        // Pass 1: truncate old tool outputs down to a stub.
        for (var i = 0; i < result.Count - 6 && total > budget; i++)
        {
            var msg = result[i];
            if (msg.Role != ChatRole.Tool || msg.Text.Length <= 400) continue;
            var before = EstimateTokens(msg);
            result[i] = ChatMessage.ToolResult(
                msg.ToolCallId ?? "", msg.ToolName ?? "",
                msg.Text[..400] + "\n… [older tool output trimmed to fit the context window]",
                msg.ToolSuccess);
            total += EstimateTokens(result[i]) - before;
        }

        // Pass 2: drop oldest messages, keeping tool-call pairs intact and the recent tail.
        while (total > budget && result.Count > 6)
        {
            var victim = result[0];
            total -= EstimateTokens(victim);
            result.RemoveAt(0);

            // Never leave an orphan tool result at the head.
            while (result.Count > 0 && result[0].Role == ChatRole.Tool)
            {
                total -= EstimateTokens(result[0]);
                result.RemoveAt(0);
            }
        }

        if (result.Count < messages.Count)
            result.Insert(0, ChatMessage.User(
                "[Earlier conversation history was trimmed to fit the model's context window.]"));

        return result;
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
}
