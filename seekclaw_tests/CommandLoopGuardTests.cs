using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class CommandLoopGuardTests
{
    [Fact]
    public async Task AgentStopsSshRetriesAcrossToolsAndCompletesSkippedToolResults()
    {
        var root = Directory.CreateTempSubdirectory("seekclaw-command-loop-");
        try
        {
            var workspace = new WorkspaceInfo { Root = root.FullName, ProjectKinds = [] };
            var client = new ScriptedClient(
                [Call("start", "exec_command", ("cmd", "ssh fixture@host.example"))],
                [Call("shell", "bash", ("command", "$prep = 'fixture'; ssh.exe -o ConnectTimeout=1 fixture@HOST.EXAMPLE uptime"))],
                [Call("retry", "command_session", ("action", "start"), ("command", """
                    $prep = @'
                    ssh a-different-host.example
                    '@
                    ssh -p 2222 fixture@host.example $prep
                    """)),
                    Call("skipped", "write_file", ("path", "must-not-exist.txt"), ("content", "not authorized"))]);

            await using var runtime = Runtime(workspace, client, new InMemorySessionStore());
            var session = runtime.Sessions.Create(workspace);
            var approvedCalls = new List<string>();
            runtime.Approvals.SetHandler((request, _) =>
            {
                approvedCalls.Add(request.CallId);
                return ValueTask.FromResult(ToolApprovalDecision.Deny("Test fixture; do not launch a process."));
            });

            using var subscription = runtime.Events.Subscribe();
            var result = await runtime.Agent.RunTurnAsync(session, workspace, "Manage the fixture connection", CancellationToken.None);

            Assert.Equal(["start", "shell", "retry"], approvedCalls);
            Assert.Equal(3, client.RequestCount);
            Assert.Contains("同一 SSH 主机", result.Error);
            Assert.Equal(result.Error, result.Text);
            Assert.False(result.Cancelled);
            Assert.Equal(result.Text, session.Messages.Last().Text);
            Assert.Equal(["start", "shell", "retry", "skipped"], session.Messages
                .Where(message => message.Role == ChatRole.Tool).Select(message => message.ToolCallId!).ToArray());
            Assert.Contains("not executed", session.Messages.Single(message => message.ToolCallId == "skipped").Text);
            Assert.False(File.Exists(Path.Combine(root.FullName, "must-not-exist.txt")));

            var completions = 0;
            while (subscription.Reader.TryRead(out var evt))
                if (evt is TurnCompletedEvent { Error: not null }) completions++;
            Assert.Equal(1, completions);
        }
        finally
        {
            // The fixture database may still have a pooled connection on Windows.
            try { root.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task AgentOnlySendsRepeatedInputOnceToALocalProcess()
    {
        var root = Directory.CreateTempSubdirectory("seekclaw-input-guard-");
        try
        {
            var workspace = new WorkspaceInfo { Root = root.FullName, ProjectKinds = [] };
            var sessions = new InMemorySessionStore();
            var session = sessions.Create(workspace);
            await using var commands = new CommandSessionManager();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            var command = OperatingSystem.IsWindows()
                ? "while ($null -ne ($line = [Console]::ReadLine())) { Add-Content -LiteralPath received.txt -Value $line; [Console]::WriteLine('received') }"
                : "while IFS= read -r line; do printf '%s\\n' \"$line\" >> received.txt; printf 'received\\n'; done";
            var started = await commands.StartAsync(session.Header.Id, command, root.FullName, 30, ct: token);
            var id = started["sessionId"]!.GetValue<string>();
            var send = new ToolCallRequest("send", "write_stdin", new JsonObject
            {
                ["session_id"] = id, ["chars"] = "one line\n", ["yield_time_ms"] = 8_000,
            }.ToJsonString());
            var replay = Call("replay", "command_session", ("session_id", id), ("action", "write"), ("text", "one line\r\n"));
            var client = new ScriptedClient([send, replay]);
            await using var runtime = Runtime(workspace, client, sessions, commands);
            var approvedCalls = new List<string>();
            runtime.Approvals.SetHandler((request, _) =>
            {
                approvedCalls.Add(request.CallId);
                return ValueTask.FromResult(ToolApprovalDecision.Allow);
            });

            var result = await runtime.Agent.RunTurnAsync(session, workspace, "Send one line to the fixture", token);
            Assert.Null(result.Error);
            Assert.Equal(["send"], approvedCalls);
            Assert.Contains("received", session.Messages.Single(message => message.ToolCallId == "send").Text);
            Assert.Contains("already sent", session.Messages.Single(message => message.ToolCallId == "replay").Text);
            Assert.Equal(["one line"], await File.ReadAllLinesAsync(Path.Combine(root.FullName, "received.txt"), token));
        }
        finally
        {
            try { root.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void IncrementalOutputKeepsSessionAliveAndStaleReadsHandItBack()
    {
        var guard = new CommandLoopGuard();
        const string id = "fixture-session";
        var start = Call("start", "exec_command", ("cmd", "fixture"));
        guard.Observe(start, SessionPage(start, id, "running", 1, "ready"));

        // Repeated output at a newer cursor can be valid progress, such as a heartbeat.
        for (var cursor = 2; cursor <= 20; cursor++)
        {
            var call = Read(id, "write_stdin", cursor - 1);
            Assert.Equal(call, guard.Prepare(call, out var rejection));
            Assert.Null(rejection);
            guard.Observe(call, SessionPage(call, id, "running", cursor, "heartbeat"));
        }
        Assert.Null(guard.StopReason);

        // Replaying older cursors is rejected even after changing the tool name.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            guard.Prepare(Read(id, attempt % 2 == 0 ? "command_session" : "write_stdin", 10 + attempt), out var rejection);
            Assert.Contains("stale", rejection);
        }
        Assert.Contains(id, guard.StopReason);
    }

    [Fact]
    public void AQuietProcessMayWaitForManyPollsAndThenExit()
    {
        const string id = "quiet-session";
        var guard = new CommandLoopGuard();
        var start = Call("start", "exec_command", ("cmd", "fixture"));
        guard.Observe(start, SessionPage(start, id, "running", 0, ""));

        var previousWait = 0;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var tool = attempt % 2 == 0 ? "command_session" : "write_stdin";
            var poll = guard.Prepare(Read(id, tool, 0), out var rejection);
            Assert.Null(rejection);
            var args = JsonNode.Parse(poll.ArgumentsJson)!;
            var wait = args[tool == "write_stdin" ? "yield_time_ms" : "wait_ms"]?.GetValue<int>() ?? 0;
            if (attempt > 0) Assert.True(wait >= 2_000);
            Assert.True(wait >= previousWait);
            previousWait = wait;
            guard.Observe(poll, SessionPage(poll, id, "running", 0, ""), TimeSpan.FromMilliseconds(wait));
        }
        var read = Read(id, "write_stdin", 0);
        guard.Observe(read, SessionPage(read, id, "exited", 4, "done"));
        Assert.Null(guard.StopReason);
    }

    [Fact]
    public void EmptyWaitsThatReturnImmediatelyDoNotKeepATurnAlive()
    {
        const string id = "fixture-session";
        var guard = new CommandLoopGuard();
        var start = Call("start", "exec_command", ("cmd", "fixture"));
        guard.Observe(start, SessionPage(start, id, "running", 0, ""));

        guard.Observe(Read(id, "write_stdin", 0), SessionPage(start, id, "running", 0, ""));
        for (var cursor = 0; cursor < 3; cursor++)
        {
            var call = guard.Prepare(Read(id, "write_stdin", cursor), out var rejection);
            Assert.Null(rejection);
            guard.Observe(call, SessionPage(call, id, "running", cursor + 1, " "), TimeSpan.Zero);
        }
        Assert.Contains(id, guard.StopReason);
        Assert.Contains("提前结束等待", guard.StopReason);
    }

    [Fact]
    public void DuplicateInputIsBlockedBeforeExecutionUntilAnotherPromptIsSeen()
    {
        const string id = "fixture-session";
        var guard = new CommandLoopGuard();
        var start = Call("start", "exec_command", ("cmd", "fixture"));
        guard.Observe(start, SessionPage(start, id, "running", 9, "Continue?"));
        var first = Call("input", "write_stdin", ("session_id", id), ("chars", "go\n"));
        Assert.Equal(first, guard.Prepare(first, out var rejection));
        Assert.Null(rejection);
        guard.Observe(first, SessionPage(first, id, "running", 24, "go\nprocessing\n"));

        var replay = Call("replay", "command_session", ("session_id", id), ("action", "write"), ("text", "go\r\n"));
        guard.Prepare(replay, out rejection);
        Assert.Contains("already sent", rejection);
        var read = Read(id, "command_session", 24);
        guard.Observe(read, SessionPage(read, id, "running", 37, "Confirm now? "));
        Assert.Equal(replay, guard.Prepare(replay, out rejection));
        Assert.Null(rejection);
        guard.Observe(replay, SessionPage(replay, id, "running", 37, ""));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            guard.Prepare(attempt % 2 == 0 ? first : replay, out rejection);
            Assert.Contains("already sent", rejection);
        }
        Assert.Contains(id, guard.StopReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedShellPromptAcceptsTheSameCommandWhenItAppearsOnANewLine(bool splitPage)
    {
        const string id = "fixture-session";
        const string prompt = " remote$ ";
        const string input = "true\n";
        var guard = new CommandLoopGuard();
        var start = Call("start", "exec_command", ("cmd", "fixture"));
        guard.Observe(start, SessionPage(start, id, "running", prompt.Length, prompt));
        var send = Call("input", "write_stdin", ("session_id", id), ("chars", input));
        var cursor = prompt.Length + input.Length;
        if (splitPage)
        {
            guard.Observe(send, SessionPage(send, id, "running", cursor, input));
            guard.Prepare(send, out var beforePrompt);
            Assert.Contains("already sent", beforePrompt);
            var poll = Read(id, "write_stdin", cursor);
            guard.Observe(poll, SessionPage(poll, id, "running", cursor + 1, prompt[..1], hasMore: true));
            var next = Read(id, "write_stdin", cursor + 1);
            guard.Observe(next, SessionPage(next, id, "running", cursor + prompt.Length, prompt[1..]));
        }
        else
        {
            guard.Observe(send, SessionPage(send, id, "running", cursor + prompt.Length, input + prompt));
        }

        guard.Prepare(send, out var afterPrompt);
        Assert.Null(afterPrompt);
        Assert.Null(guard.StopReason);
    }

    [Fact]
    public void BufferedWhitespacePagesAreDrainedWithoutStoppingAQuietSession()
    {
        const string id = "fixture-session";
        var guard = new CommandLoopGuard();
        var start = Call("start", "exec_command", ("cmd", "fixture"));
        guard.Observe(start, SessionPage(start, id, "running", 0, ""));
        var poll = new ToolCallRequest("poll", "write_stdin", new JsonObject
        {
            ["session_id"] = id, ["yield_time_ms"] = 2000,
        }.ToJsonString());

        // A buffered page returns immediately regardless of the requested wait.
        for (var cursor = 1; cursor <= 4; cursor++)
        {
            var read = guard.Prepare(poll, out var rejection);
            Assert.Null(rejection);
            guard.Observe(read, SessionPage(read, id, "running", cursor, " ", hasMore: true), TimeSpan.Zero);
        }
        Assert.Null(guard.StopReason);
    }

    [Fact]
    public void RealFileChangesAllowVerificationRetriesAndDoNotRestartSshBudget()
    {
        var guard = new CommandLoopGuard();
        var build = Call("build", "bash", ("command", "dotnet build"));
        var ssh = Call("ssh", "exec_command", ("cmd", "ssh fixture@host.example"));
        var echo = Call("echo", "bash", ("command", "echo attempting"));
        for (var attempt = 0; attempt < 6; attempt++)
        {
            guard.Observe(build, ChatMessage.ToolResult(build.Id, build.Name, "failed", false));
            guard.RecordWorkspaceChange();
        }
        Assert.Null(guard.StopReason);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            guard.Observe(echo, ChatMessage.ToolResult(echo.Id, echo.Name, "attempting", true));
            guard.RecordWorkspaceChange();
            guard.Observe(ssh, SessionPage(ssh, $"session-{attempt}", "running", 1, "prompt"));
        }
        Assert.Contains("同一 SSH 主机", guard.StopReason);
        Assert.Contains("session-2", guard.StopReason);
    }

    private static SeekClawRuntime Runtime(WorkspaceInfo workspace, ILlmClientFactory client, ISessionStore sessions,
        CommandSessionManager? commands = null)
    {
        var root = workspace.Root;
        var config = new ConfigStore(Path.Combine(root, "config.json"), Path.Combine(root, "state.json"));
        config.Config.Providers = [new ProviderConfig
        {
            Id = "test", Kind = "openai", BaseUrl = "https://example.invalid/v1",
            Models = [new ModelConfig { Id = "model", ContextWindow = 16_000, MaxOutput = 256 }],
        }];
        config.Config.Provider = "test";
        config.Config.Model = "model";
        config.Config.Agent.Mode = "guardrail";
        config.Config.Agent.AutoVerify = false;
        config.Config.Routing.DeepSeekOptimizationEnabled = false;
        var database = new SeekClawDatabase(Path.Combine(root, "data.db"));
        return SeekClawRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config)
                .AddSingleton(database)
                .AddSingleton(sessions)
                .AddSingleton(client)
                .AddSingleton<IUsageTracker>(provider =>
                    new UsageTracker(provider.GetRequiredService<IEventBus>(), Path.Combine(root, "usage.jsonl")));
            if (commands is not null) services.AddSingleton(commands);
        });
    }

    private static ToolCallRequest Read(string id, string tool, int cursor) => new("read", tool, new JsonObject
    {
        ["session_id"] = id,
        ["action"] = "read",
        ["cursor"] = cursor,
    }.ToJsonString());

    private static ToolCallRequest Call(string id, string tool, params (string Key, string Value)[] args) =>
        new(id, tool, new JsonObject(args.Select(pair =>
            KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))).ToJsonString());

    private static ChatMessage SessionPage(ToolCallRequest call, string id, string status, int cursor, string output,
        bool hasMore = false) =>
        ChatMessage.ToolResult(call.Id, call.Name, new JsonObject
        {
            ["sessionId"] = id,
            ["status"] = status,
            ["exitCode"] = status == "exited" ? 0 : null,
            ["cursor"] = cursor,
            ["hasMore"] = hasMore,
            ["output"] = output,
        }.ToJsonString(), true);

    private sealed class ScriptedClient(params ToolCallRequest[][] steps) : ILlmClientFactory, ILlmClient
    {
        public int RequestCount { get; private set; }
        public string Kind => "openai";
        public ILlmClient GetClient(string kind) => this;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var step = RequestCount++;
            await Task.Yield();
            yield return new LlmCompleted(step < steps.Length
                ? new LlmCompletion { ToolCalls = steps[step], FinishReason = "tool_calls" }
                : new LlmCompletion { Text = "Unexpected extra model request.", FinishReason = "stop" });
        }
    }
}
