using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;

namespace SeekClaw.Runtime.Workspaces;

/// <summary>A detected project workspace with its private SeekClaw state directories.</summary>
public sealed class WorkspaceInfo
{
    public required string Root { get; init; }
    public required IReadOnlyList<string> ProjectKinds { get; init; }
    public WorkspaceConfig? Config { get; init; }
    public bool IsGlobal { get; init; }
    /// <summary>File execution root; a global workspace's Root stores settings, not user artifacts.</summary>
    public string WorkingDirectory => IsGlobal ? Directory.GetCurrentDirectory() : Root;
    public string? ConfigurationRoot { get; init; }
    internal string? ProjectStateDirectory { get; init; }

    public string Name => IsGlobal ? "Global" : Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, '/'));

    public string SeekClawDir => IsGlobal ? Root
        : ProjectStateDirectory ?? WorkspaceManager.GetStateDirectory(SeekClawPaths.Home, ConfigurationRoot ?? Root);
    public string ConfigFile => Path.Combine(SeekClawDir, "config.json");
    public string PromptsDir => Path.Combine(SeekClawDir, "prompts");
    public string MemoryDir => Path.Combine(SeekClawDir, "memory");
    public string MemoryFile => Path.Combine(MemoryDir, "MEMORY.md");
    public string CacheDir => Path.Combine(SeekClawDir, "cache");
    public string SessionsDir => Path.Combine(SeekClawDir, "sessions");
    public string LogsDir => Path.Combine(SeekClawDir, "logs");
    public string SkillsDir => Path.Combine(SeekClawDir, "skills");
    public string McpDir => Path.Combine(SeekClawDir, "mcp");
    public string DocsDir => Path.Combine(SeekClawDir, "docs");
}

public interface IWorkspaceManager
{
    /// <summary>Detects the workspace containing <paramref name="startDirectory"/> (walks up to a project marker).</summary>
    WorkspaceInfo Detect(string? startDirectory = null);

    /// <summary>Creates the directory-free context used by daemon clients for global tasks.</summary>
    WorkspaceInfo CreateGlobal(string? stateRoot = null);

    /// <summary>Creates the workspace state directories in the user profile (seekclaw init).</summary>
    IReadOnlyList<string> Bootstrap(WorkspaceInfo workspace);

    string? LoadMemory(WorkspaceInfo workspace);

    /// <summary>Loads hierarchical AGENTS.md instructions from the workspace root and the current directory chain.</summary>
    string? LoadAgentInstructions(WorkspaceInfo workspace);
}

public sealed class WorkspaceManager : IWorkspaceManager
{
    private static readonly string[] RootMarkers =
        [".git", "package.json", "pyproject.toml", "Cargo.toml", "go.mod"];

    private readonly string _seekClawHome;

    public WorkspaceManager() : this(SeekClawPaths.Home)
    {
    }

    /// <summary>Test seam: simulates a different user profile / SeekClaw state directory.</summary>
    internal WorkspaceManager(string seekClawHome) => _seekClawHome = seekClawHome;

    public WorkspaceInfo Detect(string? startDirectory = null)
    {
        var start = Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory());
        var root = FindRoot(start) ?? start;
        var stateDirectory = GetStateDirectory(_seekClawHome, root);
        return new WorkspaceInfo
        {
            Root = root,
            ProjectKinds = DetectKinds(root),
            ProjectStateDirectory = stateDirectory,
            Config = LoadWorkspaceConfig(Path.Combine(stateDirectory, "config.json")),
        };
    }

    public WorkspaceInfo CreateGlobal(string? stateRoot = null) => new()
    {
        Root = Path.GetFullPath(stateRoot ?? _seekClawHome),
        ProjectKinds = [],
        Config = null,
        IsGlobal = true,
    };

    private string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (RootMarkers.Any(marker => Directory.Exists(Path.Combine(dir.FullName, marker))
                    || File.Exists(Path.Combine(dir.FullName, marker))))
                return dir.FullName;

            if (HasFiles(dir, "*.sln") || HasFiles(dir, "*.slnx"))
                return dir.FullName;
        }
        return null;
    }

    internal static string GetStateDirectory(string stateRoot, string workspaceRoot)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SeekClawDatabase.PathKey(workspaceRoot))));
        return Path.Combine(stateRoot, "workspaces", key);
    }

    private static List<string> DetectKinds(string root)
    {
        var kinds = new List<string>();
        void AddIf(bool condition, string kind) { if (condition) kinds.Add(kind); }

        AddIf(Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")), "git");
        AddIf(HasFiles(root, "*.sln")
              || HasFiles(root, "*.slnx")
              || HasFiles(root, "*.csproj")
              || HasChildFiles(root, "*.csproj"), "dotnet");
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

    private static bool HasFiles(DirectoryInfo directory, string pattern)
    {
        try { return directory.EnumerateFiles(pattern).Any(); }
        catch (UnauthorizedAccessException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
    }

    private static bool HasFiles(string directory, string pattern) =>
        HasFiles(new DirectoryInfo(directory), pattern);

    private static bool HasChildFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Any(directory => HasFiles(new DirectoryInfo(directory), pattern));
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
    }

    private static WorkspaceConfig? LoadWorkspaceConfig(string file)
    {
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
                     workspace.CacheDir, workspace.LogsDir,
                     workspace.SkillsDir, workspace.McpDir, workspace.DocsDir,
                 })
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                created.Add(dir);
            }
        }

        return created;
    }

    public string? LoadMemory(WorkspaceInfo workspace) =>
        File.Exists(workspace.MemoryFile) ? File.ReadAllText(workspace.MemoryFile) : null;

    public string? LoadAgentInstructions(WorkspaceInfo workspace)
    {
        if (workspace.IsGlobal) return null;

        var rootPath = Path.GetFullPath(workspace.Root);
        var rootInstructions = Path.Combine(rootPath, "AGENTS.md");
        var files = new List<(string Path, string Text)>();

        if (File.Exists(rootInstructions))
        {
            try
            {
                files.Add((Path.GetRelativePath(rootPath, rootInstructions), File.ReadAllText(rootInstructions)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (cwd.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var current = cwd;
            while (!string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(current, "AGENTS.md");
                if (File.Exists(candidate) && !string.Equals(candidate, rootInstructions, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var relativePath = Path.GetRelativePath(rootPath, candidate);
                        // Deeper AGENTS.md files take precedence; order them closer to the working directory.
                        files.Insert(0, (relativePath, File.ReadAllText(candidate)));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                var parent = Path.GetDirectoryName(current);
                if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        if (files.Count == 0) return null;

        var builder = new StringBuilder();
        foreach (var (path, text) in files)
        {
            builder.Append("## ").Append(path).AppendLine();
            builder.AppendLine(text.Trim());
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
