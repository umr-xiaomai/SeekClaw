using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Skills;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-tests", Guid.NewGuid().ToString("N"));

    public CoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task EventBus_DeliversToAllSubscribers_AndUnsubscribes()
    {
        var bus = new EventBus();
        var sub1 = bus.Subscribe();
        var sub2 = bus.Subscribe();

        bus.Publish(new StatusEvent("Thinking"));

        Assert.True(sub1.Reader.TryRead(out var evt1));
        Assert.True(sub2.Reader.TryRead(out _));
        Assert.Equal("Thinking", ((StatusEvent)evt1!).Status);

        sub2.Dispose();
        bus.Publish(new StatusEvent("Next"));
        Assert.True(sub1.Reader.TryRead(out _));
        Assert.False(await sub2.Reader.WaitToReadAsync());
        sub1.Dispose();
    }

    [Fact]
    public void WorkspaceManager_DetectsProjectKinds_AndWalksUpToRoot()
    {
        var root = Path.Combine(_dir, "proj");
        var nested = Path.Combine(root, "src", "deep");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "dependencies": { "vue": "^3.0.0" } }""");
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]");

        var workspace = new WorkspaceManager().Detect(nested);

        Assert.Equal(root, workspace.Root);
        Assert.Contains("git", workspace.ProjectKinds);
        Assert.Contains("node", workspace.ProjectKinds);
        Assert.Contains("vue", workspace.ProjectKinds);
        Assert.Contains("rust", workspace.ProjectKinds);
    }

    [Fact]
    public void SessionStore_PersistsAndReloadsMessages()
    {
        var workspace = NewWorkspace();
        var store = new SessionStore();
        var session = store.Create(workspace);

        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("hello"));
        var assistant = new SeekClaw.Runtime.Providers.ChatMessage
        {
            Role = SeekClaw.Runtime.Providers.ChatRole.Assistant,
            Text = "hi!",
            ToolCalls = [new SeekClaw.Runtime.Providers.ToolCallRequest("c1", "read_file", """{"path":"a.txt"}""")],
        };
        store.Append(session, assistant);
        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.ToolResult("c1", "read_file", "contents", true));

        var loaded = store.Load(workspace, session.Header.Id);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Messages.Count);
        Assert.Equal("hello", loaded.Messages[0].Text);
        Assert.Equal("read_file", loaded.Messages[1].ToolCalls![0].Name);
        Assert.Equal("c1", loaded.Messages[2].ToolCallId);

        var latest = store.LoadLatest(workspace);
        Assert.Equal(session.Header.Id, latest!.Header.Id);
    }

    [Fact]
    public void SessionStore_UpdatesArchivesRestoresAndDeletesSessions()
    {
        var workspace = NewWorkspace("session-lifecycle");
        var store = new SessionStore();
        var session = store.Create(workspace);
        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("original title"));

        var renamed = store.UpdateMetadata(workspace, session.Header.Id, title: "Renamed task");
        Assert.Equal("Renamed task", renamed.Title);

        var archived = store.UpdateMetadata(workspace, session.Header.Id, archived: true);
        Assert.True(archived.Archived);
        Assert.Empty(store.List(workspace));
        Assert.Equal(session.Header.Id, Assert.Single(store.List(workspace, includeArchived: true)).Id);
        Assert.Null(store.LoadLatest(workspace));

        store.UpdateMetadata(workspace, session.Header.Id, archived: false);
        Assert.Equal("Renamed task", Assert.Single(store.List(workspace)).Title);

        store.Delete(workspace, session.Header.Id);
        Assert.Null(store.Load(workspace, session.Header.Id));
    }

    [Fact]
    public void SessionStore_PersistsGlobalSessionsWithoutWorkspaceMetadata()
    {
        var global = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global-state"));
        var store = new SessionStore();
        var session = store.Create(global);

        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("global hello"));

        Assert.True(global.IsGlobal);
        Assert.Null(session.Header.Workspace);
        Assert.StartsWith(global.SessionsDir, session.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("global hello", store.Load(global, session.Header.Id)!.Messages[0].Text);
    }

    [Fact]
    public void SkillManager_DiscoversWorkspaceSkills_AndTogglesEnabled()
    {
        var workspace = NewWorkspace();
        var skillDir = Path.Combine(workspace.SkillsDir, "unity-helper");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "skill.yaml"), "name: unity-helper\ndescription: Unity tips\nversion: 1.0.0\n");
        File.WriteAllText(Path.Combine(skillDir, "prompt.txt"), "You know Unity.");

        var configStore = new ConfigStore(Path.Combine(_dir, "cfg.json"), Path.Combine(_dir, "state.json"));
        var manager = new SkillManager(configStore, new PromptRegistry());

        var skill = Assert.Single(manager.Discover(workspace), s => s.Name == "unity-helper");
        Assert.True(skill.Enabled);
        Assert.Equal("Unity tips", skill.Manifest.Description);

        manager.SetEnabled("unity-helper", false);
        skill = Assert.Single(manager.Discover(workspace), s => s.Name == "unity-helper");
        Assert.False(skill.Enabled);
    }

    [Fact]
    public void Bootstrap_CreatesSpecDirectories_AndGitignoreEntries()
    {
        var workspace = NewWorkspace("boot");
        var created = new WorkspaceManager().Bootstrap(workspace);

        Assert.True(Directory.Exists(workspace.CacheDir));
        Assert.True(Directory.Exists(workspace.SessionsDir));
        Assert.True(Directory.Exists(workspace.LogsDir));
        Assert.True(Directory.Exists(workspace.SkillsDir));
        Assert.True(Directory.Exists(workspace.McpDir));
        Assert.True(Directory.Exists(workspace.DocsDir));
        Assert.NotEmpty(created);

        var gitignore = File.ReadAllLines(Path.Combine(workspace.Root, ".gitignore"));
        Assert.Contains(".cache/", gitignore);
        Assert.Contains(".session/", gitignore);

        // Idempotent: second run creates nothing new.
        Assert.Empty(new WorkspaceManager().Bootstrap(new WorkspaceManager().Detect(workspace.Root)));
    }

    private WorkspaceInfo NewWorkspace(string name = "ws")
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        return new WorkspaceInfo { Root = root, ProjectKinds = [] };
    }
}
