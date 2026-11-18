using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.Providers;

/// <summary>
/// Centralized, opt-in policy for DeepSeek-specific request serialization, error
/// classification, retry heuristics, and guardrails (modeled after deepseek-harness).
/// </summary>
internal static class DeepSeekOptimizationPolicy
{
    public static bool IsDeepSeek(ProviderConfig provider, ModelConfig model) =>
        ReasoningLevelAdapter.IsDeepSeek(provider, model);

    public static bool Applies(LlmRequest request) =>
        request.OptimizeDeepSeek && IsDeepSeek(request.Provider, request.Model);

    public static bool IsThinkingEnabled(LlmRequest request) =>
        request.EnableThinking && request.ReasoningLevel != ReasoningLevel.None;

    /// <summary>
    /// On OpenAI Chat Completions with DeepSeek optimization enabled, pass back
    /// reasoning_content only for assistant turns that contain tool_calls; drop it on
    /// pure text turns to avoid paying for ignored tokens. When disabled, preserve legacy passback.
    /// </summary>
    public static bool ShouldPassBackOpenAiReasoning(LlmRequest request, ChatMessage message)
    {
        if (!IsDeepSeek(request.Provider, request.Model) || string.IsNullOrEmpty(message.Thinking))
            return false;
        return Applies(request) ? message.ToolCalls is { Count: > 0 } : true;
    }

    public static bool ShouldPassBackReasoning(LlmRequest request, ChatMessage message) =>
        ShouldPassBackOpenAiReasoning(request, message);

    /// <summary>
    /// On Anthropic Messages for DeepSeek, replay thinking blocks on ALL non-failed
    /// assistant turns (even when ThinkingSignature is absent) to preserve prefix continuity.
    /// </summary>
    public static bool ShouldPassBackAnthropicThinking(LlmRequest request, ChatMessage message)
    {
        if (message.FinishReason is "error" or "cancelled" || string.IsNullOrEmpty(message.Thinking))
            return false;
        if (Applies(request))
            return true;
        return ShouldKeepThinkingSignature(request, message);
    }

    /// <summary>
    /// Validates that a stored thinking signature came from the active provider/model;
    /// cross-model or legacy signatures are automatically stripped without failing the turn.
    /// </summary>
    public static bool ShouldKeepThinkingSignature(LlmRequest request, ChatMessage message)
    {
        if (string.IsNullOrEmpty(message.ThinkingSignature))
            return false;
        if (string.IsNullOrEmpty(message.ModelRef))
            return true;

        var activeRef = $"{request.Provider.Id}/{request.Model.Id}";
        return string.Equals(message.ModelRef, activeRef, StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.ModelRef, request.Model.Id, StringComparison.OrdinalIgnoreCase);
    }

    public static string ToolResultContent(string text, LlmRequest request) =>
        Applies(request) && string.IsNullOrWhiteSpace(text) ? "(no output)" : text;

    /// <summary>
    /// Top-level thinking toggle wire object: <c>{"type": "enabled" | "disabled"}</c>.
    /// </summary>
    public static JsonObject? ThinkingWire(LlmRequest request)
    {
        if (!Applies(request)) return null;
        return new JsonObject
        {
            ["type"] = IsThinkingEnabled(request) ? "enabled" : "disabled",
        };
    }

    /// <summary>
    /// Anthropic Messages API reasoning effort wire object:
    /// <c>{"effort": "low" | "high" | "max"}</c>, omitted when thinking is disabled.
    /// </summary>
    public static JsonObject? AnthropicOutputConfigWire(LlmRequest request)
    {
        if (!Applies(request) || !IsThinkingEnabled(request))
            return null;

        var effort = request.ReasoningLevel switch
        {
            ReasoningLevel.Low => "low",
            ReasoningLevel.Medium or ReasoningLevel.High => "high",
            ReasoningLevel.Max or ReasoningLevel.XHigh or ReasoningLevel.Ultra => "max",
            _ => "high",
        };
        return new JsonObject { ["effort"] = effort };
    }

    public static void ApplyHeaders(HttpRequestMessage message, LlmRequest request)
    {
        if (!Applies(request)) return;
        if (string.Equals(request.Purpose, "compaction", StringComparison.OrdinalIgnoreCase))
            message.Headers.TryAddWithoutValidation("x-deepseek-harness-compact", "1");
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
            throw new LlmException("DeepSeek returned an empty completion.", retryable: true)
            {
                ErrorCode = "EMPTY_RESPONSE",
            };
        }

        return completion;
    }

    public static string? ClassifyErrorCode(int? statusCode, string? message)
    {
        var msg = message ?? "";
        if (statusCode is 401 or 403
            || msg.Contains("authentication_error", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("permission_error", StringComparison.OrdinalIgnoreCase))
            return "AUTH";

        if (statusCode == 402
            || msg.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("insufficient_balance", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("billing", StringComparison.OrdinalIgnoreCase))
            return "QUOTA";

        if (statusCode == 429
            || msg.Contains("rate_limit_error", StringComparison.OrdinalIgnoreCase))
            return "RATE_LIMIT";

        if (msg.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("context window", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("too many tokens", StringComparison.OrdinalIgnoreCase))
            return "CONTEXT_WINDOW_EXCEEDED";

        if (statusCode is 400 or 413
            || msg.Contains("invalid_request_error", StringComparison.OrdinalIgnoreCase))
            return "INVALID_REQUEST";

        if (statusCode is >= 500
            || msg.Contains("api_error", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("overloaded_error", StringComparison.OrdinalIgnoreCase))
            return "SERVER";

        return null;
    }

    public static TimeSpan? ExtractRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    public static string CanonicalizeArgumentsJson(string rawJson)
    {
        try
        {
            var node = JsonNode.Parse(rawJson);
            if (node is null) return "{}";
            return SortJsonNode(node).ToJsonString();
        }
        catch (JsonException)
        {
            return ToolArguments.Sanitize(rawJson);
        }
    }

    private static JsonNode SortJsonNode(JsonNode node) => node switch
    {
        JsonObject obj => SortJsonObject(obj),
        JsonArray arr => new JsonArray(arr.Select(item => item is null ? null : SortJsonNode(item)).ToArray()),
        _ => node.DeepClone(),
    };

    private static JsonObject SortJsonObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var kv in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            sorted[kv.Key] = kv.Value is null ? null : SortJsonNode(kv.Value);
        return sorted;
    }

    public static string? FormatRepeatToolReminder(string toolName, string canonicalJson, int consecutiveCount)
    {
        if (consecutiveCount == 3)
        {
            return $"\n\n<system-reminder>You have called '{toolName}' with identical arguments {consecutiveCount} times in a row. Verify whether the results are actually changing before repeating this call.</system-reminder>";
        }

        if (consecutiveCount == 5 || consecutiveCount >= 8)
        {
            var preview = canonicalJson.Length > 500
                ? canonicalJson[..500] + "..."
                : canonicalJson;
            return $"\n\n<system-reminder>Repeated identical tool call detected: '{toolName}' was called {consecutiveCount} consecutive times with arguments {preview}. Change your parameters, inspect the underlying files/errors directly, or switch to a different approach instead of repeating the same call.</system-reminder>";
        }

        return null;
    }
}
