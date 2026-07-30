using System.Text.Json;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Workspaces;

/// <summary>A detected project workspace with its private SeekClaw state directories.</summary>
public sealed class WorkspaceInfo
{
    public required string Root { get; init; }
    public required IReadOnlyList<string> ProjectKinds { get; init; }
    public WorkspaceConfig? Config { get; init; }
    public bool IsGlobal { get; init; }

    public string Name => IsGlobal ? "Global" : Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, '/'));

    public string SeekClawDir => IsGlobal ? Root : Path.Combine(Root, ".seekclaw");
    public string PromptsDir => Path.Combine(SeekClawDir, "prompts");
    public string MemoryDir => Path.Combine(SeekClawDir, "memory");
    public string MemoryFile => Path.Combine(MemoryDir, "MEMORY.md");
    public string CacheDir => Path.Combine(SeekClawDir, "cache");
    public string SessionsDir => IsGlobal
        ? Path.Combine(Root, "sessions")
        : Directory.Exists(Path.Combine(Root, ".session")) ? Path.Combine(Root, ".session") : Path.Combine(SeekClawDir, "sessions");
    public string LogsDir => Path.Combine(SeekClawDir, "logs");
    public string SkillsDir => Directory.Exists(Path.Combine(Root, "skills")) ? Path.Combine(Root, "skills") : Path.Combine(SeekClawDir, "skills");
    public string McpDir => Directory.Exists(Path.Combine(Root, "mcp")) ? Path.Combine(Root, "mcp") : Path.Combine(SeekClawDir, "mcp");
    public string DocsDir => Directory.Exists(Path.Combine(Root, "docs")) ? Path.Combine(Root, "docs") : Path.Combine(SeekClawDir, "docs");
}

public interface IWorkspaceManager
{
    /// <summary>Detects the workspace containing <paramref name="startDirectory"/> (walks up to a project marker).</summary>
    WorkspaceInfo Detect(string? startDirectory = null);

    /// <summary>Creates the directory-free context used by daemon clients for global tasks.</summary>
    WorkspaceInfo CreateGlobal(string? stateRoot = null);

    /// <summary>Creates the standard workspace directories and .gitignore entries (seekclaw init).</summary>
    IReadOnlyList<string> Bootstrap(WorkspaceInfo workspace);

    string? LoadMemory(WorkspaceInfo workspace);
}

public sealed class WorkspaceManager : IWorkspaceManager
{
    private static readonly string[] RootMarkers =
        [".git", ".seekclaw", "package.json", "pyproject.toml", "Cargo.toml", "go.mod"];

    public WorkspaceInfo Detect(string? startDirectory = null)
    {
        var start = Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory());
        var root = FindRoot(start) ?? start;
        return new WorkspaceInfo
        {
            Root = root,
            ProjectKinds = DetectKinds(root),
            Config = LoadWorkspaceConfig(root),
        };
    }

    public WorkspaceInfo CreateGlobal(string? stateRoot = null) => new()
    {
        Root = Path.GetFullPath(stateRoot ?? SeekClawPaths.Home),
        ProjectKinds = [],
        Config = null,
        IsGlobal = true,
    };

    private static string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (RootMarkers.Any(marker =>
                    Directory.Exists(Path.Combine(dir.FullName, marker)) ||
                    File.Exists(Path.Combine(dir.FullName, marker))))
                return dir.FullName;

            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
        }
        return null;
    }

    private static List<string> DetectKinds(string root)
    {
        var kinds = new List<string>();
        void AddIf(bool condition, string kind) { if (condition) kinds.Add(kind); }

        AddIf(Directory.Exists(Path.Combine(root, ".git")), "git");
        AddIf(Directory.EnumerateFiles(root, "*.sln").Any()
              || Directory.EnumerateFiles(root, "*.slnx").Any()
              || Directory.EnumerateFiles(root, "*.csproj").Any()
              || Directory.EnumerateDirectories(root).Any(d => Directory.EnumerateFiles(d, "*.csproj").Any()), "dotnet");
        AddIf(File.Exists(Path.Combine(root, "package.json")), "node");
        AddIf(File.Exists(Path.Combine(root, "pyproject.toml"))
              || File.Exists(Path.Combine(root, "requirements.txt"))
              || File.Exists(Path.Combine(root, "setup.py")), "python");
        AddIf(File.Exists(Path.Combine(root, "Cargo.toml")), "rust");
        AddIf(File.Exists(Path.Combine(root, "go.mod")), "go");
        AddIf(Directory.Exists(Path.Combine(root, "Assets"))
              && Directory.Exists(Path.Combine(root, "ProjectSettings")), "unity");

        // Vue projects get their own developer prompt on top of node.
        var packageJson = Path.Combine(root, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                if (File.ReadAllText(packageJson).Contains("\"vue\"", StringComparison.OrdinalIgnoreCase))
                    kinds.Add("vue");
            }
            catch (IOException) { }
        }

        return kinds;
    }

    private static WorkspaceConfig? LoadWorkspaceConfig(string root)
    {
        var file = Path.Combine(root, ".seekclaw", "config.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), SeekClawJsonContext.Default.WorkspaceConfig);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> Bootstrap(WorkspaceInfo workspace)
    {
        var created = new List<string>();
        foreach (var dir in new[]
                 {
                     workspace.SeekClawDir, workspace.PromptsDir, workspace.MemoryDir,
                     workspace.CacheDir, workspace.SessionsDir, workspace.LogsDir,
                     workspace.SkillsDir, workspace.McpDir, workspace.DocsDir,
                 })
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                created.Add(Path.GetRelativePath(workspace.Root, dir));
            }
        }

        var gitignore = Path.Combine(workspace.Root, ".gitignore");
        var required = new[] { ".seekclaw/", ".session/", ".cache/", "logs/" };
        var existing = File.Exists(gitignore) ? File.ReadAllLines(gitignore).ToList() : [];
        var missing = required.Where(entry => !existing.Any(line => line.Trim() == entry || line.Trim() == entry.TrimEnd('/'))).ToList();
        if (missing.Count > 0)
        {
            if (existing.Count > 0 && existing[^1].Length > 0) existing.Add("");
            existing.Add("# SeekClaw");
            existing.AddRange(missing);
            File.WriteAllLines(gitignore, existing);
            created.Add(".gitignore (updated)");
        }

        return created;
    }

    public string? LoadMemory(WorkspaceInfo workspace) =>
        File.Exists(workspace.MemoryFile) ? File.ReadAllText(workspace.MemoryFile) : null;
}
