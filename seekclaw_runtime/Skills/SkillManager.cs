using System.Text.Json;
using System.IO.Compression;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Workspaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SeekClaw.Runtime.Skills;

/// <summary>skill.yaml manifest inside a skill directory.</summary>
public sealed class SkillManifest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Version { get; set; }
    /// <summary>Prompt file relative to the skill directory (default prompt.txt).</summary>
    public string Prompt { get; set; } = "prompt.txt";
}

public sealed record SkillInfo(SkillManifest Manifest, string Directory, bool Enabled)
{
    public string Name => Manifest.Name;
    public string PromptFile => Path.Combine(Directory, Manifest.Prompt);
}

public interface ISkillManager
{
    /// <summary>Rescans ~/.seekclaw/skills and &lt;workspace&gt;/skills. Directory changes apply without restart.</summary>
    IReadOnlyList<SkillInfo> Discover(WorkspaceInfo workspace);

    void SetEnabled(string skillName, bool enabled);
}

/// <summary>
/// Directory-based skills: each skill is a folder with skill.yaml + prompt.txt.
/// Enabled skills contribute their prompt to the composed system prompt each turn.
/// </summary>
public sealed class SkillManager : ISkillManager
{
#pragma warning disable IL2026, IL3050 // YAML manifest parsing uses reflection; acceptable for plugin metadata
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
#pragma warning restore IL2026, IL3050

    private readonly IConfigStore _configStore;
    private readonly string _globalSkillsDir;
    private WorkspaceInfo? _workspace;

