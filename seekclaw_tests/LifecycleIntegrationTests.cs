using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Scheduling;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class LifecycleIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-lifecycle-tests", Guid.NewGuid().ToString("N"));
    public LifecycleIntegrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public async Task ToolCompletion_PublishesTheSameMultilineOutputAsThePersistedResult()
    {
        File.WriteAllText(Path.Combine(_root, "result.txt"), "first line\nsecond line\n");
        await using var runtime = Runtime(new ReadFileClient());
        var session = runtime.Sessions.Create(runtime.Workspace);
        using var events = runtime.Events.Subscribe();
        await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "read result.txt", CancellationToken.None);

        var completions = new List<ToolCallCompletedEvent>();
        while (events.Reader.TryRead(out var item))
            if (item is ToolCallCompletedEvent completion) completions.Add(completion);
        var completed = Assert.Single(completions);
        Assert.True(completed.Success);
        Assert.Equal("read-call", completed.CallId);
        Assert.Contains("first line", completed.Output);
        Assert.Contains("second line", completed.Output);
        Assert.Contains('\n', completed.Output);
        var restored = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!;
        var persisted = Assert.Single(restored.Messages, message => message.ToolCallId == completed.CallId);
        Assert.Equal(persisted.Text, completed.Output);
        Assert.NotEqual(completed.ResultSummary, completed.Output);
    }

    [Fact]
    public async Task PresentedArtifact_FlowsThroughAgentEventsAndPersistedHistory()
    {
        File.WriteAllText(Path.Combine(_root, "result.txt"), "A deliverable");
        await using var runtime = Runtime(new ReadFileClient("present_artifact"));
        var session = runtime.Sessions.Create(runtime.Workspace);
        using var events = runtime.Events.Subscribe();
        await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "present result.txt", CancellationToken.None);
        ToolCallCompletedEvent? completion = null;
        while (events.Reader.TryRead(out var item)) if (item is ToolCallCompletedEvent tool) completion = tool;
        Assert.NotNull(completion);
        var presented = Assert.IsType<SeekClaw.Runtime.Presentation.ArtifactWidget>(Assert.Single(completion.Widgets!));
        Assert.Equal(Path.Combine(_root, "result.txt"), presented.Path);
        var restored = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!;
        var persisted = Assert.Single(restored.Messages, message => message.ToolCallId == completion.CallId);
        Assert.Equal(presented, Assert.Single(persisted.ToolWidgets!));
    }

    [Fact]
    public async Task ForegroundTurns_ReloadAfterTakingTheConversationLease()
    {
        var client = new CapturingClient();
        await using var runtime = Runtime(client);
        var session = runtime.Sessions.Create(runtime.Workspace);
        var stale = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!;
        await Task.WhenAll(
            runtime.Agent.RunTurnAsync(session, runtime.Workspace, "first instruction", CancellationToken.None),
            runtime.Agent.RunTurnAsync(stale, runtime.Workspace, "second instruction", CancellationToken.None));
        var reloaded = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!;
        Assert.Equal(4, reloaded.Messages.Count);
        Assert.Contains(client.Requests[^1].Messages, message => message.Text == "first instruction");
        Assert.Contains(client.Requests[^1].Messages, message => message.Text == "second instruction");
    }

    [Fact]
    public async Task Schedules_ContinueTheBoundConversation_AndSuppressUnchangedNotifications()
    {
        var client = new CapturingClient();
        await using var runtime = Runtime(client);
        var session = runtime.Sessions.Create(runtime.Workspace);
        runtime.Sessions.Append(session, ChatMessage.User("important original context"));
        runtime.Sessions.Append(session, ChatMessage.Assistant("recorded"));
        var task = runtime.Schedules.Upsert(null, "follow-up", _root, "check progress", "0 9 * * *", true,
            session.Header.Id, notificationPolicy: "changes");
        using var events = runtime.Events.Subscribe();
        using var http = new LlmHttpFactory();
        await using var service = new ScheduleService(runtime.Schedules, runtime, new FileLockCoordinator(), http,
            new CircuitBreaker(new RetryConfig()), (workspace, conversation, prompt, ct) => runtime.Agent.RunTurnAsync(conversation, workspace, prompt, ct));
        await service.RunNowAsync(task.Id, CancellationToken.None);
        await service.RunNowAsync(task.Id, CancellationToken.None);
        Assert.Single(runtime.Sessions.List(runtime.Workspace));
        Assert.Equal(6, runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!.Messages.Count);
        Assert.All(client.Requests, request => Assert.Contains(request.Messages, message => message.Text == "important original context"));
        var published = new List<RuntimeEvent>();
        while (events.Reader.TryRead(out var item)) published.Add(item);
        Assert.Single(published.OfType<ScheduledTaskCompletedEvent>());
        Assert.Single(published.OfType<ScheduledTaskUpdatedEvent>());
        Assert.Equal(session.Header.Id, runtime.Schedules.SetEnabled(task.Id, false).SessionId);
    }

    [Fact]
    public async Task Schedules_ClaimBeforeQueueing_AndShutdownDrainsQueuedRuns()
    {
        await using var runtime = Runtime(new CapturingClient());
        var first = runtime.Schedules.Upsert(null, "first", _root, "run", "0 9 * * *", true);
        var second = runtime.Schedules.Upsert(null, "second", _root, "run", "0 9 * * *", true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var http = new LlmHttpFactory();
        var service = new ScheduleService(runtime.Schedules, runtime, new FileLockCoordinator(), http, new CircuitBreaker(new RetryConfig()),
            async (_, _, _, ct) => { Interlocked.Increment(ref count); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new AgentTurnResult("", false, null); });
        service.StartRun(first.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 10; index++) service.StartRun(second.Id);
        await service.DisposeAsync();
        Assert.Equal(1, count);
        Assert.Equal(ScheduleRunStatus.Cancelled, runtime.Schedules.Get(first.Id)!.LastStatus);
        Assert.Equal(ScheduleRunStatus.Cancelled, runtime.Schedules.Get(second.Id)!.LastStatus);
    }

    [Fact]
    public async Task DurableSubAgent_SendUsesTheSameChildContext_AndInheritsNetworkRestrictions()
    {
        var client = new CapturingClient();
        await using var runtime = Runtime(client);
        var parent = runtime.Sessions.Create(runtime.Workspace, networkEnabled: false);
        var context = new ToolContext { Workspace = runtime.Workspace, SessionWorkspace = runtime.Workspace,
            Events = runtime.Events, Agent = runtime.ConfigStore.Config.Agent, SessionId = parent.Header.Id,
            NetworkEnabled = false, Owner = "parent", ApprovalGate = runtime.Approvals };
        var child = await runtime.SubAgentRunner.StartAsync(new SubAgentTaskRequest("researcher", "initial child context"), context, CancellationToken.None);
        var id = child["id"]!.GetValue<string>();
        child = await runtime.SubAgentTasks.WaitAsync(parent.Header.Id, id, 5000, CancellationToken.None);
        Assert.Equal("completed", child["status"]!.GetValue<string>());
        await runtime.SubAgentRunner.SendAsync(id, "follow-up child instruction", context, CancellationToken.None);
        child = await runtime.SubAgentTasks.WaitAsync(parent.Header.Id, id, 5000, CancellationToken.None);
        Assert.Equal("completed", child["status"]!.GetValue<string>());
        Assert.Contains(client.Requests[^1].Messages, message => message.Text == "initial child context");
        Assert.Contains(client.Requests[^1].Messages, message => message.Text == "follow-up child instruction");
        Assert.DoesNotContain(client.Requests.SelectMany(request => request.Tools ?? []), tool => tool.Name == "web_fetch" || tool.Name == "web_search");
        Assert.False(runtime.Sessions.Load(runtime.Workspace, child["sessionId"]!.GetValue<string>())!.Header.NetworkEnabled);
        Assert.Equal("guardrail", runtime.ConfigStore.Config.Agent.Mode);
    }

    [Fact]
    public async Task Coordinators_WithSeparateInstances_RespectTheSameFileLeaseAndCancellation()
    {
        var first = new SessionTurnCoordinator(Path.Combine(_root, "locks"));
        var second = new SessionTurnCoordinator(Path.Combine(_root, "locks"));
        using (await first.AcquireAsync("conversation", CancellationToken.None))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireAsync("conversation", cancel.Token));
        }
        using var lease = await second.AcquireAsync("conversation", CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundChild_SurvivesItsLaunchingTurnRuntime_AndRetainsTheApprovalChannel()
    {
        var client = new DelayedToolClient();
        await using var runtime = Runtime(client);
        var parent = runtime.Sessions.Create(runtime.Workspace);
        var turn = SeekClawRuntime.CreateIsolated(runtime.Workspace, configureServices: services =>
        {
            runtime.ShareTaskServices(services);
            services.AddSingleton<ILlmClientFactory>(client);
        });
        var approvalRequested = new TaskCompletionSource<ToolApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        turn.Approvals.SetHandler((request, _) =>
        {
            approvalRequested.TrySetResult(request);
            return ValueTask.FromResult(ToolApprovalDecision.Allow);
        });
        var context = new ToolContext { Workspace = runtime.Workspace, SessionWorkspace = runtime.Workspace,
            SessionId = parent.Header.Id, Events = turn.Events, Agent = turn.ConfigStore.Config.Agent,
            ApprovalGate = turn.Approvals, Owner = "launching-turn" };
        var child = await turn.SubAgentRunner.StartAsync(new SubAgentTaskRequest("coder", "write a file after this turn finishes"), context, CancellationToken.None);
        try { await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await turn.DisposeAsync(); client.Release.TrySetResult(); }
        var request = await approvalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(child["sessionId"]!.GetValue<string>(), request.SessionId);
        Assert.Equal("write_file", request.ToolName);
        child = await runtime.SubAgentTasks.WaitAsync(parent.Header.Id, child["id"]!.GetValue<string>(), 5000, CancellationToken.None);
        Assert.Equal("completed", child["status"]!.GetValue<string>());
        Assert.Equal("child output", File.ReadAllText(Path.Combine(_root, "child.txt")));
    }

    [Fact]
    public async Task ScheduleTool_UsesTheSourceConversationScope_AndDeletingDuringRunDoesNotResurrectTheTask()
    {
        await using var runtime = Runtime(new CapturingClient());
        var session = runtime.Sessions.Create(runtime.Workspace);
        var execution = new WorkspaceInfo { Root = Path.Combine(_root, "worktree"), ProjectKinds = [] };
        using var prompts = new FilePromptProvider([_root]);
        var tool = new ScheduleTool(prompts, runtime.Schedules);
        await tool.ExecuteAsync(new JsonObject { ["action"] = "upsert", ["prompt"] = "follow up", ["cron"] = "0 9 * * *" },
            new ToolContext { Workspace = execution, SessionWorkspace = runtime.Workspace, SessionId = session.Header.Id,
                Agent = runtime.ConfigStore.Config.Agent, Events = runtime.Events }, CancellationToken.None);
        var task = Assert.Single(runtime.Schedules.List());
        Assert.Equal(_root, task.Workspace);
        Assert.Equal(session.Header.Id, task.SessionId);
        using var http = new LlmHttpFactory();
        await using var service = new ScheduleService(runtime.Schedules, runtime, new FileLockCoordinator(), http, new CircuitBreaker(new RetryConfig()),
            (_, _, _, _) => { runtime.Schedules.Remove(task.Id); return Task.FromResult(new AgentTurnResult("completed", false, null)); });
        await service.RunNowAsync(task.Id, CancellationToken.None);
        Assert.Null(runtime.Schedules.Get(task.Id));
        Assert.Null(runtime.Schedules.RecordRun(task.Id, ScheduleRunStatus.Success));
    }

    private SeekClawRuntime Runtime(ILlmClientFactory client)
    {
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        config.Config.Providers = [new ProviderConfig { Id = "test", Kind = "openai", BaseUrl = "https://example.invalid/v1", Models = [new ModelConfig { Id = "model", ContextWindow = 16000, MaxOutput = 256 }] }];
        config.Config.Provider = "test"; config.Config.Model = "model"; config.Config.Routing.Fallback = ["test/model"];
        config.Config.Mcp = new McpConfig();
        config.Config.Agent.AutoVerify = false; config.Config.Agent.EnableCrossSessionMemory = false;
        config.Config.Agent.Mode = "guardrail";
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        return SeekClawRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_root, "data.db")));
            services.AddSingleton<ILlmClientFactory>(client);
            services.AddSingleton<IUsageTracker>(provider => new UsageTracker(provider.GetRequiredService<IEventBus>(), Path.Combine(_root, "usage.jsonl")));
        });
    }

    private sealed class ReadFileClient(string toolName = "read_file") : ILlmClientFactory, ILlmClient
    {
        public string Kind => "openai";
        private int _calls;
        public ILlmClient GetClient(string kind) => this;
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new LlmCompleted(Interlocked.Increment(ref _calls) == 1
                ? new LlmCompletion { ToolCalls = [new ToolCallRequest("read-call", toolName, """{"path":"result.txt"}""")] }
                : new LlmCompletion { Text = "done", FinishReason = "stop" });
        }
    }

    private sealed class CapturingClient : ILlmClientFactory, ILlmClient
    {
        public string Kind => "openai";
        public List<LlmRequest> Requests { get; } = [];
        public ILlmClient GetClient(string kind) => this;
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new LlmCompleted(new LlmCompletion { Text = "stable result", FinishReason = "stop" });
        }
    }

    private sealed class DelayedToolClient : ILlmClientFactory, ILlmClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Kind => "openai";
        private int _calls;
        public ILlmClient GetClient(string kind) => this;
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
                yield return new LlmCompleted(new LlmCompletion { ToolCalls = [new ToolCallRequest("child-call", "write_file", """{"path":"child.txt","content":"child output"}""")] });
            }
            else yield return new LlmCompleted(new LlmCompletion { Text = "done", FinishReason = "stop" });
        }
    }
}
