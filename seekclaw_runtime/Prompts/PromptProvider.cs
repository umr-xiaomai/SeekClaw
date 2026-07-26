using System.Collections.Concurrent;
using System.Text;

namespace SeekClaw.Runtime.Prompts;

/// <summary>
/// Loads prompt text from .txt files, with caching, {{variable}} substitution and hot reload.
/// Every module must go through this provider — no prompt strings in C# source.
/// </summary>
public interface IPromptProvider
{
    /// <summary>Resolves a key like "system/default" against workspace → user → app prompt roots.</summary>
    string? TryGet(string key);

    /// <summary>Like <see cref="TryGet"/> but throws a descriptive error when the file is missing.</summary>
    string Get(string key);

    /// <summary>Replaces {{name}} placeholders; unknown placeholders are left intact.</summary>
    string Render(string template, IReadOnlyDictionary<string, string> variables);

    string? GetRendered(string key, IReadOnlyDictionary<string, string> variables);

    /// <summary>Points the highest-priority root at &lt;workspace&gt;/.seekclaw/prompts.</summary>
    void SetWorkspaceRoot(string? promptsDir);
}

public sealed class FilePromptProvider : IPromptProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Root, FileSystemWatcher? Watcher)> _roots = [];
    private readonly object _gate = new();
    private string? _workspaceRoot;

    public FilePromptProvider(IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? DefaultRoots())
            AddRoot(root);
    }

    private static IEnumerable<string> DefaultRoots() =>
    [
        Configuration.SeekClawPaths.PromptsDir,
        Path.Combine(Configuration.SeekClawPaths.AppDir, "prompts"),
    ];

    public void SetWorkspaceRoot(string? promptsDir)
    {
        lock (_gate)
        {
            _workspaceRoot = promptsDir is not null && Directory.Exists(promptsDir) ? promptsDir : null;
            _cache.Clear();
        }
    }

    public string? TryGet(string key)
    {
        var cacheKey = $"{_workspaceRoot}|{key}";
        return _cache.GetOrAdd(cacheKey, _ => Load(key));
    }

    public string Get(string key) =>
        TryGet(key) ?? throw new FileNotFoundException(
            $"Prompt '{key}' not found. Expected '{key}.txt' under a prompts/ directory " +
            "(workspace .seekclaw/prompts, ~/.seekclaw/prompts, or the application's prompts folder).");

    public string Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (template.Length == 0 || !template.Contains("{{")) return template;

        var result = new StringBuilder(template.Length + 64);
        var position = 0;
        while (true)
        {
            var open = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0) break;
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) break;

            result.Append(template, position, open - position);
            var name = template[(open + 2)..close].Trim();
            if (variables.TryGetValue(name, out var value))
                result.Append(value);
            else
                result.Append(template, open, close + 2 - open); // keep unknown placeholder

            position = close + 2;
        }
        result.Append(template, position, template.Length - position);
        return result.ToString();
    }

    public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables)
    {
        var text = TryGet(key);
        return text is null ? null : Render(text, variables);
    }

    private string? Load(string key)
    {
        foreach (var root in EnumerateRoots())
        {
            var file = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar) + ".txt");
            if (File.Exists(file))
                return File.ReadAllText(file);
        }
        return null;
    }

    private IEnumerable<string> EnumerateRoots()
    {
        lock (_gate)
        {
            var roots = new List<string>();
            if (_workspaceRoot is not null) roots.Add(_workspaceRoot);
            roots.AddRange(_roots.Select(r => r.Root));
            return roots;
        }
    }

    private void AddRoot(string root)
    {
        FileSystemWatcher? watcher = null;
        if (Directory.Exists(root))
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                Filter = "*.txt",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            // Any change invalidates the whole cache: prompt files are tiny, reload is cheap.
            watcher.Changed += (_, _) => _cache.Clear();
            watcher.Created += (_, _) => _cache.Clear();
            watcher.Deleted += (_, _) => _cache.Clear();
            watcher.Renamed += (_, _) => _cache.Clear();
            watcher.EnableRaisingEvents = true;
        }
        _roots.Add((root, watcher));
    }

    public void Dispose()
    {
        foreach (var (_, watcher) in _roots) watcher?.Dispose();
    }
}
