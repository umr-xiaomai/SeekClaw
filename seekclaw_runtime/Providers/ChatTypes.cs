using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>Provider-neutral conversation message used by the agent loop.</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string Text { get; set; } = "";
    public string? Thinking { get; set; }
    public IReadOnlyList<ToolCallRequest>? ToolCalls { get; set; }
    /// <summary>Set when Role == Tool.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool ToolSuccess { get; init; } = true;

    public static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };
    public static ChatMessage Assistant(string text) => new() { Role = ChatRole.Assistant, Text = text };
    public static ChatMessage ToolResult(string callId, string toolName, string text, bool success) =>
        new() { Role = ChatRole.Tool, ToolCallId = callId, ToolName = toolName, Text = text, ToolSuccess = success };
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
    public long Total => InputTokens + OutputTokens;
}

/// <summary>Thrown by LLM clients for transport / API errors, carrying retryability info.</summary>
public sealed class LlmException(string message, int? statusCode = null, bool retryable = true, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;
}
