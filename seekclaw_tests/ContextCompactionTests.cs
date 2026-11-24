using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Goals;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class ContextCompactionTests
{
    private static string TempDir() => Path.Combine(
        Path.GetTempPath(), "seekclaw-compaction-tests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- split logic

    [Fact]
    public void SplitForCompaction_SplitsOversizedHistoryIntoOldAndRecent()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 2_000, MaxOutput = 256 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 40; i++)
            messages.Add(ChatMessage.User($"Message {i}: " + new string('x', 80)));

        var (old, recent) = ContextPlanner.SplitForCompaction(messages, model, "x");

        Assert.True(old.Count > 0, "expected an old part to summarize");
        Assert.True(recent.Count > 0, "expected a recent tail to keep");
        Assert.Equal(messages.Count, old.Count + recent.Count);
        Assert.Equal(messages, old.Concat(recent).ToList());
        Assert.NotEqual(ChatRole.Tool, recent[0].Role);
    }

    [Fact]
    public void SplitForCompaction_NeverOpensRecentTailWithAnOrphanedToolResult()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 2_000, MaxOutput = 256 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 25; i++)
        {
            messages.Add(ChatMessage.User($"User {i}: " + new string('x', 60)));
            messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new ToolCallRequest($"c{i}", "read_file", "{}")],
            });
            messages.Add(ChatMessage.ToolResult($"c{i}", "read_file", new string('y', 60), true));
        }

        var (old, recent) = ContextPlanner.SplitForCompaction(messages, model, "x");

        Assert.Equal(messages.Count, old.Count + recent.Count);
        Assert.Equal(messages, old.Concat(recent).ToList());
        // The tail must never open with an orphaned tool result; leading results are
        // folded back into the summarized old part where their assistant turn lives.
        Assert.NotEqual(ChatRole.Tool, recent[0].Role);
    }

    [Fact]
    public void FitToWindow_KeepsCheckpointAndRecentToolExchangeWhenAnOutputExceedsTheBudget()
    {
        var model = new ModelConfig { ContextWindow = 2_000, MaxOutput = 256 };
        const string priorSummary = ">>> [Context compaction] Keep SQLite and retain src/schema.sql.";
        const string currentRequest = "Run both read-only inspections before editing.";
        const string firstLine = "First line ";
        var originalOutput = firstLine + new string('x', 20_000) + " Last line";
        var messages = new[]
        {
            ChatMessage.User(priorSummary),
            ChatMessage.User(new string('d', 4_000)),
            ChatMessage.User(currentRequest),
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new ToolCallRequest("first", "read_file", "{}"), new ToolCallRequest("second", "read_file", "{}")],
            },
            ChatMessage.ToolResult("first", "read_file", originalOutput, true),
            ChatMessage.ToolResult("second", "read_file", "Second file", true),
        };

        var fitted = ContextPlanner.FitToWindow(messages, model, "system");

        Assert.Equal(priorSummary, fitted[0].Text);
        Assert.Contains(fitted, message => message.Text == currentRequest);
        var exchanged = fitted.TakeLast(3).ToArray();
        Assert.Equal(exchanged[0].ToolCalls!.Select(c => c.Id), exchanged.Skip(1).Select(m => m.ToolCallId));
        Assert.StartsWith(firstLine, exchanged[1].Text);
        Assert.EndsWith(" Last line", exchanged[1].Text);
        Assert.Equal("Second file", exchanged[2].Text);
        Assert.Equal(originalOutput, messages[^2].Text);
        Assert.True(fitted.Sum(ContextPlanner.EstimateTokens) <= ContextPlanner.WindowBudget(model, "system"));
    }

    // ---------------------------------------------------------------- agent turn

    [Fact]
    public async Task AgentTurn_CompactsOversizedHistory_AndKeepsRunning()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test.local/v1",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "small",
                        ContextWindow = 2_000,
                        MaxOutput = 256,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "openai";
            store.Config.Model = "small";
            store.Config.Routing.Fallback = ["openai/small"];

            var capture = new RecordingClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            runtime.Sessions.Append(session, ChatMessage.User("Keep SQLite and retain the schema at src/schema.sql."));
            const string toolId = "schema-tool";
            runtime.Sessions.Append(session, new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new ToolCallRequest(toolId, "read_file", "{\"path\":\"src/schema.sql\"}")],
            });
            const string toolOutput = "CREATE TABLE users (id INTEGER PRIMARY KEY);";
            runtime.Sessions.Append(session, ChatMessage.ToolResult(toolId, "read_file", toolOutput, true));
            for (var i = 0; i < 30; i++)
                runtime.Sessions.Append(session, ChatMessage.User($"Message {i}: " + new string('x', 200)));
            var before = session.Messages.ToArray();

            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "continue", CancellationToken.None);

            Assert.Null(result.Error);
            var summaryRequest = capture.Requests[0];
            Assert.Contains("memory compaction", summaryRequest.System);
            Assert.Empty(summaryRequest.Tools);
            Assert.Contains(summaryRequest.Messages, m => m.Text == before[0].Text);
            var toolCallIndex = summaryRequest.Messages.ToList().FindIndex(m => m.ToolCalls?.Any(c => c.Id == toolId) == true);
            Assert.True(toolCallIndex >= 0);
            Assert.Equal(toolId, summaryRequest.Messages[toolCallIndex + 1].ToolCallId);

            var effectiveHistory = capture.Requests[^1].Messages;
            Assert.StartsWith(">>> [Context compaction]", effectiveHistory[0].Text);
            Assert.DoesNotContain(effectiveHistory, m => m.ToolCallId == toolId || m.Text == before[0].Text);
            Assert.Equal("continue", effectiveHistory[^1].Text);
            Assert.Equal(before.Length + 2, session.Messages.Count);
            Assert.Equal(before.Select(m => (m.Role, m.Text, m.ToolCallId)),
                session.Messages.Take(before.Length).Select(m => (m.Role, m.Text, m.ToolCallId)));

            var disk = new SessionStore(Path.Combine(dir, "state.db"));
            var reloaded = disk.Load(globalWorkspace, session.Header.Id)!;
            Assert.Equal(session.Messages.Select(m => m.Text), reloaded.Messages.Select(m => m.Text));
            Assert.Equal(toolOutput, Assert.Single(reloaded.Messages, m => m.ToolCallId == toolId).Text);

            var fullBranch = disk.Fork(globalWorkspace, session.Header.Id);
            var beforeBoundary = disk.Fork(globalWorkspace, session.Header.Id, keepMessageCount: 3);
            var requestsBeforeBranch = capture.Requests.Count;
            var branchResult = await runtime.Agent.RunTurnAsync(fullBranch, globalWorkspace, "continue from the branch", CancellationToken.None);
            Assert.Null(branchResult.Error);
            Assert.Contains(Assert.Single(capture.Requests.Skip(requestsBeforeBranch)).Messages,
                m => m.Text.StartsWith(">>> [Context compaction]"));
            Assert.Equal(toolOutput, Assert.Single(fullBranch.Messages, m => m.ToolCallId == toolId).Text);

            var branchBeforeBoundaryResult = await runtime.Agent.RunTurnAsync(beforeBoundary, globalWorkspace, "continue from the beginning", CancellationToken.None);
            Assert.Null(branchBeforeBoundaryResult.Error);
            Assert.DoesNotContain(capture.Requests[^1].Messages, m => m.Text.StartsWith(">>> [Context compaction]"));
            Assert.Contains(capture.Requests[^1].Messages, m => m.ToolCallId == toolId);

            var goals = new SessionGoalStore(new SeekClawDatabase(Path.Combine(dir, "state.db")));
            var goal = goals.Create(globalWorkspace, session.Header.Id, "Update the schema", "Run migration tests")!;
            goals.RecordProgress(globalWorkspace, session.Header.Id, goal.Id, "Used a later decision in the conversation");
            disk.Truncate(globalWorkspace, session.Header.Id, keepMessageCount: reloaded.Messages.Count);
            Assert.Equal(SessionGoal.Active, goals.Get(globalWorkspace, session.Header.Id)?.Status);
            disk.Truncate(globalWorkspace, session.Header.Id, keepMessageCount: 3);
            var paused = goals.Get(globalWorkspace, session.Header.Id)!;
            Assert.Equal(SessionGoal.Paused, paused.Status);
            Assert.Null(paused.Progress);

            // Resume from an instance loaded before the rewind. The turn lease refreshes
            // the checkpoint and the transcript before using either for the next request.
            var recovered = await runtime.Agent.RunTurnAsync(reloaded, globalWorkspace, "continue with retained messages", CancellationToken.None);
            Assert.Null(recovered.Error);
            Assert.Equal(5, reloaded.Messages.Count);
            Assert.DoesNotContain(capture.Requests[^1].Messages, m => m.Text.StartsWith(">>> [Context compaction]"));
            Assert.Contains(capture.Requests[^1].Messages, m => m.ToolCallId == toolId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_CompactsCompletedToolStepsWithinTheCurrentTurn()
    {
        var directory = Directory.CreateTempSubdirectory("seekclaw-live-compaction-");
        var dir = directory.FullName;
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            var model = new ModelConfig { Id = "small", ContextWindow = 12_000, MaxOutput = 512 };
            store.Config.Providers =
            [
                new ProviderConfig { Id = "openai", Kind = "openai", BaseUrl = "https://test.local/v1", Models = [model] },
            ];
            store.Config.Provider = "openai";
            store.Config.Model = "small";
            store.Config.Routing.Fallback = ["openai/small"];

            const string userRequest = "Use the attached diagram to audit the index. Keep every file unchanged.";
            const string guidance = "Use the audit ledger as the source of truth.";
            const string oldFinding = "Early probe: the index is online.";
            const string latestFinding = "Latest probe: the ledger matches.";
            const string summary = "The early probe reported that the index is online.";
            var earlyOutput = oldFinding + "\n" + new string('x', 50_000) + "\nEnd of early probe.";
            var image = new ChatImageAttachment("diagram", "diagram.png", "image/png", "AQID", 3);
            var steering = new AgentSteeringQueue();
            var capture = new SequenceClientFactory(
                new LlmCompletion { ToolCalls = [new ToolCallRequest("approval", "ask_user", """{"question":"Which release?","options":["Amber","Blue"]}""")] },
                new LlmCompletion { ToolCalls = [new ToolCallRequest("early", CompactionProbe.NameValue, """{"probe":"early"}""")] },
                new LlmCompletion { ToolCalls = [new ToolCallRequest("latest", CompactionProbe.NameValue, """{"probe":"latest"}""")] },
                new LlmCompletion { Text = "Audit complete." })
            {
                Respond = request => request.Tools.Count == 0
                    ? new LlmCompletion { Text = request.Messages.Any(message => message.Text.Contains(oldFinding))
                        ? summary : "Audit the index without modifying files. The user selected Amber." }
                    : null,
            };
            var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            var databasePath = Path.Combine(dir, "state.db");
            await using var runtime = SeekClawRuntime.CreateIsolated(workspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(databasePath));
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });
            using var registration = runtime.Tools.Register(new CompactionProbe(earlyOutput, latestFinding));
            var session = runtime.Sessions.Create(workspace);

            var result = await runtime.Agent.RunTurnAsync(session, workspace, userRequest, CancellationToken.None,
                images: [image], steering: steering, askUser: (_, _, _) =>
                {
                    Assert.True(steering.TryEnqueue(ChatMessage.User(guidance)));
                    return Task.FromResult("Amber");
                });

            Assert.Null(result.Error);
            Assert.Equal("Audit complete.", result.Text);
            Assert.Contains(capture.Requests, request => request.Tools.Count == 0
                && request.Messages.Any(message => message.Text.Contains(oldFinding)
                    && message.Text.Contains("Excerpt from an oversized earlier history entry")));
            var finalRequest = capture.Requests[^1];
            var history = finalRequest.Messages;
            Assert.Contains(summary, history[0].Text);
            Assert.Equal(image, Assert.Single(Assert.Single(history, message => message.Text == userRequest).Images!));
            var answerCall = Array.FindIndex(history.ToArray(), message => message.ToolCalls?.Any(call => call.Id == "approval") == true);
            Assert.True(answerCall >= 0);
            Assert.Equal("approval", history[answerCall + 1].ToolCallId);
            Assert.Contains("Amber", history[answerCall + 1].Text);
            Assert.Contains(history, message => message.Text == guidance);
            Assert.DoesNotContain(history, message => message.ToolCallId == "early");
            Assert.Equal("latest", Assert.Single(history[^2].ToolCalls!).Id);
            Assert.Equal("latest", history[^1].ToolCallId);
            Assert.Equal(latestFinding, history[^1].Text);
            Assert.True(history.Sum(ContextPlanner.EstimateTokens) <= ContextPlanner.WindowBudget(model, finalRequest.System ?? ""));

            var persisted = new SessionStore(databasePath).Load(workspace, session.Header.Id)!;
            Assert.Equal(summary, persisted.ContextCheckpoint?.Summary);
            Assert.Equal(earlyOutput, Assert.Single(persisted.Messages, message => message.ToolCallId == "early").Text);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AgentTurn_CompactionFailure_FallsBackToTrim_AndStillFinishes()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test.local/v1",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "small",
                        ContextWindow = 2_000,
                        MaxOutput = 256,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "openai";
            store.Config.Model = "small";
            store.Config.Routing.Fallback = ["openai/small"];

            var capture = new FailingCompactionClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            for (var i = 0; i < 30; i++)
                runtime.Sessions.Append(session, ChatMessage.User($"Message {i}: " + new string('x', 200)));

            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "continue", CancellationToken.None);

            // The turn must complete even though the compaction summary call failed.
            Assert.Null(result.Error);
            Assert.Equal(2, capture.Requests.Count); // failed summary + main turn
            Assert.DoesNotContain("[Context compaction]", session.Messages[0].Text);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_TruncatedOutput_AutoContinuesWithoutUserPrompt()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "deepseek",
                Kind = "openai",
                BaseUrl = "https://api.deepseek.com",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "reasoner",
                        ContextWindow = 2_000,
                        MaxOutput = 256,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "deepseek";
            store.Config.Model = "reasoner";
            store.Config.Routing.Fallback = ["deepseek/reasoner"];

            var capture = new SequenceClientFactory(
                new LlmCompletion { Text = "", Thinking = "long thinking that exceeded max output", FinishReason = "length" },
                new LlmCompletion { Text = "finished answer", FinishReason = "stop" });
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "build the game", CancellationToken.None);

            // The truncated "thinking-only" completion must not end the turn; the agent
            // continues automatically and finishes once the model produces real output.
            Assert.Null(result.Error);
            Assert.Equal("finished answer", result.Text);
            Assert.Equal(2, capture.Requests.Count);
            Assert.Contains(session.Messages, message =>
                message.Role == ChatRole.User && message.Text.Contains("[output truncated]"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_TruncatedTextOutput_AutoContinuesAndAccumulatesFinalText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seekclaw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "deepseek",
                Kind = "openai",
                BaseUrl = "https://api.deepseek.com",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "coder",
                        ContextWindow = 64_000,
                        MaxOutput = 4000,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "deepseek";
            store.Config.Model = "coder";
            store.Config.Routing.Fallback = ["deepseek/coder"];

            var capture = new SequenceClientFactory(
                new LlmCompletion { Text = "public class Calculator {\n", FinishReason = "length" },
                new LlmCompletion { Text = "    public int Add(int a, int b) => a + b;\n}", FinishReason = "stop" });
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "write calculator", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal("public class Calculator {\n    public int Add(int a, int b) => a + b;\n}", result.Text);
            Assert.Equal(2, capture.Requests.Count);
            Assert.Contains(session.Messages, message =>
                message.Role == ChatRole.User && message.Text.Contains("[output truncated]"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_AutoExtendsSteps_CompletesMultiStepTaskWithoutStopping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seekclaw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "deepseek",
                Kind = "openai",
                BaseUrl = "https://api.deepseek.com",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "coder",
                        ContextWindow = 64_000,
                        MaxOutput = 4000,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "deepseek";
            store.Config.Model = "coder";
            store.Config.Routing.Fallback = ["deepseek/coder"];
            store.Config.Agent.MaxSteps = 2;
            store.Config.Agent.MaxRepairAttempts = 0;
            store.Config.Agent.AutoExtendSteps = true;
            store.Config.Agent.MaxTotalSteps = 10;

            var capture = new SequenceClientFactory(
                new LlmCompletion { ToolCalls = [new ToolCallRequest("c1", "list_files", "{}")] },
                new LlmCompletion { ToolCalls = [new ToolCallRequest("c2", "list_files", "{}")] },
                new LlmCompletion { ToolCalls = [new ToolCallRequest("c3", "list_files", "{}")] },
                new LlmCompletion { Text = "All files reviewed and done.", FinishReason = "stop" });

            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "review files", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.False(result.Cancelled);
            Assert.Equal("All files reviewed and done.", result.Text);
            Assert.Equal(4, capture.Requests.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_HitsMaxTotalSteps_GracefullyWrapsUpWithSummary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seekclaw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "deepseek",
                Kind = "openai",
                BaseUrl = "https://api.deepseek.com",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "coder",
                        ContextWindow = 64_000,
                        MaxOutput = 4000,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "deepseek";
            store.Config.Model = "coder";
            store.Config.Routing.Fallback = ["deepseek/coder"];
            store.Config.Agent.MaxSteps = 2;
            store.Config.Agent.MaxRepairAttempts = 0;
            store.Config.Agent.AutoExtendSteps = false;

            var capture = new SequenceClientFactory(
                new LlmCompletion { ToolCalls = [new ToolCallRequest("c1", "list_files", "{}")] },
                new LlmCompletion { ToolCalls = [new ToolCallRequest("c2", "list_files", "{}")] },
                new LlmCompletion { Text = "Completed 2 steps summary.", FinishReason = "stop" });

            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "review files", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.False(result.Cancelled);
            Assert.Equal("Completed 2 steps summary.", result.Text);
            Assert.Equal(3, capture.Requests.Count);
            Assert.Empty(capture.Requests[^1].Tools);
            Assert.Equal(ChatRole.Assistant, session.Messages[^1].Role);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class CompactionProbe(string earlyOutput, string latestOutput) : ITool
    {
        public const string NameValue = "compaction_probe";
        public string Name => NameValue;
        public string Description => "Read the audit probe.";
        public JsonObject ParameterSchema => new() { ["type"] = "object" };
        public bool Mutating => false;
        public bool RequiresWorkspace => false;
        public string StatusLabel => "Reading the audit probe";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(arguments["probe"]?.GetValue<string>() == "early" ? earlyOutput : latestOutput));
    }

    private sealed class SequenceClientFactory(params LlmCompletion[] completions) : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];
        public Func<LlmRequest, LlmCompletion?>? Respond { get; init; }
        private readonly Queue<LlmCompletion> _completions = new(completions);

        public ILlmClient GetClient(string kind) => new SequenceClient(this);

        private sealed class SequenceClient(SequenceClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                var next = owner.Respond?.Invoke(request) ?? (owner._completions.Count > 0
                    ? owner._completions.Dequeue()
                    : new LlmCompletion { Text = "done", FinishReason = "stop" });
                yield return new LlmCompleted(next);
            }
        }
    }

    private sealed class RecordingClientFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];

        public ILlmClient GetClient(string kind) => new RecordingClient(this);

        private sealed class RecordingClient(RecordingClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                yield return new LlmCompleted(new LlmCompletion { Text = "summary-or-answer" });
            }
        }
    }

    private sealed class FailingCompactionClientFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];

        public ILlmClient GetClient(string kind) => new FailingClient(this);

        private sealed class FailingClient(FailingCompactionClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                // Fail only the compaction summary call (no tools); the turn call succeeds.
                if (request.Tools.Count == 0)
                    throw new LlmException("simulated compaction failure", retryable: false);
                yield return new LlmCompleted(new LlmCompletion { Text = "done" });
            }
        }
    }
}
