namespace SeekClaw.Runtime.Sessions;

/// <summary>First line of a session .jsonl file.</summary>
public sealed class SessionHeader
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Workspace { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One conversation message, persisted as one JSONL line.</summary>
public sealed class SessionMessage
{
    /// <summary>user | assistant | tool</summary>
    public string Role { get; set; } = "user";
    public string? Text { get; set; }
    public string? Thinking { get; set; }
    public List<SessionToolCall>? ToolCalls { get; set; }
    /// <summary>Set when Role == "tool".</summary>
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public bool? ToolSuccess { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SessionToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
}
