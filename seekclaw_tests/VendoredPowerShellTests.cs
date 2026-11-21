using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Verification;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class VendoredPowerShellTests
{
    private static readonly string Checkout = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "source checkout");
    private static readonly string AppDirectory = Path.Combine(Checkout, "seekclaw_cli", "bin", "net10.0");
    private static readonly string Packaged = Path.Combine(AppDirectory, "powershell", "win-x64", "pwsh.exe");
    private static readonly string RepoCopy = Path.Combine(Checkout, "seekclaw_third_party", "powershell", "win-x64", "pwsh.exe");
    private static readonly string PromptsRoot = Path.Combine(AppContext.BaseDirectory, "prompts");
    private static readonly WorkspaceInfo Workspace = new() { Root = AppContext.BaseDirectory, ProjectKinds = [] };
    // Check the path within PowerShell so ConPTY line wrapping cannot split the value.
    private static string Probe()
    {
        var expected = VendoredPowerShell.ResolveExecutable().Replace("'", "''", StringComparison.Ordinal);
        return "[Console]::WriteLine('version=' + $PSVersionTable.PSVersion.Major); " +
            $"[Console]::WriteLine('vendored-shell=' + ([Environment]::ProcessPath -eq '{expected}')); " +
            "[Console]::WriteLine('shell=' + [Environment]::ProcessPath)";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveExecutable_PrefersPackagedCopyThenSearchesAppAncestors(bool hasPackagedCopy)
    {
        var path = VendoredPowerShell.ResolveExecutable(AppDirectory,
            candidate => candidate == RepoCopy || (hasPackagedCopy && candidate == Packaged));

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(hasPackagedCopy ? Packaged : RepoCopy, path);
    }

    [Fact]
    public void ResolveExecutable_ReportsMissingPayloadAndOnlyChecksTrustedLocations()
    {
        var checkedPaths = new List<string>();
        var error = Assert.Throws<FileNotFoundException>(() => VendoredPowerShell.ResolveExecutable(AppDirectory, path =>
        {
            checkedPaths.Add(path);
            return false;
        }));

        Assert.Equal(Packaged, error.FileName);
        Assert.Contains("Reinstall", error.Message);
        Assert.Contains("seekclaw_third_party/powershell/win-x64/", error.Message);
        var expected = new List<string> { Packaged };
        for (DirectoryInfo? dir = new(AppDirectory); dir is not null; dir = dir.Parent)
            expected.Add(Path.Combine(dir.FullName, "seekclaw_third_party", "powershell", "win-x64", "pwsh.exe"));
        Assert.Equal(expected, checkedPaths);
    }

    [Fact]
    public void ShellTools_RegisterPowerShellOnWindowsX64WithLegacyPermissions()
    {
        using var prompts = new FilePromptProvider([PromptsRoot]);
        var registry = new ToolRegistry();
        registry.Register(new BashTool(prompts));
        if (BashTool.CreatePowerShellTool(prompts) is { } tool) registry.Register(tool);
        var legacy = Assert.IsType<BashTool>(registry.Resolve("bash"));
        Assert.True(legacy.Mutating);
        Assert.True(legacy.RequiresWorkspace);

        var powerShell = registry.Resolve("powershell");
        var tester = new SubAgentRegistry().Resolve("tester")!;
        Assert.Contains("bash", tester.AllowedTools!);
        if (VendoredPowerShell.IsRequired)
        {
            Assert.IsType<BashTool>(powerShell);
            Assert.True(powerShell!.Mutating);
            Assert.True(powerShell.RequiresWorkspace);
            Assert.Contains("PowerShell 7", powerShell.Description);
            Assert.Equal(powerShell.Description, legacy.Description);
            Assert.Contains("powershell", tester.AllowedTools!);
            foreach (var name in new[] { "BASH", "PowerShell" })
            {
                Assert.True(Agent.IsToolDisabled(powerShell, [name]));
                Assert.True(Agent.IsToolDisabled(legacy, [name]));
            }
        }
        else
        {
            Assert.Null(powerShell);
            Assert.DoesNotContain("powershell", tester.AllowedTools!);
            Assert.False(Agent.IsToolDisabled(legacy, ["powershell"]));
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public async Task ShellTool_UsesVendoredPowerShellAndPreservesUtf8AndExitCode(string name)
    {
        if (!VendoredPowerShell.IsRequired) return;

        using var prompts = new FilePromptProvider([PromptsRoot]);
        var tool = name == "powershell" ? BashTool.CreatePowerShellTool(prompts)! : new BashTool(prompts);
        var context = new ToolContext { Workspace = Workspace, Events = new EventBus(), Agent = new AgentConfig() };
        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["command"] = Probe() + "; [Console]::WriteLine('你好'); exit 19",
            ["timeout_seconds"] = 20,
        }, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Exit code 19", result.Output);
        Assert.Contains("你好", result.Output);
        AssertVendoredShell(result.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public async Task Verification_UsesVendoredPowerShell(int exitCode)
    {
        if (!VendoredPowerShell.IsRequired) return;

        var workspace = new WorkspaceInfo
        {
            Root = Workspace.Root,
            ProjectKinds = [],
            Config = new WorkspaceConfig { VerifyCommand = Probe() + $"; exit {exitCode}" },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new BuildVerifier().VerifyAsync(workspace, deadline.Token);

        Assert.Equal(exitCode == 0, result.Success);
        AssertVendoredShell(result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandSession_UsesVendoredPowerShellWithStdinAndExitCode(bool pty)
    {
        if (!VendoredPowerShell.IsRequired) return;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var manager = new CommandSessionManager();
        var command = "[Console]::WriteLine('awaiting-input'); $line = [Console]::ReadLine(); " +
            "[Console]::WriteLine('received=' + $line); " + Probe() + "; exit 19";
        var read = await manager.StartAsync("owner", command, Workspace.Root, 20, pty, deadline.Token);
        var id = read["sessionId"]!.GetValue<string>();
        var cursor = read["cursor"]!.GetValue<long>();
        var output = new StringBuilder(read["output"]!.GetValue<string>());
        while (read["status"]!.GetValue<string>() == "running" && !output.ToString().Contains("awaiting-input", StringComparison.Ordinal))
            await ReadPage();
        Assert.Contains("awaiting-input", output.ToString());
        await manager.WriteAsync("owner", id, "hello\n", false, deadline.Token);
        do { await ReadPage(); }
        while (read["status"]!.GetValue<string>() == "running" || read["hasMore"]!.GetValue<bool>());

        Assert.Equal(pty, read["pty"]!.GetValue<bool>());
        Assert.Equal("exited", read["status"]!.GetValue<string>());
        Assert.Equal(19, read["exitCode"]!.GetValue<int>());
        Assert.Contains("received=hello", output.ToString());
        AssertVendoredShell(output.ToString());

        async Task ReadPage()
        {
            read = await manager.ReadAsync("owner", id, cursor, 1000, deadline.Token);
            cursor = read["cursor"]!.GetValue<long>();
            output.Append(read["output"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Doctor_ReportsSelectedPathAndVersion()
    {
        if (!VendoredPowerShell.IsRequired) return;

        var check = HealthChecker.CheckVendoredPowerShell();
        Assert.True(check.Ok, check.Detail);
        Assert.Contains(VendoredPowerShell.ResolveExecutable(), check.Detail);
        Assert.Contains("version 7.", check.Detail);
    }

    private static void AssertVendoredShell(string output)
    {
        Assert.Contains("version=7", output);
        Assert.Contains("vendored-shell=True", output);
    }
}
