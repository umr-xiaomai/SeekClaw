namespace SeekClaw.Runtime.Memory;

/// <summary>A distilled, user-level fact that can be recalled in later conversations.</summary>
public sealed record CrossSessionMemory
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public string? SourceWorkspace { get; init; }
    public string? SourceSessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
