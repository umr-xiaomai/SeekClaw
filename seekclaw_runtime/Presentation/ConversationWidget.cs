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

    private static readonly HashSet<string> ScratchDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmp", ".tmp", "temp", "obj", "bin", ".cache", "node_modules", "__pycache__", ".pytest_cache"
    };

    /// <summary>
    /// Auto-presents HTML files written outside transient scratch or build-output directories
    /// so intermediate helpers (such as PDF print templates under tmp/) do not surface as artifacts.
    /// </summary>
    public static bool ShouldAutoPreview(string path, params string?[] roots)
    {
        if (!IsHtml(path)) return false;

        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception) { return false; }

        var checkedRelative = false;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var fullRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
            var relative = System.IO.Path.GetRelativePath(fullRoot, fullPath);
            if (System.IO.Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                continue;

            checkedRelative = true;
            if (HasScratchDirectorySegment(relative)) return false;
        }

        if (!checkedRelative)
        {
            var systemTemp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (fullPath.StartsWith(systemTemp + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            if (HasScratchDirectorySegment(fullPath))
                return false;
        }

        return true;
    }

    private static bool HasScratchDirectorySegment(string path)
    {
        var segments = path.Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (ScratchDirectoryNames.Contains(segments[i]))
                return true;
        }
        return false;
    }
}
