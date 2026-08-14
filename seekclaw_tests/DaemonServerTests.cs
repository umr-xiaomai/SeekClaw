using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Daemon;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class DaemonServerTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "seekclaw-daemon-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _serverCts = new();
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private SeekClawRuntime? _runtime;

    public DaemonServerTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task ActiveChat_DoesNotBlockCancellation_AndEndsWithCancelledEvent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(1, "chat", new JsonObject { ["message"] = "keep working" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendAsync(3, "workspace.open", new JsonObject { ["path"] = _tempDir });
        var openedWorkspace = await connection.ReadAsync();
        Assert.Equal(3, openedWorkspace["id"]!.GetValue<long>());
        Assert.Equal("result", openedWorkspace["event"]!.GetValue<string>());

        await connection.SendAsync(2, "agent.cancel", new JsonObject { ["requestId"] = 1 });

        var responses = new List<JsonObject>();
        while (responses.Count < 2)
            responses.Add(await connection.ReadAsync());

        Assert.Contains(responses, response =>
            response["id"]!.GetValue<long>() == 2
            && response["event"]!.GetValue<string>() == "result");
        Assert.Contains(responses, response =>
            response["id"]!.GetValue<long>() == 1
            && response["event"]!.GetValue<string>() == "cancelled");
    }

    [Fact]
    public async Task Routing_GetAndSet_FailoverEnabled()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("", false, null)));

        await connection.SendAsync(1, "routing.get");
        var initial = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "result");
        var initialData = JsonNode.Parse(initial["data"]!.GetValue<string>())!;
        var initialEnabled = initialData["failoverEnabled"]!.GetValue<bool>();
        Assert.True(initialEnabled); // default on
        Assert.False(initialData["deepSeekOptimizationEnabled"]!.GetValue<bool>()); // default off

        await connection.SendAsync(2, "routing.set", new JsonObject
        {
            ["failoverEnabled"] = false,
            ["deepSeekOptimizationEnabled"] = true,
        });
        var set = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "result");
        var setData = JsonNode.Parse(set["data"]!.GetValue<string>())!;
        Assert.False(setData["failoverEnabled"]!.GetValue<bool>());
        Assert.True(setData["deepSeekOptimizationEnabled"]!.GetValue<bool>());

        await connection.SendAsync(3, "routing.get");
        var after = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 3
                        && response["event"]!.GetValue<string>() == "result");
        var afterData = JsonNode.Parse(after["data"]!.GetValue<string>())!;
        Assert.False(afterData["failoverEnabled"]!.GetValue<bool>());
        Assert.True(afterData["deepSeekOptimizationEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Schedule_AdminMethods_CrudToggleRunAndList()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "schedule.create", new JsonObject
        {
            ["name"] = "每日检查",
            ["prompt"] = "检查项目状态",
            ["cron"] = "0 9 * * *",
            ["workspace"] = _tempDir,
        });
        var created = ParseData(await connection.ReadAsync());
        var id = created["id"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(id));

        await connection.SendAsync(2, "schedule.toggle", new JsonObject { ["id"] = id, ["enabled"] = false });
        var toggled = ParseData(await connection.ReadAsync());
        Assert.False(toggled["enabled"]!.GetValue<bool>());

        await connection.SendAsync(3, "schedule.run", new JsonObject { ["id"] = id });
        var run = await connection.ReadAsync();
        Assert.Equal("result", run["event"]!.GetValue<string>());

        // schedule.run acknowledges immediately and executes in the background;
        // poll the list until the (instant) stub turn records its outcome.
        JsonObject? listed = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await connection.SendAsync(4, "schedule.list");
            var listResponse = await connection.ReadUntilAsync(item =>
                item["id"]!.GetValue<long>() == 4 && item["event"]!.GetValue<string>() == "result");
            var listNow = JsonNode.Parse(listResponse["data"]!.GetValue<string>())!.AsArray();
            listed = listNow.FirstOrDefault(item => item!["id"]!.GetValue<string>() == id)?.AsObject();
            if (listed is not null && listed["lastStatus"]!.GetValue<string>() is not null) break;
            await Task.Delay(50);
        }
        Assert.NotNull(listed);
        Assert.Equal("success", listed!["lastStatus"]!.GetValue<string>());

        await connection.SendAsync(5, "schedule.create", new JsonObject
        {
            ["name"] = "坏任务",
            ["prompt"] = "x",
            ["cron"] = "not a cron",
        });
        var invalid = await connection.ReadAsync();
        Assert.Equal("error", invalid["event"]!.GetValue<string>());
        Assert.Contains("cron", invalid["data"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await connection.SendAsync(6, "schedule.delete", new JsonObject { ["id"] = id });
        var deleted = await connection.ReadAsync();
        Assert.Equal("result", deleted["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task Schedule_Upcoming_BroadcastsNotice()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));
        _runtime!.Events.Publish(new ScheduledTaskUpcomingEvent(
            "upcoming-id", "即将执行", DateTimeOffset.UtcNow.AddMinutes(1)));

        var notice = await connection.ReadUntilAsync(item =>
            item["event"]!.GetValue<string>() == "schedule.upcoming");
        Assert.Equal(0, notice["id"]!.GetValue<long>());
        var details = notice["details"]!.AsObject();
        Assert.Equal("upcoming-id", details["taskId"]!.GetValue<string>());
        Assert.Equal("即将执行", details["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(details["runAt"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Schedule_Completion_BroadcastsUpdatedEvent()
    {
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)));

        await connection.SendAsync(1, "schedule.create", new JsonObject
        {
            ["name"] = "广播通知",
            ["prompt"] = "检查项目状态",
            ["cron"] = "0 9 * * *",
            ["workspace"] = _tempDir,
        });
        var created = ParseData(await connection.ReadAsync());
        var id = created["id"]!.GetValue<string>();

        await connection.SendAsync(2, "schedule.run", new JsonObject { ["id"] = id });
        var run = await connection.ReadAsync();
        Assert.Equal("result", run["event"]!.GetValue<string>());

        var updated = await connection.ReadUntilAsync(item =>
            item["event"]!.GetValue<string>() == "schedule.updated");
        Assert.Equal(0, updated["id"]!.GetValue<long>());
        var details = updated["details"]!.AsObject();
        Assert.Equal(id, details["taskId"]!.GetValue<string>());
        Assert.Equal("success", details["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(details["sessionId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ActiveChat_AcceptsSteeringWithoutCancellingTheTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(10, "session.new");
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(1, "chat", new JsonObject
        {
            ["message"] = "keep working",
            ["sessionId"] = sessionId,
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendAsync(2, "agent.steer", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["message"] = "also consider the edge cases",
        });
        var steering = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "result");
        Assert.Equal("guidance queued", steering["data"]!.GetValue<string>());

        await connection.SendAsync(3, "agent.cancel", new JsonObject { ["requestId"] = 1 });
        var cancelled = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "cancelled");
        Assert.Equal(sessionId, cancelled["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task MultipleExplicitSessions_RunConcurrently_AndCancelIndependently()
    {
        var startedCount = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgentTurnResult> SlowTurn(
            AgentSession session, WorkspaceInfo workspace, string message, CancellationToken ct)
        {
            if (Interlocked.Increment(ref startedCount) == 2) bothStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTurnResult("unreachable", false, null);
        }

        var connection = await StartServerAsync(SlowTurn);
        await connection.SendAsync(10, "session.new");
        var firstSession = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(11, "session.new");
        var secondSession = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(1, "chat", new JsonObject
        {
            ["message"] = "first",
            ["sessionId"] = firstSession,
        });
        await connection.SendAsync(2, "chat", new JsonObject
        {
            ["message"] = "second",
            ["sessionId"] = secondSession,
        });
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.SendAsync(3, "agent.cancel", new JsonObject { ["requestId"] = 1 });
        var firstCancelled = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 1
                        && response["event"]!.GetValue<string>() == "cancelled");
        Assert.Equal(firstSession, firstCancelled["sessionId"]!.GetValue<string>());

        await connection.SendAsync(4, "agent.cancel", new JsonObject { ["requestId"] = 2 });
        var secondCancelled = await connection.ReadUntilAsync(
            response => response["id"]!.GetValue<long>() == 2
                        && response["event"]!.GetValue<string>() == "cancelled");
        Assert.Equal(secondSession, secondCancelled["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChatError_ReturnsProviderDetailInsteadOfRuntimeSummary()
    {
        const string detail = "openai returned HTTP 401: Invalid API key supplied";
        var connection = await StartServerAsync((_, _, _, _) =>
        {
            _runtime!.Events.Publish(new ErrorEvent("LLM request failed", detail));
            return Task.FromResult(new AgentTurnResult("", false, detail));
        });

        await connection.SendAsync(4, "chat", new JsonObject { ["message"] = "hello" });
        var response = await connection.ReadAsync();

        Assert.Equal(4, response["id"]!.GetValue<long>());
        Assert.Equal("error", response["event"]!.GetValue<string>());
        Assert.Equal(detail, response["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task ImageOnlyChat_AcceptsMultipleValidatedAttachments()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = await StartServerAsync((_, _, message, _) =>
        {
            Assert.Equal("", message);
            invoked.SetResult();
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        });

        await connection.SendAsync(40, "chat", new JsonObject
        {
            ["images"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "one",
                    ["name"] = "one.png",
                    ["mediaType"] = "image/png",
                    ["data"] = "AQID",
                },
                new JsonObject
                {
                    ["id"] = "two",
                    ["name"] = "two.webp",
                    ["mediaType"] = "image/webp",
                    ["data"] = "BAUG",
                })
        });

        var response = await connection.ReadAsync();
        Assert.Equal("done", response["event"]!.GetValue<string>());
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ImageChat_RejectsUnsupportedMediaTypeBeforeStartingTurn()
    {
        var connection = await StartServerAsync((_, _, _, _) =>
            Task.FromResult(new AgentTurnResult("should not run", false, null)));

        await connection.SendAsync(41, "chat", new JsonObject
        {
            ["images"] = new JsonArray(new JsonObject
            {
                ["name"] = "vector.svg",
                ["mediaType"] = "image/svg+xml",
                ["data"] = "AQID",
            })
        });

        var response = await connection.ReadAsync();
        Assert.Equal("error", response["event"]!.GetValue<string>());
        Assert.Contains("Unsupported image type", response["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionGet_ReturnsPersistedImagesAndViewedReferences()
    {
        var workspace = CreateWorkspace("image-session");
        var connection = await StartServerAsync((_, _, _, _) =>
            Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);
        var session = _runtime!.Sessions.Create(_runtime.Workspace);
        _runtime.Sessions.Append(session, SeekClaw.Runtime.Providers.ChatMessage.User("inspect",
        [
            new SeekClaw.Runtime.Providers.ChatImageAttachment(
                "screen-1", "screen.png", "image/png", "AQID", 3),
        ]));
        _runtime.Sessions.Append(session, new SeekClaw.Runtime.Providers.ChatMessage
        {
            Role = SeekClaw.Runtime.Providers.ChatRole.Assistant,
            Text = "done",
            ViewedImages =
            [
                new SeekClaw.Runtime.Providers.ChatImageReference("screen-1", "screen.png"),
            ],
        });

        await connection.SendAsync(42, "session.get", new JsonObject
        {
            ["id"] = session.Header.Id,
            ["workspace"] = workspace,
        });

        var restored = ParseData(await connection.ReadAsync());
        var messages = restored["messages"]!.AsArray();
        Assert.Equal("screen.png", messages[0]!["images"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("AQID", messages[0]!["images"]![0]!["data"]!.GetValue<string>());
        Assert.Equal("screen-1", messages[1]!["viewedImages"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkspaceModeAndProtocolMethods_ReturnRuntimeStateAndPersistChanges()
    {
        var workspaceA = CreateWorkspace("workspace-a");
        var workspaceB = CreateWorkspace("workspace-b", "{\"mode\":\"edit\"}");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            workspaceA);

        await connection.SendAsync(1, "protocol.info");
        var protocol = ParseData(await connection.ReadAsync());
        Assert.Equal(DaemonServer.ProtocolVersion, protocol["version"]!.GetValue<string>());
        Assert.Contains("agent.cancel", protocol["methods"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Contains("agent.steer", protocol["methods"]!.AsArray().Select(node => node!.GetValue<string>()));

        await connection.SendAsync(2, "workspace.open", new JsonObject { ["path"] = workspaceB });
        var opened = ParseData(await connection.ReadAsync());
        Assert.Equal(Path.GetFullPath(workspaceB), opened["path"]!.GetValue<string>());

        await connection.SendAsync(3, "agent.mode.switch", new JsonObject { ["mode"] = "readonly" });
        var modeResponse = await connection.ReadAsync();
        Assert.Equal("readonly", modeResponse["data"]!.GetValue<string>());

        await connection.SendAsync(4, "agent.mode.get");
        var currentMode = await connection.ReadAsync();
        Assert.Equal("readonly", currentMode["data"]!.GetValue<string>());

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(workspaceB, ".seekclaw", "config.json")))!.AsObject();
        Assert.Equal("readonly", persisted["mode"]!.GetValue<string>());

        await connection.SendAsync(5, "workspace.open", new JsonObject
        {
            ["path"] = Path.Combine(_tempDir, "missing"),
        });
        var missing = await connection.ReadAsync();
        Assert.Equal("error", missing["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task AdministrativeMethods_ManageWorkspaceProvidersMcpSkillsAndDiagnostics()
    {
        var workspace = CreateWorkspace("admin-workspace", """
            {
              "mcp": {
                "servers": {
                  "inline-test": {
                    "transport": "stdio",
                    "command": "old-command",
                    "enabled": false
                  }
                }
              }
            }
            """);
        var skillDirectory = Path.Combine(workspace, "skills", "reviewer");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "prompt.txt"), "Review changes carefully.");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)),
            workspace);

        await connection.SendAsync(10, "workspace.init");
        var initialized = ParseData(await connection.ReadAsync());
        Assert.Equal(Path.GetFullPath(workspace), initialized["path"]!.GetValue<string>());

        await connection.SendAsync(11, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "openai",
            ["baseUrl"] = "http://localhost:11434/v1",
            ["apiKey"] = "secret-test-key",
            ["models"] = new JsonArray("test-model"),
        });
        var provider = ParseData(await connection.ReadAsync());
        Assert.True(provider["apiKeyConfigured"]!.GetValue<bool>());
        Assert.Equal("secret-test-key", provider["apiKey"]!.GetValue<string>());

        // The desktop receives the directly stored key for editing. Older clients that submit
        // an empty value still preserve it unless they explicitly request clearApiKey.
        await connection.SendAsync(111, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "openai",
            ["baseUrl"] = "http://localhost:11434/v1",
            ["apiKey"] = "",
            ["models"] = new JsonArray("test-model"),
        });
        provider = ParseData(await connection.ReadAsync());
        Assert.True(provider["apiKeyConfigured"]!.GetValue<bool>());
        Assert.Equal("secret-test-key", provider["apiKey"]!.GetValue<string>());
        var reloadedStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        Assert.Equal("secret-test-key", reloadedStore.Config.FindProvider("local")!.ApiKey);

        await connection.SendAsync(112, "provider.upsert", new JsonObject
        {
            ["id"] = "local",
            ["kind"] = "openai",
            ["baseUrl"] = "http://localhost:11434/v1",
            ["clearApiKey"] = true,
            ["models"] = new JsonArray("test-model"),
        });
        provider = ParseData(await connection.ReadAsync());
        Assert.False(provider["apiKeyConfigured"]!.GetValue<bool>());
        Assert.Null(provider["apiKey"]);

        await connection.SendAsync(12, "profile.upsert", new JsonObject
        {
            ["name"] = "desktop",
            ["provider"] = "local",
            ["model"] = "test-model",
            ["strategy"] = "balanced",
        });
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());
        await connection.SendAsync(13, "profile.use", new JsonObject { ["name"] = "desktop" });
        Assert.Equal("desktop", (await connection.ReadAsync())["data"]!.GetValue<string>());

        await connection.SendAsync(131, "model.update", new JsonObject
        {
            ["provider"] = "local",
            ["id"] = "test-model",
            ["contextWindow"] = 64_000,
            ["maxOutput"] = 4_096,
            ["vision"] = true,
        });
        var updatedModel = ParseData(await connection.ReadAsync());
        Assert.Equal(64_000, updatedModel["contextWindow"]!.GetValue<int>());
        Assert.True(updatedModel["vision"]!.GetValue<bool>());
        await connection.SendAsync(132, "model.catalog");
        var catalog = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(catalog, item => item!["ref"]!.GetValue<string>() == "local/test-model"
            && item["contextWindow"]!.GetValue<int>() == 64_000
            && item["capabilities"]!["vision"]!.GetValue<bool>());

        await connection.SendAsync(14, "mcp.upsert", new JsonObject
        {
            ["name"] = "inline-test",
            ["scope"] = "workspace",
            ["server"] = new JsonObject
            {
                ["transport"] = "stdio",
                ["command"] = "updated-command",
                ["enabled"] = false,
            },
        });
        var mcpServers = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(mcpServers, server => server!["name"]!.GetValue<string>() == "inline-test");
        Assert.Contains("updated-command", await File.ReadAllTextAsync(
            Path.Combine(workspace, ".seekclaw", "config.json")));

        await connection.SendAsync(15, "skill.list");
        var skills = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(skills, skill => skill!["name"]!.GetValue<string>() == "reviewer");
        await connection.SendAsync(16, "skill.toggle", new JsonObject
        {
            ["name"] = "reviewer",
            ["enabled"] = false,
        });
        skills = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(skills, skill =>
            skill!["name"]!.GetValue<string>() == "reviewer"
            && !skill["enabled"]!.GetValue<bool>());

        await connection.SendAsync(17, "usage.get");
        var usage = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.All(usage, item => Assert.NotNull(item!["model"]));
        await connection.SendAsync(18, "doctor.run");
        var checks = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.NotEmpty(checks);

        await connection.SendAsync(181, "project.upsert", new JsonObject
        {
            ["id"] = "desktop-project",
            ["path"] = workspace,
            ["name"] = "Desktop project",
        });
        var project = ParseData(await connection.ReadAsync());
        Assert.Equal("desktop-project", project["id"]!.GetValue<string>());
        await connection.SendAsync(182, "project.list");
        var projects = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(projects, item => item!["id"]!.GetValue<string>() == "desktop-project");

        await connection.SendAsync(19, "session.new", new JsonObject { ["reasoningLevel"] = "xhigh" });
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(20, "session.get", new JsonObject { ["id"] = sessionId });
        var session = ParseData(await connection.ReadAsync());
        Assert.Equal(sessionId, session["id"]!.GetValue<string>());
        Assert.Equal("xhigh", session["reasoningLevel"]!.GetValue<string>());

        await connection.SendAsync(21, "session.update", new JsonObject
        {
            ["id"] = sessionId,
            ["title"] = "Desktop task",
            ["workspace"] = workspace,
            ["reasoningLevel"] = "ultra",
        });
        session = ParseData(await connection.ReadAsync());
        Assert.Equal("Desktop task", session["title"]!.GetValue<string>());
        Assert.Equal("ultra", session["reasoningLevel"]!.GetValue<string>());

        await connection.SendAsync(22, "session.archive", new JsonObject
        {
            ["id"] = sessionId,
            ["workspace"] = workspace,
        });
        session = ParseData(await connection.ReadAsync());
        Assert.True(session["archived"]!.GetValue<bool>());

        await connection.SendAsync(23, "session.list", new JsonObject
        {
            ["workspace"] = workspace,
            ["includeArchived"] = true,
        });
        var sessions = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(sessions, item => item!["id"]!.GetValue<string>() == sessionId);

        await connection.SendAsync(24, "session.delete", new JsonObject
        {
            ["id"] = sessionId,
            ["workspace"] = workspace,
        });
        Assert.Equal(sessionId, (await connection.ReadAsync())["data"]!.GetValue<string>());

        await connection.SendAsync(24_1, "session.new", new JsonObject { ["workspace"] = workspace });
        var projectSessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(25, "project.remove", new JsonObject { ["id"] = "desktop-project" });
        Assert.Equal("desktop-project", (await connection.ReadAsync())["data"]!.GetValue<string>());
        await connection.SendAsync(26, "session.get", new JsonObject
        {
            ["id"] = projectSessionId,
            ["workspace"] = workspace,
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectUpsert_RejectsUserProfileAndSeekClawStateDirectories()
    {
        var workspace = CreateWorkspace("forbidden-project");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);

        await connection.SendAsync(1, "project.upsert", new JsonObject
        {
            ["id"] = "home-project",
            ["path"] = Path.GetDirectoryName(SeekClawPaths.Home)!,
            ["name"] = "Home",
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        await connection.SendAsync(2, "project.upsert", new JsonObject
        {
            ["id"] = "state-project",
            ["path"] = SeekClawPaths.Home,
            ["name"] = "State",
        });
        Assert.Equal("error", (await connection.ReadAsync())["event"]!.GetValue<string>());

        // A normal project directory still registers fine.
        await connection.SendAsync(3, "project.upsert", new JsonObject
        {
            ["id"] = "ok-project",
            ["path"] = workspace,
            ["name"] = "OK",
        });
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task RemoveProject_WithKeepSessions_PreservesSessionsInDatabase()
    {
        var workspace = CreateWorkspace("keep-sessions");
        var connection = await StartServerAsync(
            (_, _, _, _) => Task.FromResult(new AgentTurnResult("ok", false, null)), workspace);

        await connection.SendAsync(1, "project.upsert", new JsonObject
        {
            ["id"] = "keep-project",
            ["path"] = workspace,
            ["name"] = "Keep",
        });
        Assert.Equal("result", (await connection.ReadAsync())["event"]!.GetValue<string>());

        await connection.SendAsync(2, "session.new", new JsonObject { ["workspace"] = workspace });
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(3, "project.remove", new JsonObject
        {
            ["id"] = "keep-project",
            ["keepSessions"] = true,
        });
        Assert.Equal("keep-project", (await connection.ReadAsync())["data"]!.GetValue<string>());

        await connection.SendAsync(4, "session.get", new JsonObject
        {
            ["id"] = sessionId,
            ["workspace"] = workspace,
        });
        var response = await connection.ReadAsync();
        Assert.Equal("result", response["event"]!.GetValue<string>());
        Assert.Equal(sessionId, ParseData(response)["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task GlobalSessions_RunWithoutAProjectAndRemainSeparateFromWorkspaceSessions()
    {
        var observedGlobalContext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = CreateWorkspace("project-sessions");
        var connection = await StartServerAsync((session, context, _, _) =>
        {
            observedGlobalContext.SetResult();
            Assert.True(context.IsGlobal);
            Assert.Null(session.Header.Workspace);
            return Task.FromResult(new AgentTurnResult("ok", false, null));
        }, workspace);

        await connection.SendAsync(30, "session.new", new JsonObject { ["global"] = true });
        var globalId = (await connection.ReadAsync())["data"]!.GetValue<string>();

        await connection.SendAsync(31, "session.get", new JsonObject
        {
            ["id"] = globalId,
            ["global"] = true,
        });
        var globalSession = ParseData(await connection.ReadAsync());
        Assert.Null(globalSession["workspace"]);

        await connection.SendAsync(32, "session.list", new JsonObject { ["global"] = true });
        var globalSessions = JsonNode.Parse((await connection.ReadAsync())["data"]!.GetValue<string>())!.AsArray();
        Assert.Contains(globalSessions, item => item!["id"]!.GetValue<string>() == globalId);

        await connection.SendAsync(33, "session.list", new JsonObject { ["workspace"] = workspace });
        var projectResponse = await connection.ReadAsync();
        Assert.True(projectResponse["event"]!.GetValue<string>() == "result", projectResponse["data"]!.GetValue<string>());
        var projectSessions = JsonNode.Parse(projectResponse["data"]!.GetValue<string>())!.AsArray();
        Assert.DoesNotContain(projectSessions, item => item!["id"]!.GetValue<string>() == globalId);

        await connection.SendAsync(34, "chat", new JsonObject
        {
            ["message"] = "hello without a directory",
            ["global"] = true,
        });
        Assert.Equal("done", (await connection.ReadAsync())["event"]!.GetValue<string>());
        await observedGlobalContext.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<TestConnection> StartServerAsync(
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>> runTurn,
        string? workspace = null)
    {
        workspace ??= CreateWorkspace("workspace");
        var configStore = new ConfigStore(
            Path.Combine(_tempDir, "config.json"),
            Path.Combine(_tempDir, "state.json"));
        _runtime = SeekClawRuntime.Create(
            workspace,
            configStore,
            Path.Combine(_tempDir, "seekclaw.db"),
            new OfflineHealthChecker(new HealthChecker(new LlmHttpFactory(), configStore)));
        var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-state"));
        var server = new DaemonServer(_runtime, runTurn, globalWorkspace);
        _asyncDisposables.Add(server);

        var pipeName = $"seekclaw-test-{Guid.NewGuid():N}";
        var serverPipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var clientPipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync(_serverCts.Token);
            await server.HandleConnectionAsync(serverPipe, _serverCts.Token);
        });
        await clientPipe.ConnectAsync(_serverCts.Token);

        var connection = new TestConnection(clientPipe, serverPipe, serverTask);
        _asyncDisposables.Add(connection);
        return connection;
    }

    private string CreateWorkspace(string name, string? workspaceConfig = null)
    {
        var root = Path.Combine(_tempDir, name);
        var configDir = Path.Combine(root, ".seekclaw");
        Directory.CreateDirectory(configDir);
        if (workspaceConfig is not null)
            File.WriteAllText(Path.Combine(configDir, "config.json"), workspaceConfig);
        return root;
    }

    private static JsonObject ParseData(JsonObject response) =>
        JsonNode.Parse(response["data"]!.GetValue<string>())!.AsObject();

    public async ValueTask DisposeAsync()
    {
        _serverCts.Cancel();
        foreach (var disposable in _asyncDisposables)
        {
            try { await disposable.DisposeAsync(); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
        if (_runtime is not null) await _runtime.DisposeAsync();
        _serverCts.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Keeps daemon tests hermetic: local <see cref="IHealthChecker.RunChecks"/> still run,
    /// but provider probes never touch the network. A filtered/absent localhost port could
    /// otherwise stall <c>doctor.run</c> past the test read timeout and flake the build.
    /// </summary>
    private sealed class OfflineHealthChecker(IHealthChecker inner) : IHealthChecker
    {
        public Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default)
            => Task.FromResult(new HealthReport(provider.Id, false, 0, "offline (test stub)"));

        public IReadOnlyList<HealthCheckResult> RunChecks(WorkspaceInfo workspace)
            => inner.RunChecks(workspace);
    }

    private sealed class TestConnection(
        NamedPipeClientStream client,
        NamedPipeServerStream server,
        Task serverTask) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(client, Encoding.UTF8, leaveOpen: true);
        private readonly StreamWriter _writer = new(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        public Task SendAsync(long id, string method, JsonObject? parameters = null)
        {
            var request = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            };
            return _writer.WriteLineAsync(request.ToJsonString());
        }

        public async Task<JsonObject> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await _reader.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(line));
            return JsonNode.Parse(line)!.AsObject();
        }

        public async Task<JsonObject> ReadUntilAsync(Func<JsonObject, bool> predicate)
        {
            while (true)
            {
                var response = await ReadAsync();
                if (predicate(response)) return response;
            }
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            server.Dispose();
            _reader.Dispose();
            await _writer.DisposeAsync();
        }
    }
}
