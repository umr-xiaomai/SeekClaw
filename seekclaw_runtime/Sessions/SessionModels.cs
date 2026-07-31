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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Provider-neutral conversation payload persisted as JSON inside SQLite.</summary>
public sealed class SessionMessage
{
    /// <summary>user | assistant | tool</summary>
    public string Role { get; set; } = "user";
    public string? Text { get; set; }
    public List<SessionImage>? Images { get; set; }
    public string? Thinking { get; set; }
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
