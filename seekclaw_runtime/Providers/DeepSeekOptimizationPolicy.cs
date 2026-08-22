using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

/// <summary>
/// Centralized, opt-in policy for DeepSeek-specific request serialization. Keeping the
/// provider/model matching and wire adjustments here means future DeepSeek model/API changes
/// can be made without touching the generic OpenAI-compatible client or call sites.
/// </summary>
internal static class DeepSeekOptimizationPolicy
{
    public static bool IsDeepSeek(ProviderConfig provider, ModelConfig model) =>
        ReasoningLevelAdapter.IsDeepSeek(provider, model);

    public static bool Applies(LlmRequest request) =>
        request.OptimizeDeepSeek && IsDeepSeek(request.Provider, request.Model);

    /// <summary>
    /// DeepSeek requires reasoning_content to be passed back only for tool-calling turns;
    /// passing it on plain text turns costs tokens without changing the next completion.
    /// When the opt-in is off, preserve the historical safe behavior and pass it back.
    /// </summary>
    public static bool ShouldPassBackReasoning(LlmRequest request, ChatMessage message)
    {
        if (!IsDeepSeek(request.Provider, request.Model)) return false;
        if (string.IsNullOrEmpty(message.Thinking)) return false;
        if (!Applies(request)) return true;
        return message.ToolCalls is { Count: > 0 };
    }

    public static string ToolResultContent(string text, LlmRequest request) =>
        Applies(request) && string.IsNullOrWhiteSpace(text) ? "(no output)" : text;

    /// <summary>
    /// DeepSeek uses a top-level thinking switch plus reasoning_effort; the latter is produced
    /// by <see cref="ReasoningLevelAdapter.OpenAiEffort"/>. Keeping this mapping here
    /// isolates provider-specific wire shape from the generic body builder.
    /// </summary>
    public static JsonObject? ThinkingWire(LlmRequest request)
    {
        if (!Applies(request)) return null;
        return new JsonObject
        {
            ["type"] = request.EnableThinking ? "enabled" : "disabled",
        };
    }

    /// <summary>
    /// Stream idle timeout matching deepseek-harness (default 300s / 5 minutes).
    /// Prevents long thinking and coding turns from timing out as long as active tokens/comments arrive.
    /// </summary>
    public static TimeSpan GetStreamIdleTimeout(ProviderConfig provider) =>
        TimeSpan.FromSeconds(Math.Max(provider.TimeoutSeconds > 0 ? provider.TimeoutSeconds : 60, 300));

    /// <summary>
    /// DeepSeek reports prompt_tokens = prompt_cache_hit_tokens + prompt_cache_miss_tokens.
    /// Returns the disjoint (uncached) input tokens.
    /// </summary>
    public static long DisjointInputTokens(long promptTokens, long cachedTokens, LlmRequest request)
    {
        if (!Applies(request) || cachedTokens <= 0) return promptTokens;
        return Math.Max(0, promptTokens - cachedTokens);
    }

    public static LlmCompletion ValidateCompletion(LlmCompletion completion, LlmRequest request)
    {
        if (!Applies(request)) return completion;
        if (string.IsNullOrWhiteSpace(completion.Text)
            && string.IsNullOrWhiteSpace(completion.Thinking)
            && completion.ToolCalls.Count == 0)
        {
            throw new LlmException("DeepSeek returned an empty completion.", retryable: true);
        }

        return completion;
    }
}
