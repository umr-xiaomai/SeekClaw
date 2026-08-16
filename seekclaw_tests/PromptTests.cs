using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Agents;

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

    [Fact]
    public void CapabilityInstruction_TellsVisionModelsToUseAttachedImages()
    {
        Assert.Equal("", PromptVariables.BuildCapabilityInstruction(vision: false));

        var visionPrompt = PromptVariables.BuildCapabilityInstruction(vision: true);
        Assert.Contains("MULTIMODAL", visionPrompt);
        Assert.Contains("Image attachments in user messages are available", visionPrompt);
        Assert.Contains("Do not say that you lack a vision encoder", visionPrompt);
        Assert.Contains("provider explicitly exposes another output modality", visionPrompt);

        var imageOutputPrompt = PromptVariables.BuildCapabilityInstruction(vision: true, imageOutput: true);
        Assert.Contains("may also expose image output", imageOutputPrompt);
    }

    [Fact]
    public void PromptVariables_Build_IncludesRuntimePermissionContext()
    {
        var variables = PromptVariables.Build(
            workspace: null,
            model: null,
            toolNames: ["read_file"],
            memory: "memory",
            mode: "plan",
            networkEnabled: false,
            autoVerify: false,
            personality: "friendly");

        Assert.Equal("plan", variables["mode"]);
        Assert.Equal("disabled", variables["network"]);
        Assert.Equal("read_only", variables["sandbox_mode"]);
        Assert.Equal("never", variables["approval_policy"]);
        Assert.Equal("false", variables["auto_verify"]);
        Assert.Equal("friendly", variables["personality"]);
    }

    [Fact]
    public void FitInjectedText_BoundsLongFragments()
    {
        var text = new string('a', 20_000);
        var fit = ContextPlanner.FitInjectedText(text, maxTokens: 100);

        Assert.True(ContextPlanner.EstimateTokens(fit) <= 120);
        Assert.Contains("middle section trimmed", fit);
        Assert.StartsWith(new string('a', 200), fit);
    }
}
