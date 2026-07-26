using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Tests;

public sealed class PromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-tests", Guid.NewGuid().ToString("N"));

    public PromptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeRoot(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Render_ReplacesKnownVariables_KeepsUnknown()
    {
        using var provider = new FilePromptProvider([MakeRoot("a")]);
        var result = provider.Render(
            "Hello {{name}}, os={{os}}, missing={{nope}}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "world", ["os"] = "win" });

        Assert.Equal("Hello world, os=win, missing={{nope}}", result);
    }

    [Fact]
    public void TryGet_ResolvesFromRoot_AndCaches()
    {
        var root = MakeRoot("prompts");
        Directory.CreateDirectory(Path.Combine(root, "system"));
        File.WriteAllText(Path.Combine(root, "system", "default.txt"), "MAIN PROMPT");

        using var provider = new FilePromptProvider([root]);
        Assert.Equal("MAIN PROMPT", provider.TryGet("system/default"));
        Assert.Null(provider.TryGet("system/missing"));
    }

    [Fact]
    public void WorkspaceRoot_TakesPriorityOverDefaults()
    {
        var appRoot = MakeRoot("app");
        Directory.CreateDirectory(Path.Combine(appRoot, "system"));
        File.WriteAllText(Path.Combine(appRoot, "system", "default.txt"), "DEFAULT");

        var workspaceRoot = MakeRoot("workspace-prompts");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "system"));
        File.WriteAllText(Path.Combine(workspaceRoot, "system", "default.txt"), "WORKSPACE");

        using var provider = new FilePromptProvider([appRoot]);
        Assert.Equal("DEFAULT", provider.TryGet("system/default"));

        provider.SetWorkspaceRoot(workspaceRoot);
        Assert.Equal("WORKSPACE", provider.TryGet("system/default"));

        provider.SetWorkspaceRoot(null);
        Assert.Equal("DEFAULT", provider.TryGet("system/default"));
    }

    [Fact]
    public async Task Composer_OrdersContributionsBySlot_AndRendersVariables()
    {
        using var provider = new FilePromptProvider([MakeRoot("empty")]);
        var registry = new PromptRegistry();
        registry.Register(new PromptContribution("mem", PromptSlot.Memory,
            (_, _) => ValueTask.FromResult<string?>("MEMORY {{project}}")));
        registry.Register(new PromptContribution("sys", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("SYSTEM")));
        registry.Register(new PromptContribution("skill", PromptSlot.Skill,
            (_, _) => ValueTask.FromResult<string?>("SKILL")));

        var composer = new PromptComposer(provider, registry);
        var result = await composer.ComposeAsync(new PromptRenderContext
        {
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["project"] = "demo" },
        });

        Assert.Equal("SYSTEM\n\nSKILL\n\nMEMORY demo", result);
    }

    [Fact]
    public void Registry_Unregister_RemovesContribution()
    {
        var registry = new PromptRegistry();
        var registration = registry.Register(new PromptContribution("x", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>("X")));
        Assert.Single(registry.All);
        registration.Dispose();
        Assert.Empty(registry.All);
    }
}
