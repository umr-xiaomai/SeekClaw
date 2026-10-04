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

    [Fact]
    public async Task Calibration_Disabled_ProducesByteIdenticalPromptToLegacy()
    {
        var root = MakeRoot("prompts");
        Directory.CreateDirectory(Path.Combine(root, "system"));
        File.WriteAllText(Path.Combine(root, "system", "default.txt"), "SYSTEM DEFAULT");
        File.WriteAllText(Path.Combine(root, "system", "permissions.txt"), "PERMISSIONS");
        File.WriteAllText(Path.Combine(root, "system", "calibration.txt"), "CALIBRATION EXAMPLES");

        using var provider = new FilePromptProvider([root]);

        // Legacy registry without calibration contribution
        var legacyRegistry = new PromptRegistry();
        legacyRegistry.Register(new PromptContribution("system", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/default"))));
        legacyRegistry.Register(new PromptContribution("permissions", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/permissions"))));

        // Registry with calibration contribution disabled (returns null)
        var newRegistry = new PromptRegistry();
        newRegistry.Register(new PromptContribution("system", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/default"))));
        newRegistry.Register(new PromptContribution("permissions", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/permissions"))));
        newRegistry.Register(new PromptContribution("calibration", PromptSlot.System,
            (_, _) => ValueTask.FromResult<string?>(null))); // disabled

        var composerLegacy = new PromptComposer(provider, legacyRegistry);
        var composerNew = new PromptComposer(provider, newRegistry);

        var context = new PromptRenderContext
        {
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var legacyPrompt = await composerLegacy.ComposeAsync(context);
        var newPrompt = await composerNew.ComposeAsync(context);

        Assert.Equal(legacyPrompt, newPrompt);
        Assert.Equal("SYSTEM DEFAULT\n\nPERMISSIONS", newPrompt);
    }

    [Fact]
    public async Task Calibration_Enabled_AppendsCalibrationBlockInSystemSlot()
    {
        var root = MakeRoot("prompts");
        Directory.CreateDirectory(Path.Combine(root, "system"));
        Directory.CreateDirectory(Path.Combine(root, "developer"));
        File.WriteAllText(Path.Combine(root, "system", "default.txt"), "SYSTEM DEFAULT");
        File.WriteAllText(Path.Combine(root, "system", "permissions.txt"), "PERMISSIONS");
        File.WriteAllText(Path.Combine(root, "system", "calibration.txt"), "CALIBRATION EXAMPLES");
        File.WriteAllText(Path.Combine(root, "developer", "dotnet.txt"), "DEVELOPER DOTNET");

        using var provider = new FilePromptProvider([root]);

        var registry = new PromptRegistry();
        registry.Register(new PromptContribution("system", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/default"))));
        registry.Register(new PromptContribution("permissions", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/permissions"))));
        registry.Register(new PromptContribution("calibration", PromptSlot.System,
            (_, _) => ValueTask.FromResult(provider.TryGet("system/calibration"))));
        registry.Register(new PromptContribution("developer", PromptSlot.Developer,
            (_, _) => ValueTask.FromResult(provider.TryGet("developer/dotnet"))));

        var composer = new PromptComposer(provider, registry);
        var context = new PromptRenderContext
        {
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var prompt = await composer.ComposeAsync(context);

        var expected = "SYSTEM DEFAULT\n\nPERMISSIONS\n\nCALIBRATION EXAMPLES\n\nDEVELOPER DOTNET";
        Assert.Equal(expected, prompt);

        // Verify slot ordering: permissions < calibration < developer
        var permIndex = prompt.IndexOf("PERMISSIONS", StringComparison.Ordinal);
        var calIndex = prompt.IndexOf("CALIBRATION EXAMPLES", StringComparison.Ordinal);
        var devIndex = prompt.IndexOf("DEVELOPER DOTNET", StringComparison.Ordinal);

        Assert.True(permIndex < calIndex);
        Assert.True(calIndex < devIndex);
    }

    [Fact]
    public void CalibrationExamples_OnlyReferenceRegisteredTools()
    {
        var shippedCalibrationFile = Path.Combine(AppContext.BaseDirectory, "prompts", "system", "calibration.txt");
        if (!File.Exists(shippedCalibrationFile))
        {
            // Fallback to project root if running from alternate working directory
            shippedCalibrationFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "seekclaw_cli", "prompts", "system", "calibration.txt"));
        }

        Assert.True(File.Exists(shippedCalibrationFile), $"Calibration file not found at {shippedCalibrationFile}");
        var content = File.ReadAllText(shippedCalibrationFile);

        var validToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "read_file",
            "write_file",
            "edit_file",
            "list_dir",
            "glob",
            "grep",
            "bash",
            "web_search",
            "web_fetch",
            "capture_screen",
            "update_plan",
            "computer_inspect",
            "computer",
        };

        var matches = System.Text.RegularExpressions.Regex.Matches(content, @"`([a-z_]+)`");
        var checkedTools = new HashSet<string>();
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var identifier = match.Groups[1].Value;
            if (identifier.EndsWith("_file", StringComparison.Ordinal) ||
                identifier.EndsWith("_dir", StringComparison.Ordinal) ||
                identifier.EndsWith("_search", StringComparison.Ordinal) ||
                identifier.EndsWith("_fetch", StringComparison.Ordinal) ||
                identifier.EndsWith("_screen", StringComparison.Ordinal) ||
                identifier.EndsWith("_plan", StringComparison.Ordinal) ||
                identifier.EndsWith("_inspect", StringComparison.Ordinal) ||
                identifier is "bash" or "grep" or "glob" or "computer")
            {
                Assert.Contains(identifier, validToolNames);
                checkedTools.Add(identifier);
            }
        }

        // Must at least check the core tools used in the calibration examples
        Assert.Contains("read_file", checkedTools);
        Assert.Contains("edit_file", checkedTools);
        Assert.Contains("grep", checkedTools);
        Assert.Contains("bash", checkedTools);
        Assert.Contains("update_plan", checkedTools);
    }
}
