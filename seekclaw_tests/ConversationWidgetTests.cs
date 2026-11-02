using System.Text.Json.Nodes;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Presentation;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Scheduling;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class ConversationWidgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-widget-tests", Guid.NewGuid().ToString("N"));
    public ConversationWidgetTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    private ToolContext Context(IEventBus events) => new()
    {
        Workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] }, Events = events,
        Agent = new AgentConfig(), SessionId = "conversation",
    };

    [Fact]
    public async Task ScheduleChanges_ReturnBoundCards_AndEmitInvalidations()
    {
        using var prompts = new FilePromptProvider([_root]);
        var store = new ScheduleStore(new SeekClawDatabase(Path.Combine(_root, "data.db")));
        var bus = new EventBus();
        using var events = bus.Subscribe();
        var tool = new ScheduleTool(prompts, store);
        var context = Context(bus);
        var result = await tool.ExecuteAsync(new JsonObject {
            ["action"] = "upsert", ["name"] = "Check progress", ["prompt"] = "Report meaningful changes", ["cron"] = "0 9 * * *"
        }, context, CancellationToken.None);
        var created = Assert.IsType<ScheduleWidget>(Assert.Single(result.Widgets!));
        Assert.Equal("conversation", created.Task.SessionId);
        Assert.Equal("schedule:" + created.Task.Id, created.Id);
        Assert.Equal("changes", created.Task.NotificationPolicy);
        Assert.True(created.Task.Enabled);

        foreach (var action in new[] { "pause", "resume", "complete", "delete" })
        {
            result = await tool.ExecuteAsync(new JsonObject { ["action"] = action, ["id"] = created.Task.Id }, context, CancellationToken.None);
            var card = Assert.IsType<ScheduleWidget>(Assert.Single(result.Widgets!));
            Assert.Equal(created.Id, card.Id);
            Assert.Equal(action == "resume", card.Task.Enabled);
            Assert.Equal(action == "delete", card.Deleted);
        }
        Assert.Empty(store.List());
        var invalidations = 0;
        while (events.Reader.TryRead(out var item)) if (item is ScheduledTaskUpdatedEvent) invalidations++;
        Assert.Equal(5, invalidations);
    }

    [Fact]
    public async Task HtmlWritesAndExplicitPresentation_ProduceCards_AndRoundTripThroughHistory()
    {
        using var prompts = new FilePromptProvider([_root]);
        var context = Context(new EventBus());
        var result = await new WriteFileTool(prompts).ExecuteAsync(new JsonObject { ["path"] = "preview.html", ["content"] = "<h1>Hello</h1>" }, context, CancellationToken.None);
        var artifact = Assert.IsType<ArtifactWidget>(Assert.Single(result.Widgets!));
        Assert.Equal(Path.Combine(_root, "preview.html"), artifact.Path);
        Assert.Equal("text/html", artifact.MediaType);
        Assert.Equal(context.Workspace.WorkingDirectory, artifact.ExecutionRoot);
        var edit = await new EditFileTool(prompts).ExecuteAsync(new JsonObject { ["path"] = "preview.html", ["old_string"] = "Hello", ["new_string"] = "Updated" }, context, CancellationToken.None);
        Assert.Equal(artifact.Id, Assert.Single(edit.Widgets!).Id);
        var presented = await new PresentArtifactTool(prompts).ExecuteAsync(new JsonObject { ["path"] = "preview.html", ["title"] = "Interactive demo" }, context, CancellationToken.None);
        Assert.Equal(artifact.Id, Assert.Single(presented.Widgets!).Id);
        Assert.Equal("Interactive demo", presented.Widgets![0].Title);
        Assert.Equal(artifact.ExecutionRoot, Assert.IsType<ArtifactWidget>(presented.Widgets[0]).ExecutionRoot);

        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        var session = store.Create(context.Workspace);
        var schedule = new ScheduleWidget("schedule:task", "Daily review", new ScheduledTask { Id = "task", Name = "Daily review", SessionId = session.Header.Id });
        store.Append(session, ChatMessage.ToolResult("call", "test", "result", true, widgets: [artifact, schedule]));
        var restored = Assert.Single(store.Load(context.Workspace, session.Header.Id)!.Messages);
        Assert.Equal(artifact, Assert.IsType<ArtifactWidget>(restored.ToolWidgets![0]));
        var restoredSchedule = Assert.IsType<ScheduleWidget>(restored.ToolWidgets[1]);
        Assert.Equal(schedule.Id, restoredSchedule.Id);
        Assert.Equal(session.Header.Id, restoredSchedule.Task.SessionId);
    }

    [Fact]
    public void GlobalFileRoot_IsTheExecutionDirectory_RatherThanTheConfigurationDirectory()
    {
        var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(_root, "global-state"));
        var context = Context(new EventBus());
        var global = new ToolContext { Workspace = workspace, Events = context.Events, Agent = context.Agent };
        Assert.NotEqual(workspace.Root, workspace.WorkingDirectory);
        Assert.Equal(Directory.GetCurrentDirectory(), workspace.WorkingDirectory);
        Assert.Equal(Path.Combine(workspace.WorkingDirectory, "preview.html"), global.ResolvePath("preview.html"));
    }

    [Fact]
    public async Task PresentArtifact_RejectsMissingAndOutsideFiles_AndKeepsOrdinaryFilesAsFileCards()
    {
        using var prompts = new FilePromptProvider([_root]);
        var tool = new PresentArtifactTool(prompts);
        var context = Context(new EventBus());
        Assert.False((await tool.ExecuteAsync(new JsonObject { ["path"] = "missing.html" }, context, CancellationToken.None)).Success);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new JsonObject { ["path"] = "../outside.html" }, context, CancellationToken.None));
        var result = await new WriteFileTool(prompts).ExecuteAsync(new JsonObject { ["path"] = "report.txt", ["content"] = "Report" }, context, CancellationToken.None);
        Assert.Null(result.Widgets);
        result = await tool.ExecuteAsync(new JsonObject { ["path"] = "report.txt" }, context, CancellationToken.None);
        Assert.Equal("application/octet-stream", Assert.IsType<ArtifactWidget>(Assert.Single(result.Widgets!)).MediaType);
    }
}
