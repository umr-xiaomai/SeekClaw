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

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling(text.Length / CharsPerToken);

    public static int EstimateTokens(ChatMessage message) =>
        EstimateTokens(message.Text)
        + (message.ToolCalls?.Sum(c => EstimateTokens(c.ArgumentsJson) + EstimateTokens(c.Name)) ?? 0)
        + PerMessageOverheadTokens;

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
    public static IReadOnlyList<ChatMessage> FitToWindow(
        IReadOnlyList<ChatMessage> messages, ModelConfig model, string systemPrompt)
    {
        var budget = model.ContextWindow
                     - model.MaxOutput
                     - EstimateTokens(systemPrompt)
                     - 1_500; // safety reserve for wire format overhead
        if (budget <= 0) budget = model.ContextWindow / 2;

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
}
