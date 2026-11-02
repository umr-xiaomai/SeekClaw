using System.Text.Json.Serialization;
using SeekClaw.Runtime.Scheduling;

namespace SeekClaw.Runtime.Presentation;

/// <summary>Declarative, versioned UI data. Clients own the allowed actions and rendering.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ScheduleWidget), "schedule")]
[JsonDerivedType(typeof(ArtifactWidget), "artifact")]
public abstract record ConversationWidget(string Id, string Title)
{
    public int Version { get; init; } = 1;
}

public sealed record ScheduleWidget(string Id, string Title, ScheduledTask Task, bool Deleted = false)
    : ConversationWidget(Id, Title);

public sealed record ArtifactWidget(string Id, string Title, string Path, string MediaType, string? Description = null)
    : ConversationWidget(Id, Title)
{
    /// <summary>Trusted tool-side execution root, retained for global artifacts across daemon restarts.</summary>
    public string? ExecutionRoot { get; init; }

    public static ArtifactWidget FromFile(string path, string? title = null, string? description = null,
        string? executionRoot = null) => new(
        "artifact:" + path,
        string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileName(path) : title.Trim(),
        path,
        IsHtml(path) ? "text/html" : "application/octet-stream",
        description)
    {
        ExecutionRoot = executionRoot is null ? null : System.IO.Path.GetFullPath(executionRoot),
    };

    public static bool IsHtml(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";
}