    public SkillManager(IConfigStore configStore, IPromptRegistry promptRegistry, string? globalSkillsDir = null)
    {
        _configStore = configStore;
        _globalSkillsDir = globalSkillsDir ?? SeekClawPaths.SkillsDir;
        // One dynamic contribution: enumerates enabled skills at compose time (hot reload for free).
        promptRegistry.Register(new PromptContribution("skills", PromptSlot.Skill, (ctx, _) =>
        {
            if (_workspace is null || !string.Equals(_workspace.Root, ctx.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<string?>(null);

            var parts = Discover(_workspace)
                .Where(s => s.Enabled && File.Exists(s.PromptFile))
                .Select(s => ContextPlanner.FitInjectedText(File.ReadAllText(s.PromptFile).Trim()))
                .Where(text => text.Length > 0)
                .ToList();
            return ValueTask.FromResult<string?>(parts.Count == 0 ? null : string.Join("\n\n", parts));
        }));
    }

    /// <summary>Binds the manager to the current workspace (called during runtime initialization).</summary>
    public void Attach(WorkspaceInfo workspace) => _workspace = workspace;

    public IReadOnlyList<SkillInfo> Discover(WorkspaceInfo workspace)
    {
        var disabledGlobal = _configStore.State.DisabledSkills;
        var disabledWorkspace = workspace.Config?.DisabledSkills ?? [];
        var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { _globalSkillsDir, workspace.SkillsDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifest = LoadManifest(dir);
                if (manifest is null) continue;
                var enabled = !disabledGlobal.ContainsKey(manifest.Name)
                              && !disabledWorkspace.Contains(manifest.Name, StringComparer.OrdinalIgnoreCase);
                // Workspace skills override same-named global skills.
                skills[manifest.Name] = new SkillInfo(manifest, dir, enabled);
            }
        }

        return skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void SetEnabled(string skillName, bool enabled)
    {
        if (enabled)
        {
            _configStore.State.DisabledSkills.Remove(skillName);
            if (_workspace?.Config?.DisabledSkills is { } disabled
                && disabled.RemoveAll(name => name.Equals(skillName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Directory.CreateDirectory(_workspace.SeekClawDir);
                File.WriteAllText(
                    Path.Combine(_workspace.SeekClawDir, "config.json"),
                    JsonSerializer.Serialize(_workspace.Config, SeekClawJsonContext.Default.WorkspaceConfig));
            }
        }
        else
            _configStore.State.DisabledSkills[skillName] = "";
        _configStore.SaveState();
    }

    /// <summary>
    /// Imports one <c>.md</c> or <c>.zip</c> file into the global skills directory.
    /// Markdown files become bare prompt-only skills. ZIP files may contain either one
    /// skill directory at the archive root or one directory per skill; a skill directory
    /// is recognized by <c>skill.yaml</c>/<c>skill.yml</c>/<c>prompt.txt</c>.
    /// </summary>
    public IReadOnlyList<SkillInfo> ImportGlobal(string path, WorkspaceInfo workspace)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Skill import file not found.", fullPath);

        Directory.CreateDirectory(_globalSkillsDir);
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            ImportMarkdown(fullPath);
        else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            ImportZip(fullPath);
        else
            throw new InvalidDataException("Only .md and .zip skill files can be imported.");

        return Discover(workspace);
    }

    private void ImportMarkdown(string markdownFile)
    {
        var name = Path.GetFileNameWithoutExtension(markdownFile);
        var directory = NewSkillDirectory(SanitizeSkillName(name));
        Directory.CreateDirectory(directory);
        File.Copy(markdownFile, Path.Combine(directory, "prompt.txt"), overwrite: false);
        WriteManifest(directory, name, $"Imported from {Path.GetFileName(markdownFile)}");
    }

    private void ImportZip(string zipFile)
    {
        var extractionRoot = Path.Combine(
            Path.GetTempPath(), "seekclaw-skill-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);

        try
        {
            ZipFile.ExtractToDirectory(zipFile, extractionRoot);
            var imported = false;

            if (LooksLikeSkillDirectory(extractionRoot))
            {
                CopySkillDirectory(extractionRoot, Path.GetFileNameWithoutExtension(zipFile));
                imported = true;
            }
            else
            {
                foreach (var directory in Directory.EnumerateDirectories(extractionRoot))
                {
                    if (!LooksLikeSkillDirectory(directory)) continue;
                    CopySkillDirectory(directory, Path.GetFileName(directory));
                    imported = true;
                }

                foreach (var markdown in Directory.EnumerateFiles(extractionRoot, "*.md", SearchOption.TopDirectoryOnly))
                {
                    ImportMarkdown(markdown);
                    imported = true;
                }
            }

            if (!imported)
                throw new InvalidDataException("ZIP did not contain any skill.yaml/prompt.txt skill folders or markdown files.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractionRoot))
                    Directory.Delete(extractionRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void CopySkillDirectory(string source, string fallbackName)
    {
        var manifest = LoadManifest(source);
        var desiredName = !string.IsNullOrWhiteSpace(manifest?.Name)
            ? manifest.Name
            : fallbackName;
        var destination = NewSkillDirectory(SanitizeSkillName(desiredName));
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private string NewSkillDirectory(string name)
    {
        var candidate = Path.Combine(_globalSkillsDir, name);
        if (Directory.Exists(candidate) || File.Exists(candidate))
            throw new InvalidDataException($"A global skill named '{name}' already exists.");
        return candidate;
    }

    private static string SanitizeSkillName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "imported-skill" : sanitized;
    }

    private static bool LooksLikeSkillDirectory(string directory) =>
        File.Exists(Path.Combine(directory, "skill.yaml"))
        || File.Exists(Path.Combine(directory, "skill.yml"))
        || File.Exists(Path.Combine(directory, "prompt.txt"));

    private static void WriteManifest(string directory, string name, string description)
    {
        File.WriteAllText(
            Path.Combine(directory, "skill.yaml"),
            $"name: \"{EscapeYaml(name)}\"\ndescription: \"{EscapeYaml(description)}\"\n");
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static SkillManifest? LoadManifest(string dir)
    {
        var file = new[] { "skill.yaml", "skill.yml" }
            .Select(name => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

        try
        {
            SkillManifest? manifest = null;
            if (file is not null)
                manifest = Yaml.Deserialize<SkillManifest>(File.ReadAllText(file));
            else if (File.Exists(Path.Combine(dir, "prompt.txt")))
                manifest = new SkillManifest(); // bare skill: folder with just a prompt

            if (manifest is null) return null;
            if (string.IsNullOrWhiteSpace(manifest.Name))
                manifest.Name = Path.GetFileName(dir);
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }
}
