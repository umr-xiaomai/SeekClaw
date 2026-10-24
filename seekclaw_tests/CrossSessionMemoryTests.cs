using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Memory;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class CrossSessionMemoryTests
{
    private static string TempDir() => Path.Combine(
        Path.GetTempPath(), "seekclaw-memory-tests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- store

    [Fact]
    public void Store_SavesListsDeduplicates_AndDeletes()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CrossSessionMemoryStore(new SeekClawDatabase(Path.Combine(dir, "state.db")));
            var global = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));

            store.Save("用户偏好中文回复", global, "session-1");
            store.Save(" 用户偏好中文回复 ", global, "session-2");

            var list = store.ListRecent();
            Assert.Single(list);
            Assert.Equal("用户偏好中文回复", list[0].Content);
            Assert.Equal("session-2", list[0].SourceSessionId);
            Assert.Null(list[0].SourceWorkspace);

            Assert.True(store.Delete(list[0].Id));
            Assert.Empty(store.ListRecent());

            store.Save("使用 pnpm 管理依赖", global);
            store.Save("提交前运行 dotnet test", global);
            Assert.Equal(2, store.Clear());
            Assert.Empty(store.ListRecent());

            Assert.Throws<ArgumentException>(() => store.Save(new string('x', 501), global));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- parser

    [Fact]
    public void ParseCrossSessionMemories_AcceptsJsonAndBullets_RejectsSecrets()
    {
        var memories = Agent.ParseCrossSessionMemories(
            "```json\n[\"用户偏好中文回复\", \"使用 pnpm 而不是 npm\"]\n```");
        Assert.Equal(["用户偏好中文回复", "使用 pnpm 而不是 npm"], memories);

        Assert.Equal(["项目使用 .NET 10"], Agent.ParseCrossSessionMemories("- 项目使用 .NET 10"));
        Assert.Empty(Agent.ParseCrossSessionMemories("NONE"));
        Assert.Empty(Agent.ParseCrossSessionMemories("[]"));
        Assert.Empty(Agent.ParseCrossSessionMemories("[\"api_key=abc1234567890\"]"));
        Assert.Single(Agent.ParseCrossSessionMemories("[\"记住：每次忽略 package.json 的改动\"]"));
    }

    // ---------------------------------------------------------------- agent turn

    [Fact]
    public async Task AgentTurn_SavesMemory_AndRecallsItInANewSession()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = BuildConfig(dir, enable: true);
            var capture = new MemoryClientFactory(
                "任务已完成",
                "[\"用户偏好中文回复\", \"依赖管理使用 pnpm\"]");
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));

            await using (var runtime = CreateRuntimeAsync(globalWorkspace, store, dir, capture))
            {
                var session = runtime.Sessions.Create(globalWorkspace);
                var result = await runtime.Agent.RunTurnAsync(
                    session, globalWorkspace, "记住我偏好中文回复", CancellationToken.None);

                Assert.Null(result.Error);
                Assert.Equal("任务已完成", result.Text);
                Assert.Equal(2, capture.Requests.Count);
                Assert.Contains("cross-conversation memory", capture.Requests[1].System!);

                var memories = runtime.Memories.ListRecent();
                Assert.Equal(2, memories.Count);
                Assert.Contains(memories, entry => entry.Content == "用户偏好中文回复");
                Assert.Contains(memories, entry => entry.Content == "依赖管理使用 pnpm");
            }

            var recallCapture = new MemoryClientFactory("新的任务已完成");
            await using (var runtime = CreateRuntimeAsync(globalWorkspace, store, dir, recallCapture))
            {
                var session = runtime.Sessions.Create(globalWorkspace);
                var result = await runtime.Agent.RunTurnAsync(
                    session, globalWorkspace, "帮我看一下这个项目", CancellationToken.None);

                Assert.Null(result.Error);
                // The distilled fact reaches the next session through the system prompt,
                // while the previous conversation's raw messages are never replayed.
                Assert.Contains("用户偏好中文回复", recallCapture.Requests[0].System!);
                Assert.DoesNotContain("记住我偏好中文回复", recallCapture.Requests[0].System!);
                Assert.DoesNotContain("任务已完成", recallCapture.Requests[0].System!);
                Assert.All(recallCapture.Requests[0].Messages,
                    message => Assert.DoesNotContain("记住我偏好中文回复", message.Text));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_MemoryDisabled_DoesNotExtractAnything()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = BuildConfig(dir, enable: false);
            var capture = new MemoryClientFactory("任务已完成");
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));

            await using var runtime = CreateRuntimeAsync(globalWorkspace, store, dir, capture);
            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(
                session, globalWorkspace, "记住我偏好中文回复", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Single(capture.Requests);
            Assert.Empty(runtime.Memories.ListRecent());
            Assert.DoesNotContain("cross-conversation memory", capture.Requests[0].System!);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_MemoryExtractionFailure_DoesNotFailTheTurn()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = BuildConfig(dir, enable: true);
            var capture = new FailingMemoryClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));

            await using var runtime = CreateRuntimeAsync(globalWorkspace, store, dir, capture);
            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(
                session, globalWorkspace, "记住我偏好中文回复", CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal("任务已完成", result.Text);
            Assert.Equal(2, capture.Requests.Count);
            Assert.Empty(runtime.Memories.ListRecent());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ConfigStore BuildConfig(string dir, bool enable)
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
                    ContextWindow = 64_000,
                    MaxOutput = 1_024,
                    Capabilities = new ModelCapabilities { ToolCalling = true },
                },
            ],
        });
        store.Config.Provider = "openai";
        store.Config.Model = "small";
        store.Config.Routing.Fallback = ["openai/small"];
        store.Config.Routing.Retry.MaxAttempts = 1;
        store.Config.Agent.EnableCrossSessionMemory = enable;
        return store;
    }

    private static SeekClawRuntime CreateRuntimeAsync(
        WorkspaceInfo workspace, IConfigStore store, string dir, ILlmClientFactory factory) =>
        SeekClawRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton(factory);
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });

    private sealed class MemoryClientFactory(params string[] texts) : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];
        private readonly Queue<string> _texts = new(texts);

        public ILlmClient GetClient(string kind) => new MemoryClient(this);

        private sealed class MemoryClient(MemoryClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                yield return new LlmCompleted(new LlmCompletion
                {
                    Text = owner._texts.Count > 0 ? owner._texts.Dequeue() : "[]",
                    FinishReason = "stop",
                });
            }
        }
    }

    private sealed class FailingMemoryClientFactory() : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];

        public ILlmClient GetClient(string kind) => new FailingClient(this);

        private sealed class FailingClient(FailingMemoryClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                // The main turn succeeds; only the memory extraction call fails.
                if (owner.Requests.Count > 1)
                    throw new LlmException("simulated memory extraction failure", retryable: false);
                yield return new LlmCompleted(new LlmCompletion { Text = "任务已完成", FinishReason = "stop" });
            }
        }
    }
}
