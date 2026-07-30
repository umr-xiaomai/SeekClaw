using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Daemon;
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
        var busyWorkspace = await connection.ReadAsync();
        Assert.Equal(3, busyWorkspace["id"]!.GetValue<long>());
        Assert.Equal("error", busyWorkspace["event"]!.GetValue<string>());

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

        await connection.SendAsync(19, "session.new");
        var sessionId = (await connection.ReadAsync())["data"]!.GetValue<string>();
        await connection.SendAsync(20, "session.get", new JsonObject { ["id"] = sessionId });
        var session = ParseData(await connection.ReadAsync());
        Assert.Equal(sessionId, session["id"]!.GetValue<string>());

        await connection.SendAsync(21, "session.update", new JsonObject
        {
            ["id"] = sessionId,
            ["title"] = "Desktop task",
            ["workspace"] = workspace,
        });
        session = ParseData(await connection.ReadAsync());
        Assert.Equal("Desktop task", session["title"]!.GetValue<string>());

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
        _runtime = SeekClawRuntime.Create(workspace, configStore);
        var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(_tempDir, "global-state"));
        var server = new DaemonServer(_runtime, runTurn, globalWorkspace);

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
