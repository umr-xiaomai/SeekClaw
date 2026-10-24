using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class ToolSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seekclaw-tool-safety-tests", Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(
        Path.GetTempPath(), "seekclaw-tool-safety-outside", Guid.NewGuid().ToString("N"));

    public ToolSafetyTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_outside)) Directory.Delete(_outside, recursive: true); } catch { }
    }

    [Fact]
    public void ResolvePath_RejectsTraversalAndAbsolutePathsOutsideWorkspace()
    {
        var context = Context(_root);

        Assert.Throws<UnauthorizedAccessException>(() => context.ResolvePath("../escape.txt"));
        Assert.Throws<UnauthorizedAccessException>(
            () => context.ResolvePath(Path.Combine(_outside, "escape.txt")));
    }

    [Fact]
    public void ResolvePath_RejectsSymlinkedDirectoryOutsideWorkspace()
    {
        var link = Path.Combine(_root, "outside-link");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows may require developer mode / elevation to create symlinks.
        }

        var context = Context(_root);
        Assert.Throws<UnauthorizedAccessException>(() => context.ResolvePath("outside-link/escape.txt"));
    }

    [Fact]
    public async Task EditFile_PreservesUtf16BomAndCrlf()
    {
        var path = Path.Combine(_root, "crlf.txt");
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma\r\n", encoding);

        var tool = new EditFileTool(new NullPrompts());
        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["path"] = "crlf.txt",
            ["old_string"] = "beta\ngamma",
            ["new_string"] = "BETA\nGAMMA",
        }, Context(_root), CancellationToken.None);

        Assert.True(result.Success, result.Output);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("alpha\r\nBETA\r\nGAMMA\r\n", encoding.GetString(bytes, 2, bytes.Length - 2));
    }

    [Theory]
    [InlineData("git status", true)]
    [InlineData("git log -n 5", true)]
    [InlineData("git show 060ca71", true)]
    [InlineData("git diff HEAD~1", true)]
    [InlineData("ls -la", true)]
    [InlineData("dir /b", true)]
    [InlineData("cat README.md", true)]
    [InlineData("rg 'function' src/", true)]
    [InlineData("pwd", true)]
    [InlineData("echo hello", true)]
    [InlineData("git log | head -n 10", true)]
    [InlineData("git commit -m 'feat'", false)]
    [InlineData("rm -rf src/", false)]
    [InlineData("echo hello > out.txt", false)]
    [InlineData("dotnet build", false)]
    [InlineData("npm install", false)]
    public void BashTool_IsReadOnlyCommand_DetectsReadActionsAccurately(string command, bool expectedReadOnly)
    {
        Assert.Equal(expectedReadOnly, BashTool.IsReadOnlyCommand(command));
    }

    [Fact]
    public void PromptVariables_MapApprovalModesAndHonorExplicitOverride()
    {
        Assert.Equal("on-request", PromptVariables.Build(null, null, mode: "manual")["approval_policy"]);
        Assert.Equal("on-risk", PromptVariables.Build(null, null, mode: "guardrail")["approval_policy"]);
        Assert.Equal("never", PromptVariables.Build(null, null, mode: "auto")["approval_policy"]);
        Assert.Equal("explicit",
            PromptVariables.Build(null, null, mode: "manual", approvalPolicy: "explicit")["approval_policy"]);
    }

    private static ToolContext Context(string root) => new()
    {
        Workspace = new WorkspaceInfo { Root = root, ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
    };

    private sealed class NullPrompts : IPromptProvider
    {
        public string? TryGet(string key) => null;
        public string Get(string key) => "";
        public string Render(string template, IReadOnlyDictionary<string, string> variables) => template;
        public string? GetRendered(string key, IReadOnlyDictionary<string, string> variables) => null;
        public void SetWorkspaceRoot(string? promptsDir) { }
    }
}
