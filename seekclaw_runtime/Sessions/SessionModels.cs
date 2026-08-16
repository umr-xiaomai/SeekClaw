using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Sessions;

/// <summary>Persisted session metadata.</summary>
public sealed class SessionHeader
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Workspace { get; set; }
    public bool Archived { get; set; }
    public ReasoningLevel ReasoningLevel { get; set; } = ReasoningLevel.High;
    /// <summary>Per-session "联网" toggle; controls web_search + web_fetch together.</summary>
    public bool NetworkEnabled { get; set; } = true;
    public long LlmRounds { get; set; }
    public long ExecutionSteps { get; set; }
    public long InputTokens { get; set; }
    public long TotalInputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long OutputElapsedMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Cumulative usage recorded for one agent turn.</summary>
public sealed record SessionUsage
{
    public long LlmRounds { get; init; }
    public long ExecutionSteps { get; init; }
    public long InputTokens { get; init; }
    public long TotalInputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long OutputElapsedMs { get; init; }
}

/// <summary>Provider-neutral conversation payload persisted as JSON inside SQLite.</summary>
public sealed class SessionMessage
{
    /// <summary>user | assistant | tool</summary>
    public string Role { get; set; } = "user";
    public string? Text { get; set; }
    public List<SessionImage>? Images { get; set; }
    public string? Thinking { get; set; }
    public string? ModelRef { get; set; }
    public List<SessionImageReference>? ViewedImages { get; set; }
    public List<SessionToolCall>? ToolCalls { get; set; }
    /// <summary>Set when Role == "tool".</summary>
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public bool? ToolSuccess { get; set; }
    public string? ToolDiff { get; set; }
    public string? ToolFilePath { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SessionImage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Data { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class SessionImageReference
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class SessionToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
}
