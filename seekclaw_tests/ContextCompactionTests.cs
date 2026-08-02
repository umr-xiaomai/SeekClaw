using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Providers;
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
            store.Config.Profiles["default"].Strategy = "fast";
            store.Config.Routing.Strategies["fast"] = ["openai/small"];
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
            for (var i = 0; i < 30; i++)
                runtime.Sessions.Append(session, ChatMessage.User($"Message {i}: " + new string('x', 200)));

            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "continue", CancellationToken.None);

            Assert.Null(result.Error);

            // A compaction summary request ran before the real turn request.
            var summaryRequest = capture.Requests[0];
            Assert.NotNull(summaryRequest.System);
            Assert.Contains("memory compaction", summaryRequest.System);
            Assert.Empty(summaryRequest.Tools);

            // The session history was replaced by the summary plus a recent tail.
            Assert.Contains("[Context compaction]", session.Messages[0].Text);
            Assert.Equal("continue", session.Messages[^2].Text);
            Assert.True(session.Messages.Count < 32, $"expected compaction, got {session.Messages.Count} messages");

            // The persisted history matches the compacted in-memory view.
            var reloaded = runtime.Sessions.Load(globalWorkspace, session.Header.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(session.Messages.Count, reloaded!.Messages.Count);
            Assert.Equal(session.Messages[0].Text, reloaded.Messages[0].Text);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
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
            store.Config.Profiles["default"].Strategy = "fast";
            store.Config.Routing.Strategies["fast"] = ["openai/small"];
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

    // ---------------------------------------------------------------- helpers

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
