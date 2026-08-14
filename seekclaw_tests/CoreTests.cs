using System.Text.Json;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Projects;
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
        var store = NewSessionStore();
        var session = store.Create(workspace, SeekClaw.Runtime.Providers.ReasoningLevel.XHigh);

        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("hello",
        [
            new SeekClaw.Runtime.Providers.ChatImageAttachment(
                "image-1", "screen.png", "image/png", "AQID", 3),
        ]));
        var assistant = new SeekClaw.Runtime.Providers.ChatMessage
        {
            Role = SeekClaw.Runtime.Providers.ChatRole.Assistant,
            Text = "hi!",
            ModelRef = "openai/gpt-5.5",
            ViewedImages = [new SeekClaw.Runtime.Providers.ChatImageReference("image-1", "screen.png")],
            ToolCalls = [new SeekClaw.Runtime.Providers.ToolCallRequest("c1", "edit_file", """{"path":"a.txt"}""")],
        };
        store.Append(session, assistant);
        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.ToolResult(
            "c1", "edit_file", "updated", true, "--- a.txt\n+++ a.txt\n-old\n+new", "a.txt"));

        var loaded = store.Load(workspace, session.Header.Id);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Messages.Count);
        Assert.Equal("hello", loaded.Messages[0].Text);
        Assert.Equal("screen.png", Assert.Single(loaded.Messages[0].Images!).Name);
        Assert.Equal("AQID", loaded.Messages[0].Images![0].Data);
        Assert.Equal("screen.png", Assert.Single(loaded.Messages[1].ViewedImages!).Name);
        Assert.Equal("edit_file", loaded.Messages[1].ToolCalls![0].Name);
        Assert.Equal("openai/gpt-5.5", loaded.Messages[1].ModelRef);
        Assert.Equal("c1", loaded.Messages[2].ToolCallId);
        Assert.Equal("a.txt", loaded.Messages[2].ToolFilePath);
        Assert.Contains("+new", loaded.Messages[2].ToolDiff);
        Assert.Equal(SeekClaw.Runtime.Providers.ReasoningLevel.XHigh, loaded.Header.ReasoningLevel);

        var latest = store.LoadLatest(workspace);
        Assert.Equal(session.Header.Id, latest!.Header.Id);
    }

    [Fact]
    public void SessionStore_UpdatesArchivesRestoresAndDeletesSessions()
    {
        var workspace = NewWorkspace("session-lifecycle");
        var store = NewSessionStore();
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
    public void SessionStore_PersistsNetworkEnabledToggleAndMigratesLegacyDatabases()
    {
        var workspace = NewWorkspace("network-toggle");
        var store = NewSessionStore();

        // New sessions default to network enabled.
        var session = store.Create(workspace);
        Assert.True(session.Header.NetworkEnabled);

        // Toggle off and verify the reloaded session keeps it.
        store.UpdateMetadata(workspace, session.Header.Id, networkEnabled: false);
        Assert.False(store.Load(workspace, session.Header.Id)!.Header.NetworkEnabled);
        Assert.False(Assert.Single(store.List(workspace)).NetworkEnabled);

        // A session created with the toggle already off round-trips too.
        var offline = store.Create(workspace, networkEnabled: false);
        Assert.False(store.Load(workspace, offline.Header.Id)!.Header.NetworkEnabled);

        // Legacy databases without the column are migrated with the default enabled.
        var legacyFile = Path.Combine(_dir, "legacy.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyFile}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                    scope TEXT NOT NULL, id TEXT NOT NULL, workspace TEXT NULL, title TEXT NULL,
                    archived INTEGER NOT NULL DEFAULT 0, reasoning_level INTEGER NOT NULL,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    PRIMARY KEY (scope, id));
                """;
            command.ExecuteNonQuery();
        }
        var migratedStore = new SessionStore(legacyFile);
        var migrated = migratedStore.Create(workspace);
        Assert.True(migrated.Header.NetworkEnabled);
        migratedStore.UpdateMetadata(workspace, migrated.Header.Id, networkEnabled: false);
        Assert.False(migratedStore.Load(workspace, migrated.Header.Id)!.Header.NetworkEnabled);
    }

    [Fact]
    public void SessionStore_TruncateDropsMessagesAfterKeepCount()
    {
        var workspace = NewWorkspace("truncate");
        var store = NewSessionStore();
        var session = store.Create(workspace);
        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("first"));
        store.Append(session, new SeekClaw.Runtime.Providers.ChatMessage
        {
            Role = SeekClaw.Runtime.Providers.ChatRole.Assistant,
            Text = "answer one",
        });
        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("second"));
        store.Append(session, new SeekClaw.Runtime.Providers.ChatMessage
        {
            Role = SeekClaw.Runtime.Providers.ChatRole.Assistant,
            Text = "answer two",
        });

        store.Truncate(workspace, session.Header.Id, keepMessageCount: 2);

        var reloaded = store.Load(workspace, session.Header.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Messages.Count);
        Assert.Equal("first", reloaded.Messages[0].Text);
        Assert.Equal("answer one", reloaded.Messages[1].Text);

        // Truncating beyond the current size is a no-op.
        store.Truncate(workspace, session.Header.Id, keepMessageCount: 99);
        Assert.Equal(2, store.Load(workspace, session.Header.Id)!.Messages.Count);
    }

    [Fact]
    public void SessionStore_PersistsGlobalSessionsWithoutWorkspaceMetadata()
    {
        var global = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global-state"));
        var store = NewSessionStore();
        var session = store.Create(global);

        store.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("global hello"));

        Assert.True(global.IsGlobal);
        Assert.Null(session.Header.Workspace);
        Assert.EndsWith("seekclaw.db", session.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("global hello", store.Load(global, session.Header.Id)!.Messages[0].Text);
    }

    [Fact]
    public void SessionStore_ImportsLegacyJsonlOnce_AndKeepsBackupUntilDeletion()
    {
        var workspace = NewWorkspace("legacy-import");
        Directory.CreateDirectory(workspace.SessionsDir);
        var header = new SessionHeader
        {
            Id = "legacy-1",
            Workspace = workspace.Root,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        var message = new SessionMessage { Role = "user", Text = "legacy title" };
        var file = Path.Combine(workspace.SessionsDir, "legacy-1.jsonl");
        File.WriteAllLines(file,
        [
            JsonSerializer.Serialize(header, SeekClawJsonContext.Compact.SessionHeader),
            JsonSerializer.Serialize(message, SeekClawJsonContext.Compact.SessionMessage),
        ]);

        var store = NewSessionStore();
        var imported = store.Load(workspace, "legacy-1");
        Assert.NotNull(imported);
        Assert.Equal("legacy title", imported!.Messages[0].Text);
        Assert.Equal("legacy title", Assert.Single(store.List(workspace)).Title);
        Assert.True(File.Exists(file));

        var secondStore = NewSessionStore();
        Assert.Single(secondStore.List(workspace));
        store.Delete(workspace, "legacy-1");
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task SessionStore_AllowsConcurrentAppendsFromIsolatedStores()
    {
        var workspace = NewWorkspace("concurrent-sqlite");
        var first = NewSessionStore();
        var session = first.Create(workspace);
        var secondStore = NewSessionStore();
        var second = secondStore.Load(workspace, session.Header.Id)!;

        var writes = Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            var target = index % 2 == 0 ? session : second;
            (index % 2 == 0 ? first : secondStore).Append(
                target, SeekClaw.Runtime.Providers.ChatMessage.User($"message-{index}"));
        }));
        await Task.WhenAll(writes);

        Assert.Equal(100, first.Load(workspace, session.Header.Id)!.Messages.Count);
    }

    [Fact]
    public void ProjectStore_DeduplicatesPathsAndRemovesProjects()
    {
        var root = Path.Combine(_dir, "project-store");
        Directory.CreateDirectory(root);
        var projects = new ProjectStore(new SeekClawDatabase(Path.Combine(_dir, "projects.db")));
        var created = projects.Upsert("project-id", root, "First");
        var same = projects.Upsert("another-id", root, "Renamed");

        Assert.Equal(created.Id, same.Id);
        Assert.Equal("Renamed", Assert.Single(projects.List()).Name);
        projects.Remove(created.Id);
        Assert.Empty(projects.List());
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
        Assert.False(Directory.Exists(workspace.SessionsDir));
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

    private SessionStore NewSessionStore() =>
        new(Path.Combine(_dir, "state", "seekclaw.db"));
}
