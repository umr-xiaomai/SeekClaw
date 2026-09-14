using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;
using Xunit;

namespace SeekClaw.Tests;

public class McpManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-mcp-tests", Guid.NewGuid().ToString("N"));

    public McpManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ConnectAllAsync_ReportsFailuresWithoutAbortingTheReload()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var configStore = new ConfigStore(
            Path.Combine(_dir, "mcp-config.json"),
            Path.Combine(_dir, "mcp-state.json"));

        // A stdio server whose executable does not exist used to escape the exception
        // filter and abort the whole reload, hiding every other server.
        configStore.Config.Mcp.Servers["broken-stdio"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "seekclaw-command-that-does-not-exist",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["broken-remote"] = new McpServerConfig
        {
            Transport = "sse",
            Url = "http://127.0.0.1:9/mcp",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["broken-streamable"] = new McpServerConfig
        {
            Transport = "http",
            Url = "http://127.0.0.1:9/mcp",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["off"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "seekclaw-command-that-does-not-exist",
            Enabled = false,
        };

        await using var manager = new McpManager(configStore, new ToolRegistry(), new PromptRegistry());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var statuses = await manager.ConnectAllAsync(workspace, cts.Token);

        Assert.Equal(4, statuses.Count);
        Assert.All(statuses, status => Assert.False(status.Connected));
        Assert.Equal("disabled", statuses.Single(status => status.Name == "off").Error);
        Assert.False(string.IsNullOrWhiteSpace(statuses.Single(status => status.Name == "broken-stdio").Error));
        Assert.False(string.IsNullOrWhiteSpace(statuses.Single(status => status.Name == "broken-remote").Error));
        Assert.False(string.IsNullOrWhiteSpace(statuses.Single(status => status.Name == "broken-streamable").Error));
    }

    [Fact]
    public void MarkConnecting_ShowsEnabledServersAsPending()
    {
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "pending-ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);

        var configStore = new ConfigStore(
            Path.Combine(_dir, "pending-config.json"),
            Path.Combine(_dir, "pending-state.json"));
        configStore.Config.Mcp.Servers["on"] = new McpServerConfig
        {
            Transport = "sse",
            Url = "http://127.0.0.1:9/mcp",
            Enabled = true,
        };
        configStore.Config.Mcp.Servers["off"] = new McpServerConfig
        {
            Transport = "stdio",
            Command = "npx",
            Enabled = false,
        };

        var manager = new McpManager(configStore, new ToolRegistry(), new PromptRegistry());

        manager.MarkConnecting(workspace);

        var statuses = manager.Status;
        Assert.Equal(2, statuses.Count);
        var pending = statuses.Single(status => status.Name == "on");
        Assert.True(pending.Connecting);
        Assert.False(pending.Connected);
        Assert.Null(pending.Error);
        var disabled = statuses.Single(status => status.Name == "off");
        Assert.False(disabled.Connecting);
        Assert.Equal("disabled", disabled.Error);
    }
}
