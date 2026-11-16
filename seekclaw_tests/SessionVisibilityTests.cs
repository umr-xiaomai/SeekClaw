using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class SessionVisibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-session-visibility", Guid.NewGuid().ToString("N"));

    public SessionVisibilityTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildSessionsStayReadableButNeverBecomeUserConversations(bool global)
    {
        var database = new SeekClawDatabase(Path.Combine(_root, "data.db"));
        var store = new SessionStore(database);
        var workspace = new WorkspaceInfo { Root = _root, IsGlobal = global, ProjectKinds = [] };
        var parent = store.Create(workspace);
        store.Append(parent, ChatMessage.User("Only the parent appears"));
        // Ordinary sessions started by schedules have no internal parent marker.
        var scheduled = store.Create(workspace);
        store.Append(scheduled, ChatMessage.User("Scheduled review"));
        var child = store.Create(workspace, parentSessionId: parent.Header.Id);
        store.Append(child, ChatMessage.User("Internal delegated instruction"));

        var visible = store.List(workspace, includeArchived: true).Select(session => session.Id).ToArray();
        Assert.Contains(parent.Header.Id, visible);
        Assert.Contains(scheduled.Header.Id, visible);
        Assert.DoesNotContain(child.Header.Id, visible);
        Assert.NotEqual(child.Header.Id, store.LoadLatest(workspace)!.Header.Id);
        var loaded = store.Load(workspace, child.Header.Id)!;
        Assert.Equal(parent.Header.Id, loaded.Header.ParentSessionId);
        Assert.Single(loaded.Messages);
        Assert.Single(store.Load(workspace, parent.Header.Id)!.Messages);
    }

    [Fact]
    public void LegacyTaskRecordsHideOnlyTheirExactSessionsInTheMatchingWorkspace()
    {
        var database = new SeekClawDatabase(Path.Combine(_root, "data.db"));
        var store = new SessionStore(database);
        var records = new ExecutionRecordStore(database);
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        var user = store.Create(workspace);
        // Identical wording is intentionally retained for a real user conversation.
        store.Append(user, ChatMessage.User("只读审查 Godot 渲染"));
        var child = store.Create(workspace);
        store.Append(child, ChatMessage.User("只读审查 Godot 渲染"));
        records.Put("subagent", "task", user.Header.Id, new JsonObject
        {
            ["sessionId"] = child.Header.Id, ["workspace"] = _root.Replace('\\', '/') + "/",
            ["parent"] = user.Header.Id, ["role"] = "researcher", ["status"] = "completed"
        });
        // An unrelated record and a non-agent execution record must not hide the user session.
        records.Put("subagent", "foreign-task", "other-parent", new JsonObject
        {
            ["sessionId"] = user.Header.Id, ["workspace"] = Path.Combine(_root, "other"), ["status"] = "completed"
        });
        records.Put("command", "command", user.Header.Id, new JsonObject { ["sessionId"] = user.Header.Id, ["workspace"] = _root });

        Assert.Equal(user.Header.Id, Assert.Single(store.List(workspace, includeArchived: true)).Id);
        Assert.NotNull(store.Load(workspace, child.Header.Id));
        store.UpdateMetadata(workspace, child.Header.Id, archived: true);
        Assert.Equal(user.Header.Id, Assert.Single(store.List(workspace, includeArchived: true)).Id);
        using var connection = database.OpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sessions;";
        Assert.Equal(2L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void ExistingDatabasesGainNullableParentMetadataWithoutLosingUserSessions()
    {
        var file = Path.Combine(_root, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE sessions (
                    scope TEXT NOT NULL, id TEXT NOT NULL, workspace TEXT NULL, title TEXT NULL,
                    archived INTEGER NOT NULL DEFAULT 0, reasoning_level INTEGER NOT NULL,
                    network_enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    PRIMARY KEY(scope, id)
                );
                """;
            schema.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions(scope, id, workspace, title, reasoning_level, created_at, updated_at)
                VALUES($scope, 'existing-user', $workspace, 'Existing user conversation', $reasoning, $now, $now);
                """;
            insert.Parameters.AddWithValue("$scope", SeekClawDatabase.ScopeKey(new WorkspaceInfo { Root = _root, ProjectKinds = [] }));
            insert.Parameters.AddWithValue("$workspace", _root);
            insert.Parameters.AddWithValue("$reasoning", (int)ReasoningLevel.High);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        var database = new SeekClawDatabase(file);
        var store = new SessionStore(database);
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        var user = store.Load(workspace, "existing-user")!;
        var child = store.Create(workspace, parentSessionId: user.Header.Id);
        Assert.Equal(user.Header.Id, Assert.Single(store.List(workspace)).Id);
        Assert.Equal("Existing user conversation", user.Header.Title);
        Assert.Null(store.Load(workspace, user.Header.Id)!.Header.ParentSessionId);
        Assert.Equal(user.Header.Id, store.Load(workspace, child.Header.Id)!.Header.ParentSessionId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
