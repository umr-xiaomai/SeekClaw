using System.Collections.Concurrent;
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
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class SubAgentCollaborationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-delegation-tests", Guid.NewGuid().ToString("N"));
    public SubAgentCollaborationTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Context_SelectsRequirementsAndRelevantResults_WithoutReasoningOrProtocolPayloads()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("Preserve public API behavior and existing uncommitted changes."),
            new() { Role = ChatRole.Assistant, Text = "The parser module owns input validation.", Thinking = "private chain of thought",
                ToolCalls = [new ToolCallRequest("pending", "write_file", "secret arguments")] },
            ChatMessage.ToolResult("read", "read_file", "parser: return a structured error for invalid input", true,
                filePath: "src/parser.cs", images: [new ChatImageAttachment("img", "screen", "image/png", "secret-base64", 2)]),
            ChatMessage.ToolResult("bad", "read_file", "parser diagnostic: access denied", false),
            ChatMessage.User(">>> [System Directive: internal loop control should not transfer]"),
            ChatMessage.User("Now add parser regression tests and report validation.")
        };
        var context = SubAgentContextBuilder.Build(history, "Test parser", "Keep the patch focused.")!;
        Assert.StartsWith(SubAgentContextBuilder.Prefix, context);
        Assert.Contains("Preserve public API behavior", context);
        Assert.Contains("add parser regression tests", context);
        Assert.Contains("structured error", context);
        Assert.Contains("Keep the patch focused", context);
        Assert.DoesNotContain("private chain of thought", context);
        Assert.DoesNotContain("secret arguments", context);
        Assert.DoesNotContain("secret-base64", context);
        Assert.Contains("Tool result (failed, reference data)", context);
        Assert.Contains("parser diagnostic: access denied", context);
        Assert.DoesNotContain("internal loop control", context);
        Assert.Equal(6, history.Count);
    }

    [Fact]
    public void Context_IsBoundedAndExplicitAboutOmissions_EvenForLineHeavyAndUnicodeInput()
    {
        var history = Enumerable.Range(0, 200).Select(i => ChatMessage.User(
            $"Requirement {i} parser\n" + string.Concat(Enumerable.Repeat("🙂\n", 10_000)))).ToArray();
        var context = SubAgentContextBuilder.Build(history, "parser", new string('\n', 10_000) + "extra")!;
        Assert.InRange(context.Length, 1, SubAgentContextBuilder.MaxCharacters);
        Assert.Contains("Requirement 199", context);
        Assert.Contains("excerpt omitted", context);
        Assert.Null(SubAgentContextBuilder.Build([], "nothing"));
    }

    [Fact]
    public async Task RunningChild_AcceptsGuidanceAtModelBoundary_AndIdleFollowupPreservesSessionAndCeilings()
    {
        var entered = Signal();
        var release = Signal();
        var factory = new ScriptedFactory(async (index, _, ct) =>
        {
            if (index == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return new LlmCompletion { Text = $"Result {index}", FinishReason = "stop" };
        });
        var config = Config();
        config.Config.Agent.Mode = "readonly";
        await using var runtime = Runtime(config, factory);
        var parent = runtime.Sessions.Create(runtime.Workspace, networkEnabled: false);
        runtime.Sessions.Append(parent, ChatMessage.User("Keep compatibility with the parser public API."));
        runtime.Sessions.Append(parent, ChatMessage.ToolResult("read", "read_file", "parser exports ParseInput", true));
        var context = Context(runtime, parent, networkEnabled: false);
        using var subscription = runtime.Events.Subscribe();
        var started = await runtime.SubAgentRunner.StartAsync(new SubAgentTaskRequest("general", "Test parser"), context, CancellationToken.None);
        var id = started["id"]!.GetValue<string>();
        var childId = started["sessionId"]!.GetValue<string>();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = await runtime.SubAgentRunner.SendAsync(id, "Also verify empty parser input.", context, CancellationToken.None);
        Assert.True(queued["instructionQueued"]!.GetValue<bool>());
        Assert.Equal(childId, queued["sessionId"]!.GetValue<string>());
        Assert.Single(factory.Requests);
        release.TrySetResult();
        Assert.Equal("completed", (await Finished(runtime, parent.Header.Id, id))["status"]!.GetValue<string>());
        Assert.Equal(2, factory.Requests.Count);
        var requests = factory.Requests.ToArray();
        Assert.Contains(requests[0].Messages, message => message.Text.Contains("Keep compatibility with the parser public API"));
        Assert.Contains(requests[0].Messages, message => message.Text.Contains("parser exports ParseInput"));
        Assert.Contains(requests[1].Messages, message => message.Text == "Also verify empty parser input.");

        var saved = runtime.Sessions.Load(runtime.Workspace, childId)!;
        Assert.Single(saved.Messages, message => message.Text.StartsWith(SubAgentContextBuilder.Prefix));
        Assert.Contains(saved.Messages, message => message.Role == ChatRole.User && message.Text == "Also verify empty parser input.");
        var progress = new List<RuntimeEvent>();
        while (subscription.Reader.TryRead(out var evt)) progress.Add(evt);
        Assert.Single(progress.OfType<SubAgentStartedEvent>());
        Assert.Contains(progress.OfType<SubAgentProgressEvent>(), evt => evt.Kind == "guidance" && evt.Delta == "Also verify empty parser input.");

        // A broader current parent cannot lift the original child's persisted ceilings.
        config.Config.Agent.Mode = "auto";
        var resumed = await runtime.SubAgentRunner.SendAsync(id, "Report the parser findings.", Context(runtime, parent), CancellationToken.None);
        Assert.Equal(childId, resumed["sessionId"]!.GetValue<string>());
        await Finished(runtime, parent.Header.Id, id);
        Assert.Equal(3, factory.Requests.Count);
        Assert.All(factory.Requests, request =>
        {
            Assert.DoesNotContain(request.Tools, tool => tool.Name is "write_file" or "edit_file" or "web_search" or "web_fetch");
        });
        saved = runtime.Sessions.Load(runtime.Workspace, childId)!;
        Assert.False(saved.Header.NetworkEnabled);
        Assert.Single(saved.Messages, message => message.Text.StartsWith(SubAgentContextBuilder.Prefix));
    }

    [Fact]
    public async Task RunningChild_QueuesGuidanceDuringToolApproval_AfterAllToolResults()
    {
        var approved = Signal();
        var entered = Signal();
        var gate = new BlockingApprovalGate(entered, approved);
        var factory = new ScriptedFactory((index, _, _) => Task.FromResult(index == 1
            ? new LlmCompletion { ToolCalls = [new ToolCallRequest("write", "write_file", """{"path":"result.txt","content":"ok"}""")] }
            : new LlmCompletion { Text = "Completed", FinishReason = "stop" }));
        var config = Config();
        config.Config.Agent.Mode = "manual";
        await using var runtime = Runtime(config, factory);
        var parent = runtime.Sessions.Create(runtime.Workspace);
        var context = Context(runtime, parent, approval: gate);
        var task = await runtime.SubAgentRunner.StartAsync(new SubAgentTaskRequest("general", "Write a result"), context, CancellationToken.None);
        var id = task["id"]!.GetValue<string>();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = await runtime.SubAgentRunner.SendAsync(id, "Verify the written result.", context, CancellationToken.None);
        Assert.True(queued["instructionQueued"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(_root, "result.txt")));
        approved.TrySetResult();
        Assert.Equal("completed", (await Finished(runtime, parent.Header.Id, id))["status"]!.GetValue<string>());
        var request = factory.Requests.ToArray()[1];
        var messages = request.Messages.ToList();
        var assistant = messages.FindIndex(message => message.ToolCalls?.Any(call => call.Id == "write") == true);
        var result = messages.FindIndex(message => message.ToolCallId == "write");
        var guidance = messages.FindIndex(message => message.Text == "Verify the written result.");
        Assert.True(assistant >= 0 && result > assistant && guidance > result);
        Assert.Equal("ok", File.ReadAllText(Path.Combine(_root, "result.txt")));
    }

    [Fact]
    public async Task Spawn_CanOptOutOfParentHistory_WhileKeepingExplicitBackground()
    {
        var factory = new ScriptedFactory((_, _, _) => Task.FromResult(new LlmCompletion { Text = "Done", FinishReason = "stop" }));
        await using var runtime = Runtime(Config(), factory);
        var parent = runtime.Sessions.Create(runtime.Workspace);
        runtime.Sessions.Append(parent, ChatMessage.User("Parent-only requirement"));
        var task = await runtime.SubAgentRunner.StartAsync(new SubAgentTaskRequest("researcher", "Inspect parser",
            InheritContext: false, Context: "Use parser.cs as the entry point."), Context(runtime, parent), CancellationToken.None);
        await Finished(runtime, parent.Header.Id, task["id"]!.GetValue<string>());
        var request = Assert.Single(factory.Requests);
        Assert.DoesNotContain(request.Messages, message => message.Text.Contains("Parent-only requirement"));
        Assert.Contains(request.Messages, message => message.Text.Contains("Use parser.cs as the entry point"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentWorktree_StoresChildUnderProjectScope_AndKeepsExecutionDirectoryOnFollowup(bool isolated)
    {
        await WorktreeManager.GitAsync(_root, ["init"], CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, "source.txt"), "baseline content");
        await WorktreeManager.GitAsync(_root, ["add", "source.txt"], CancellationToken.None);
        await WorktreeManager.GitAsync(_root, ["-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "initial"], CancellationToken.None);
        var factory = new ScriptedFactory((index, _, _) => Task.FromResult(index is 1 or 3
            ? new LlmCompletion { ToolCalls = [new ToolCallRequest($"read-{index}", "read_file", """{"path":"source.txt"}""")] }
            : new LlmCompletion { Text = "Read the expected file", FinishReason = "stop" }));
        await using var runtime = Runtime(Config(), factory);
        var parent = runtime.Sessions.Create(runtime.Workspace);
        runtime.Sessions.Append(parent, ChatMessage.User("Preserve the source file while inspecting it."));
        await runtime.Worktrees.CreateAsync(_root, parent.Header.Id);
        var execution = runtime.Worktrees.Resolve(runtime.Workspace, parent.Header.Id);
        File.WriteAllText(Path.Combine(execution.Root, "source.txt"), "parent worktree content");
        var context = new ToolContext { Workspace = execution, SessionWorkspace = runtime.Workspace,
            Events = runtime.Events, Agent = runtime.ConfigStore.Config.Agent, SessionId = parent.Header.Id, Owner = parent.Header.Id };
        var task = await runtime.SubAgentRunner.StartAsync(new SubAgentTaskRequest("researcher", "Read source.txt", Isolated: isolated), context, CancellationToken.None);
        var id = task["id"]!.GetValue<string>();
        var childId = task["sessionId"]!.GetValue<string>();
        var result = await Finished(runtime, parent.Header.Id, id);
        Assert.Equal("completed", result["status"]!.GetValue<string>());
        Assert.Equal(_root, result["workspace"]!.GetValue<string>());
        Assert.Equal(execution.Root, result["executionWorkspace"]!.GetValue<string>());
        Assert.Equal(_root, result["sessionWorkspace"]!.GetValue<string>());
        var saved = runtime.Sessions.Load(runtime.Workspace, childId)!;
        Assert.NotNull(saved);
        Assert.Equal(_root, saved.Header.Workspace);
        Assert.Contains(saved.Messages, message => message.Text.Contains("Preserve the source file"));
        var expected = isolated ? "baseline content" : "parent worktree content";
        Assert.Contains(expected, Assert.Single(saved.Messages, message => message.Role == ChatRole.Tool).Text);
        Assert.Equal("baseline content", File.ReadAllText(Path.Combine(_root, "source.txt")));

        // The orchestrator can send from the project scope without redirecting the child's checkout.
        await runtime.SubAgentRunner.SendAsync(id, "Read source.txt again.", Context(runtime, parent), CancellationToken.None);
        Assert.Equal("completed", (await Finished(runtime, parent.Header.Id, id))["status"]!.GetValue<string>());
        saved = runtime.Sessions.Load(runtime.Workspace, childId)!;
        Assert.Equal(2, saved.Messages.Count(message => message.Role == ChatRole.Tool));
        Assert.All(saved.Messages.Where(message => message.Role == ChatRole.Tool), message => Assert.Contains(expected, message.Text));
    }

    [Fact]
    public async Task Guidance_CompletionRaceWaitsForRelease_AndChecksParentScope()
    {
        var records = new ExecutionRecordStore(new SeekClawDatabase(Path.Combine(_root, "race.db")));
        await using var manager = new SubAgentTaskManager(records);
        var closed = Signal();
        var finish = Signal();
        await manager.StartAsync("parent", new JsonObject { ["id"] = "child" }, async (queue, ct) =>
        {
            Assert.True(queue.TryCompleteIfEmpty());
            closed.TrySetResult();
            await finish.Task.WaitAsync(ct);
            return new AgentTurnResult("done", false, null);
        }, CancellationToken.None);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.TrySteerAsync("other", "child", "wrong parent", CancellationToken.None));
        var send = manager.TrySteerAsync("parent", "child", "next task", CancellationToken.None);
        Assert.False(send.IsCompleted);
        finish.TrySetResult();
        Assert.Null(await send.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("completed", manager.Get("parent", "child")["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task LegacyChild_ResumesItsExistingWorktreeStorageScope_WithoutCopyingHistory()
    {
        var factory = new ScriptedFactory((_, _, _) => Task.FromResult(new LlmCompletion { Text = "Resumed legacy child", FinishReason = "stop" }));
        await using var runtime = Runtime(Config(), factory);
        var parent = runtime.Sessions.Create(runtime.Workspace);
        var legacyWorkspace = new WorkspaceInfo { Root = Path.Combine(_root, "legacy-worktree"), ProjectKinds = [], ConfigurationRoot = _root };
        Directory.CreateDirectory(legacyWorkspace.Root);
        var child = runtime.Sessions.Create(legacyWorkspace, parentSessionId: parent.Header.Id);
        runtime.Sessions.Append(child, ChatMessage.User("Original legacy assignment"));
        runtime.ExecutionRecords.Put("subagent", "legacy-child", parent.Header.Id, new JsonObject
        {
            ["id"] = "legacy-child", ["parent"] = parent.Header.Id, ["sessionId"] = child.Header.Id,
            ["workspace"] = legacyWorkspace.Root, ["role"] = "researcher", ["mode"] = "readonly",
            ["networkEnabled"] = false, ["status"] = "interrupted"
        });
        var context = new ToolContext { Workspace = legacyWorkspace, SessionWorkspace = runtime.Workspace,
            Events = runtime.Events, Agent = runtime.ConfigStore.Config.Agent, SessionId = parent.Header.Id, Owner = parent.Header.Id };
        var task = await runtime.SubAgentRunner.SendAsync("legacy-child", "Continue existing work.", context, CancellationToken.None);
        Assert.Equal(child.Header.Id, task["sessionId"]!.GetValue<string>());
        Assert.Equal("completed", (await Finished(runtime, parent.Header.Id, "legacy-child"))["status"]!.GetValue<string>());
        Assert.Null(runtime.Sessions.Load(runtime.Workspace, child.Header.Id));
        var saved = runtime.Sessions.Load(legacyWorkspace, child.Header.Id)!;
        Assert.Contains(saved.Messages, message => message.Text == "Original legacy assignment");
        Assert.Contains(saved.Messages, message => message.Text == "Continue existing work.");
    }

    [Fact]
    public async Task Guidance_AcceptedBeforeProviderFailure_RemainsInChildHistoryForResume()
    {
        var entered = Signal();
        var release = Signal();
        var factory = new ScriptedFactory(async (index, _, ct) =>
        {
            if (index == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new LlmCompletion { ErrorMessage = "upstream response failed", FinishReason = "error" };
            }
            return new LlmCompletion { Text = "Recovered", FinishReason = "stop" };
        });
        await using var runtime = Runtime(Config(), factory);
        var parent = runtime.Sessions.Create(runtime.Workspace);
        var context = Context(runtime, parent);
        var task = await runtime.SubAgentRunner.StartAsync(new SubAgentTaskRequest("general", "Inspect parser"), context, CancellationToken.None);
        var id = task["id"]!.GetValue<string>();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.SubAgentRunner.SendAsync(id, "Remember to check invalid input.", context, CancellationToken.None);
        release.TrySetResult();
        Assert.Equal("failed", (await Finished(runtime, parent.Header.Id, id))["status"]!.GetValue<string>());
        var child = runtime.Sessions.Load(runtime.Workspace, task["sessionId"]!.GetValue<string>())!;
        Assert.Contains(child.Messages, message => message.Text == "Remember to check invalid input.");
        await runtime.SubAgentRunner.SendAsync(id, "Resume and report.", context, CancellationToken.None);
        Assert.Equal("completed", (await Finished(runtime, parent.Header.Id, id))["status"]!.GetValue<string>());
        Assert.Contains(factory.Requests.Last().Messages, message => message.Text == "Remember to check invalid input.");
    }

    [Fact]
    public async Task Guidance_RejectsAnOverfullQueueWithoutInterruptingTheChild()
    {
        var records = new ExecutionRecordStore(new SeekClawDatabase(Path.Combine(_root, "bounded.db")));
        await using var manager = new SubAgentTaskManager(records);
        var entered = Signal();
        var release = Signal();
        await manager.StartAsync("parent", new JsonObject { ["id"] = "child" }, async (queue, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            Assert.Equal(32, queue.Drain().Count);
            return new AgentTurnResult("done", false, null);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 32; i++)
            Assert.NotNull(await manager.TrySteerAsync("parent", "child", $"Guidance {i}", CancellationToken.None));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TrySteerAsync("parent", "child", "Too many", CancellationToken.None));
        Assert.Contains("queue is full", error.Message);
        Assert.Equal("running", manager.Get("parent", "child")["status"]!.GetValue<string>());
        release.TrySetResult();
        Assert.Equal("completed", (await manager.WaitAsync("parent", "child", 1000, CancellationToken.None))["status"]!.GetValue<string>());
    }

    private ConfigStore Config()
    {
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        config.Config.Providers.Clear();
        config.Config.Providers.Add(new ProviderConfig { Id = "test", Kind = "openai", BaseUrl = "https://test.invalid/v1",
            Models = [new ModelConfig { Id = "model", ContextWindow = 128_000, MaxOutput = 512 }] });
        config.Config.Provider = "test";
        config.Config.Model = "model";
        config.Config.Routing.Fallback = ["test/model"];
        config.Config.Agent.EnableCrossSessionMemory = false;
        config.Config.Agent.AutoVerify = false;
        return config;
    }

    private SeekClawRuntime Runtime(IConfigStore config, ILlmClientFactory factory)
    {
        var database = new SeekClawDatabase(Path.Combine(_root, "data.db"));
        return SeekClawRuntime.CreateIsolated(new WorkspaceInfo { Root = _root, ProjectKinds = [] }, configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton(database);
            services.AddSingleton(new WorktreeManager(new ExecutionRecordStore(database), new WorkspaceManager(), Path.Combine(_root, "managed")));
            services.AddSingleton(factory);
        });
    }

    private static ToolContext Context(SeekClawRuntime runtime, AgentSession parent, bool networkEnabled = true, IToolApprovalGate? approval = null) => new()
    {
        Workspace = runtime.Workspace, SessionWorkspace = runtime.Workspace, Events = runtime.Events, Agent = runtime.ConfigStore.Config.Agent,
        SessionId = parent.Header.Id, Owner = parent.Header.Id, NetworkEnabled = networkEnabled, ApprovalGate = approval
    };

    private static async Task<JsonObject> Finished(SeekClawRuntime runtime, string parent, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        JsonObject record;
        do { record = await runtime.SubAgentTasks.WaitAsync(parent, id, 1000, timeout.Token); }
        while (record["status"]?.GetValue<string>() == "running");
        return record;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class BlockingApprovalGate(TaskCompletionSource entered, TaskCompletionSource release) : IToolApprovalGate
    {
        public async ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ToolApprovalDecision.Allow;
        }
    }

    private sealed class ScriptedFactory(Func<int, LlmRequest, CancellationToken, Task<LlmCompletion>> complete) : ILlmClientFactory
    {
        private int _invocations;
        public ConcurrentQueue<LlmRequest> Requests { get; } = new();
        public ILlmClient GetClient(string kind) => new Client(this);
        private Task<LlmCompletion> CompleteAsync(int index, LlmRequest request, CancellationToken ct) => complete(index, request, ct);
        private sealed class Client(ScriptedFactory factory) : ILlmClient
        {
            public string Kind => "openai";
            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                factory.Requests.Enqueue(request);
                var result = await factory.CompleteAsync(Interlocked.Increment(ref factory._invocations), request, ct);
                yield return new LlmCompleted(result);
            }
        }
    }
}
