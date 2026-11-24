using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Goals;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Verification;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class SessionGoalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("seekclaw-goal-").FullName;

    [Fact]
    public async Task PersistentGoalContinuesPastStepLimitAndRetriesWithoutReplayingCompletedTools()
    {
        var providers = new ScriptedProviders(
            [Call("create", """{"action":"create","objective":"Check workspace","criteria":"Workspace check passes"}""")],
            [new LlmCompleted(new LlmCompletion
            {
                ToolCalls = [new ToolCallRequest("once", "check_once", "{}"),
                    new ToolCallRequest("progress", GoalTool.ToolName, """{"action":"progress","progress":"Workspace checked"}""")],
                FinishReason = "tool_calls",
            })],
            [new LlmTextDelta("incomplete output")],
            [Call("complete", """{"action":"complete","evidence":"Workspace check returned success"}""")],
            [Answer("Verified.")]);
        await using var runtime = CreateRuntime(providers, maxSteps: 1);
        var check = new CheckOnceTool();
        runtime.Tools.Register(check);
        var workspace = runtime.Workspace;
        var session = runtime.Sessions.Create(workspace);

        var result = await runtime.Agent.RunTurnAsync(session, workspace, "Start a persistent goal to check the workspace.", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("Verified.", result.Text);
        Assert.Equal(1, check.Calls);
        Assert.Equal(5, providers.Requests.Count);
        Assert.Empty(providers.Requests[4].Tools);
        Assert.Contains("Workspace checked", providers.Requests[3].System);
        Assert.Contains(providers.Requests[3].Messages, message =>
            message is { ToolCallId: "once", Text: "Check passed." });
        Assert.DoesNotContain(runtime.Sessions.Load(workspace, session.Header.Id)!.Messages,
            message => message.Text.Contains("incomplete output", StringComparison.Ordinal));
        var saved = runtime.Goals.Get(workspace, session.Header.Id);
        Assert.NotNull(saved);
        Assert.Equal(SessionGoal.Complete, saved.Status);
        Assert.Equal("Workspace check returned success", saved.Progress);
        Assert.Null(saved.PauseReason);
    }

    [Fact]
    public async Task PausePersistsAcrossRuntimesAndResumeUsesOriginalHistory()
    {
        string goalId;
        string sessionId;
        await using (var initial = CreateRuntime(new ScriptedProviders(
            [Call("progress", """{"action":"progress","progress":"Inspected src/parser.cs"}""")],
            [Call("pause", """{"action":"pause","reason":"Need input format"}""")])))
        {
            var session = initial.Sessions.Create(initial.Workspace);
            sessionId = session.Header.Id;
            goalId = initial.Goals.Create(initial.Workspace, sessionId, "Check parser", "Verify accepted input")!.Id;
            var pausedTurn = await initial.Agent.RunTurnAsync(session, initial.Workspace, "Review current parser", CancellationToken.None);
            Assert.Null(pausedTurn.Error);
            Assert.Equal(SessionGoal.Paused, initial.Goals.Get(initial.Workspace, sessionId)?.Status);
        }

        var providers = new ScriptedProviders(
            [Answer("The format is JSON.")],
            [Call("complete", """{"action":"complete","evidence":"Validated parser with JSON input"}""")],
            [Answer("Parser verified.")]);
        await using var runtime = CreateRuntime(providers);
        var workspace = runtime.Workspace;
        var restored = runtime.Sessions.Load(workspace, sessionId)!;
        var before = runtime.Goals.Get(workspace, sessionId);
        Assert.Equal("Need input format", before?.PauseReason);
        Assert.Equal("Inspected src/parser.cs", before?.Progress);

        await runtime.Agent.RunTurnAsync(restored, workspace, "The input format is JSON.", CancellationToken.None);
        Assert.Equal(SessionGoal.Paused, runtime.Goals.Get(workspace, sessionId)?.Status);
        Assert.DoesNotContain("# Active conversation goal", providers.Requests[0].System);

        var result = await runtime.Agent.RunTurnAsync(restored, workspace, "", CancellationToken.None, resumeGoal: true);
        Assert.Null(result.Error);
        Assert.Equal("Parser verified.", result.Text);
        Assert.Contains("Inspected src/parser.cs", providers.Requests[1].System);
        Assert.Contains(providers.Requests[1].Messages, message => message.Text == "Review current parser");
        Assert.Contains(providers.Requests[1].Messages, message => message.Text == "The input format is JSON.");
        var after = runtime.Goals.Get(workspace, sessionId);
        Assert.Equal(goalId, after?.Id);
        Assert.Equal(SessionGoal.Complete, after?.Status);
        Assert.Null(after?.PauseReason);
    }

    [Theory]
    [InlineData(true, SessionGoal.Complete)]
    [InlineData(false, SessionGoal.Paused)]
    public async Task GoalCompletionWaitsForWorkspaceVerification(bool passes, string expectedStatus)
    {
        var providers = new ScriptedProviders(
            [new LlmCompleted(new LlmCompletion
            {
                ToolCalls = [new ToolCallRequest("entry", "check_once", "{}")],
                FinishReason = "tool_calls",
            })],
            [Call("complete", """{"action":"complete","evidence":"Finished the requested change"}""")],
            [Answer("Finished the change.")]);
        var verifier = new ObservingVerifier(passes);
        await using var runtime = CreateRuntime(providers, maxSteps: 2, verifier: verifier);
        var workspace = runtime.Workspace;
        var session = runtime.Sessions.Create(workspace);
        runtime.Tools.Register(new CheckOnceTool(mutating: true));
        var original = runtime.Goals.Create(workspace, session.Header.Id, "Make a change", "Checks pass")!;
        runtime.Goals.RecordProgress(workspace, session.Header.Id, original.Id, "Change made");
        verifier.OnVerify = () => runtime.Goals.Get(workspace, session.Header.Id)?.Status;

        var result = await runtime.Agent.RunTurnAsync(session, workspace, "Proceed", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(SessionGoal.Active, verifier.StatusDuringVerification);
        var goal = runtime.Goals.Get(workspace, session.Header.Id);
        Assert.Equal(original.Id, goal?.Id);
        Assert.Equal(expectedStatus, goal?.Status);
        Assert.Equal(passes ? "Finished the requested change" : "Change made", goal?.Progress);
        if (!passes) Assert.False(string.IsNullOrWhiteSpace(goal?.PauseReason));
    }

    [Fact]
    public async Task ATextAnswerAloneLeavesGoalPausedAndCannotTriggerUnboundedContinuation()
    {
        var providers = new ScriptedProviders([Answer("Work left.")], [Answer("Work still left.")]);
        await using var runtime = CreateRuntime(providers);
        var workspace = runtime.Workspace;
        var session = runtime.Sessions.Create(workspace);
        runtime.Goals.Create(workspace, session.Header.Id, "Check workspace", "Workspace check passes");

        var result = await runtime.Agent.RunTurnAsync(session, workspace, "Proceed", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, providers.Requests.Count);
        var goal = runtime.Goals.Get(workspace, session.Header.Id);
        Assert.Equal(SessionGoal.Paused, goal?.Status);
        Assert.False(string.IsNullOrWhiteSpace(goal?.PauseReason));
    }

    [Fact]
    public void ForkDoesNotStartAnIndependentGoalAndDeletingOwnerCascades()
    {
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        var database = new SeekClawDatabase(Path.Combine(_root, "lifecycle.db"));
        var sessions = new SessionStore(database);
        var goals = new SessionGoalStore(database);
        var source = sessions.Create(workspace);
        sessions.Append(source, ChatMessage.User("original"));
        Assert.NotNull(goals.Create(workspace, source.Header.Id, "Objective", "Criteria"));

        var fork = sessions.Fork(workspace, source.Header.Id);
        Assert.Null(goals.Get(workspace, fork.Header.Id));
        Assert.NotNull(goals.Get(workspace, source.Header.Id));
        sessions.Delete(workspace, source.Header.Id);
        Assert.Null(goals.Get(workspace, source.Header.Id));
    }

    private SeekClawRuntime CreateRuntime(ScriptedProviders providers, int maxSteps = 8, IVerifier? verifier = null)
    {
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        config.Config.Agent.MaxSteps = config.Config.Agent.MaxTotalSteps = maxSteps;
        config.Config.Agent.MaxRepairAttempts = 0;
        config.Config.Agent.AutoVerify = verifier is not null;
        config.Config.Agent.Mode = "auto";
        config.Config.Agent.AutoExtendSteps = false;
        config.Config.Agent.EnableCrossSessionMemory = false;
        return SeekClawRuntime.CreateIsolated(new WorkspaceInfo { Root = _root, ProjectKinds = [] }, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton<IProviderManager>(providers);
            if (verifier is not null) services.AddSingleton<IVerifier>(verifier);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_root, "data.db")));
        });
    }

    private static LlmCompleted Call(string id, string arguments) => new(new LlmCompletion
    {
        ToolCalls = [new ToolCallRequest(id, GoalTool.ToolName, arguments)], FinishReason = "tool_calls",
    });

    private static LlmCompleted Answer(string text) => new(new LlmCompletion { Text = text, FinishReason = "stop" });

    private sealed class ScriptedProviders(params IReadOnlyList<LlmStreamEvent>[] responses) : IProviderManager
    {
        private static readonly ModelInfo Model = new(new ProviderConfig { Id = "goal-test" },
            new ModelConfig { Id = "model", ContextWindow = 128_000, MaxOutput = 1024 });
        public List<LlmRequest> Requests { get; } = [];
        public ModelInfo ResolveActive(WorkspaceConfig? workspace = null) => Model;
        public IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null) => [Model];
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(Func<ModelInfo, LlmRequest> requestFactory,
            WorkspaceConfig? workspace, [EnumeratorCancellation] CancellationToken ct,
            Func<ModelInfo, bool>? candidateFilter = null, ModelInfo? pinnedModel = null)
        {
            var index = Requests.Count;
            Requests.Add(requestFactory(Model));
            foreach (var item in responses[index])
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
            await Task.CompletedTask;
            if (!responses[index].Any(item => item is LlmCompleted))
                throw new LlmException("ResponseEnded", retryable: true);
        }
        public Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FetchModelsAsync(ProviderConfig provider, string? url = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ObservingVerifier(bool passes) : IVerifier
    {
        public Func<string?>? OnVerify { get; set; }
        public string? StatusDuringVerification { get; private set; }
        public string? ResolveCommand(WorkspaceInfo workspace) => "check";
        public Task<VerifyResult> VerifyAsync(WorkspaceInfo workspace, CancellationToken ct)
        {
            StatusDuringVerification = OnVerify?.Invoke();
            return Task.FromResult(new VerifyResult(passes, "check", passes ? "Passed" : "Build failed"));
        }
    }

    private sealed class CheckOnceTool(bool mutating = false) : ITool
    {
        public int Calls { get; private set; }
        public string Name => "check_once";
        public string Description => "Check the workspace";
        public System.Text.Json.Nodes.JsonObject ParameterSchema => ToolSchema.Object();
        public bool Mutating => mutating;
        public string StatusLabel => "Checking";
        public Task<ToolResult> ExecuteAsync(System.Text.Json.Nodes.JsonObject arguments, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ToolResult.Ok("Check passed."));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
