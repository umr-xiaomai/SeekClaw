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
    private WorkspaceInfo? _workspace;

    public SkillManager(IConfigStore configStore, IPromptRegistry promptRegistry)
    {
        _configStore = configStore;
        // One dynamic contribution: enumerates enabled skills at compose time (hot reload for free).
        promptRegistry.Register(new PromptContribution("skills", PromptSlot.Skill, (ctx, _) =>
        {
            if (_workspace is null || !string.Equals(_workspace.Root, ctx.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<string?>(null);

            var parts = Discover(_workspace)
                .Where(s => s.Enabled && File.Exists(s.PromptFile))
                .Select(s => File.ReadAllText(s.PromptFile).Trim())
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

        foreach (var root in new[] { SeekClawPaths.SkillsDir, workspace.SkillsDir })
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
            _configStore.State.DisabledSkills.Remove(skillName);
        else
            _configStore.State.DisabledSkills[skillName] = "";
        _configStore.SaveState();
    }

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
