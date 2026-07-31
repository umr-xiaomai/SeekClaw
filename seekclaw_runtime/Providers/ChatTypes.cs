using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>Provider-neutral inline image attached to a user message.</summary>
public sealed record ChatImageAttachment(
    string Id,
    string Name,
    string MediaType,
    string Data,
    long SizeBytes);

/// <summary>Reference recorded on the assistant message that consumed an uploaded image.</summary>
public sealed record ChatImageReference(string Id, string Name);

/// <summary>Provider-neutral conversation message used by the agent loop.</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string Text { get; set; } = "";
    public IReadOnlyList<ChatImageAttachment>? Images { get; init; }
    public string? Thinking { get; set; }
    public IReadOnlyList<ChatImageReference>? ViewedImages { get; init; }
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; set; }
    /// <summary>Set when Role == Tool.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool ToolSuccess { get; init; } = true;
    /// <summary>Unified diff produced by a mutating tool result.</summary>
    public string? ToolDiff { get; init; }
    /// <summary>Workspace-relative file path associated with ToolDiff.</summary>
    public string? ToolFilePath { get; init; }

    public static ChatMessage User(string text, IReadOnlyList<ChatImageAttachment>? images = null) => new()
    {
        Role = ChatRole.User,
        Text = text,
        Images = images is { Count: > 0 } ? images : null,
    };
    public static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Text = text };
    public static ChatMessage ToolResult(
        string callId,
        string toolName,
        string text,
        bool success,
        string? diff = null,
        string? filePath = null) =>
        new()
        {
            Role = ChatRole.Tool,
            ToolCallId = callId,
            ToolName = toolName,
            Text = text,
            ToolSuccess = success,
            ToolDiff = diff,
            ToolFilePath = filePath,
        };
}

public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>Provider-neutral tool definition; Parameters is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record LlmRequest
{
    public required ProviderConfig Provider { get; init; }
    public required ModelConfig Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public string? System { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public bool EnableThinking { get; init; }
    public int ThinkingBudgetTokens { get; init; } = 4096;
    public ReasoningLevel ReasoningLevel { get; init; } = ReasoningLevel.Medium;
}

// ---------------------------------------------------------------- stream events

public abstract record LlmStreamEvent;

public sealed record LlmTextDelta(string Text) : LlmStreamEvent;

public sealed record LlmThinkingDelta(string Text) : LlmStreamEvent;

/// <summary>Emitted as soon as the model starts a tool call (arguments may still be streaming).</summary>
public sealed record LlmToolCallStarted(string Id, string Name) : LlmStreamEvent;

/// <summary>Terminal event carrying the fully accumulated completion.</summary>
public sealed record LlmCompleted(LlmCompletion Completion) : LlmStreamEvent;

public sealed record LlmCompletion
{
    public string Text { get; init; } = "";
    public string Thinking { get; init; } = "";
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = [];
    public TokenUsage Usage { get; init; } = new(0, 0);
    public string FinishReason { get; init; } = "";
}

public sealed record TokenUsage(long InputTokens, long OutputTokens)
{
    /// <summary>
    /// Total prompt tokens before cache discounts. Some providers report cached tokens outside
    /// InputTokens, so consumers should use this value when calculating a cache hit rate.
    /// </summary>
    public long TotalInputTokens { get; init; } = InputTokens;
    /// <summary>Input tokens served from the provider's prompt/context cache.</summary>
    public long CachedInputTokens { get; init; }
    /// <summary>Input tokens written to a provider cache, when reported by the provider.</summary>
    public long CacheCreationInputTokens { get; init; }
    public long Total => TotalInputTokens + OutputTokens;
}

/// <summary>Thrown by LLM clients for transport / API errors, carrying retryability info.</summary>
public sealed class LlmException(string message, int? statusCode = null, bool retryable = true, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;
}
