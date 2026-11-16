using System.Text.Json.Nodes;
using SeekClaw.Runtime.Browser;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class BrowserTests
{
    [Fact]
    public async Task Bridge_CorrelatesConcurrentReplies_AndRejectsForeignOrExpiredReplies()
    {
        var requests = new List<JsonObject>();
        using var bridge = new DesktopBrowserBridge((name, data) => { if (name == "browser.request") requests.Add(data); return Task.CompletedTask; }) { Enabled = true };
        using var other = new DesktopBrowserBridge((_, _) => Task.CompletedTask) { Enabled = true };
        var a = bridge.ExecuteAsync("a", "first", new JsonObject { ["action"] = "snapshot" }, CancellationToken.None);
        var b = bridge.ExecuteAsync("b", "second", new JsonObject { ["action"] = "click" }, CancellationToken.None);
        var aid = requests[0]["requestId"]!.GetValue<string>();
        var bid = requests[1]["requestId"]!.GetValue<string>();
        Assert.False(other.Complete(aid, new JsonObject()));
        Assert.True(bridge.Complete(bid, new JsonObject { ["value"] = "b" }));
        Assert.False(a.IsCompleted);
        Assert.True(bridge.Complete(aid, new JsonObject { ["value"] = "a" }));
        Assert.Equal("a", (await a)["value"]!.GetValue<string>());
        Assert.Equal("b", (await b)["value"]!.GetValue<string>());
        Assert.False(bridge.Complete(aid, new JsonObject()));
    }

    [Fact]
    public async Task Bridge_CancelsTimesOutAndDisconnects_WithoutReplayingActions()
    {
        var events = new List<string>();
        using var bridge = new DesktopBrowserBridge((name, _) => { events.Add(name); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(20)) { Enabled = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExecuteAsync("a", "s", new JsonObject(), CancellationToken.None));
        Assert.Equal(1, events.Count(name => name == "browser.request"));
        Assert.Contains("browser.cancel", events);
        using var cancellation = new CancellationTokenSource();
        var pending = bridge.ExecuteAsync("a", "s", new JsonObject(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var disconnected = bridge.ExecuteAsync("a", "s", new JsonObject(), CancellationToken.None);
        bridge.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => disconnected);
    }

    [Fact]
    public async Task Tool_UsesStorageScopeAcrossWorktrees_AndPreservesScreenshotAndValidJson()
    {
        using var prompts = new FilePromptProvider();
        var workspace = new WorkspaceManager().Detect(Directory.GetCurrentDirectory());
        var storage = new WorkspaceManager().Detect(Path.GetTempPath());
        var bridge = new StubBridge();
        var tool = new BrowserTool(prompts, bridge);
        var result = await tool.ExecuteAsync(new JsonObject { ["action"] = "screenshot" }, new ToolContext
        {
            Workspace = workspace, SessionWorkspace = storage, SessionId = "session", Events = new EventBus(), Agent = new AgentConfig(), MaxOutputChars = 1200,
        }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(BrowserTool.Scope(storage.Root, "session"), bridge.Scope);
        Assert.NotEqual(bridge.Scope, BrowserTool.Scope(storage.Root, "other"));
        Assert.NotNull(JsonNode.Parse(result.Output));
        Assert.Single(result.Images!);
        Assert.DoesNotContain("base64", result.Output);
        var denied = await tool.ExecuteAsync(new JsonObject { ["action"] = "click" }, new ToolContext
        {
            Workspace = workspace, SessionId = "session", Events = new EventBus(), Agent = new AgentConfig { Mode = "readonly" },
        }, CancellationToken.None);
        Assert.False(denied.Success);
    }

    private sealed class StubBridge : IBrowserBridge
    {
        public string? Scope { get; private set; }
        public Task<JsonObject> ExecuteAsync(string scope, string sessionId, JsonObject command, CancellationToken ct)
        {
            Scope = scope;
            return Task.FromResult(new JsonObject { ["snapshot_id"] = "snapshot", ["text"] = new string('x', 5000),
                ["elements"] = new JsonArray("button"), ["image"] = new JsonObject { ["data"] = "AQID" } });
        }
    }
}
