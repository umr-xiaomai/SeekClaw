using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Workspaces;
using Xunit;

namespace SeekClaw.Tests;

public sealed class SubAgentTests
{
    [Fact]
    public void SubAgentRegistry_HasDefaultRoles()
    {
        var registry = new SubAgentRegistry();
        Assert.NotNull(registry.Resolve("researcher"));
        Assert.NotNull(registry.Resolve("coder"));
        Assert.NotNull(registry.Resolve("tester"));
        Assert.NotNull(registry.Resolve("general"));

        var researcher = registry.Resolve("researcher")!;
        Assert.False(researcher.AllowMutating);
        Assert.Contains("read_file", researcher.AllowedTools!);
        Assert.DoesNotContain("write_file", researcher.AllowedTools!);
        Assert.DoesNotContain("edit_file", researcher.AllowedTools!);

        var coder = registry.Resolve("coder")!;
        Assert.True(coder.AllowMutating);
        Assert.Contains("edit_file", coder.AllowedTools!);

        var tester = registry.Resolve("tester")!;
        Assert.False(tester.AllowMutating);
        Assert.Contains("bash", tester.AllowedTools!);
    }

    [Fact]
    public void SubAgentRegistry_CustomRegistration()
    {
        var registry = new SubAgentRegistry();
        var custom = new SubAgentDefinition(
            Role: "reviewer",
            Description: "Code review specialist",
            PromptKey: "subagent/reviewer",
            AllowedTools: new HashSet<string> { "read_file", "grep" },
            AllowMutating: false);

        registry.Register(custom);
        var resolved = registry.Resolve("reviewer");
        Assert.NotNull(resolved);
        Assert.Equal("reviewer", resolved.Role);
    }

    [Fact]
    public void ToolRegistry_RetainOnly_FiltersCorrectly()
    {
        var registry = new ToolRegistry();
        var prompts = new FilePromptProvider();

        registry.Register(new ReadFileTool(prompts));
        registry.Register(new WriteFileTool(prompts));
        registry.Register(new GlobTool(prompts));

        Assert.Equal(3, registry.All.Count);

        registry.RetainOnly(t => !t.Mutating);
        Assert.Equal(2, registry.All.Count);
        Assert.Null(registry.Resolve("write_file"));
        Assert.NotNull(registry.Resolve("read_file"));
        Assert.NotNull(registry.Resolve("glob"));
    }

    [Fact]
    public void InMemorySessionStore_BasicOperations()
    {
        var store = new InMemorySessionStore();
        var ws = new WorkspaceInfo { Root = "C:\\test", ProjectKinds = [] };

        var session = store.Create(ws);
        Assert.NotNull(session);
        Assert.Equal(":memory:", session.FilePath);

        store.Append(session, ChatMessage.User("Hello"));
        store.Append(session, ChatMessage.Assistant("Hi there"));
        Assert.Equal(2, session.Messages.Count);

        store.Truncate(ws, session.Header.Id, 1);
        Assert.Single(session.Messages);
        Assert.Equal("Hello", session.Messages[0].Text);

        store.ReplaceHistory(session, [ChatMessage.User("New history")]);
        Assert.Single(session.Messages);
        Assert.Equal("New history", session.Messages[0].Text);

        store.Delete(ws, session.Header.Id);
        Assert.Null(store.Load(ws, session.Header.Id));
    }

    [Fact]
    public async Task InvokeSubAgentTool_FailsWhenDepthExceeded()
    {
        var prompts = new FilePromptProvider();
        var registry = new SubAgentRegistry();
        var coordinator = new NoopFileLockCoordinator();
        var http = new LlmHttpFactory();
        var breaker = new CircuitBreaker(new RetryConfig());
        var runner = new SubAgentRunner(registry, coordinator, http, breaker);
        var tool = new InvokeSubAgentTool(prompts, runner);

        var ws = new WorkspaceInfo { Root = "C:\\test", ProjectKinds = [] };
        var bus = new EventBus();
        var context = new ToolContext
        {
            Workspace = ws,
            Events = bus,
            Agent = new AgentConfig(),
            SubAgentDepth = 1, // Already in sub-agent!
        };

        var args = new JsonObject
        {
            ["role"] = "researcher",
            ["task"] = "Explore codebase",
        };

        var result = await tool.ExecuteAsync(args, context, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("maximum delegation depth", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeSubAgentTool_ValidatesRequiredArguments()
    {
        var prompts = new FilePromptProvider();
        var registry = new SubAgentRegistry();
        var coordinator = new NoopFileLockCoordinator();
        var http = new LlmHttpFactory();
        var breaker = new CircuitBreaker(new RetryConfig());
        var runner = new SubAgentRunner(registry, coordinator, http, breaker);
        var tool = new InvokeSubAgentTool(prompts, runner);

        var ws = new WorkspaceInfo { Root = "C:\\test", ProjectKinds = [] };
        var bus = new EventBus();
        var context = new ToolContext
        {
            Workspace = ws,
            Events = bus,
            Agent = new AgentConfig(),
            SubAgentDepth = 0,
        };

        var noRole = new JsonObject { ["task"] = "Explore" };
        var res1 = await tool.ExecuteAsync(noRole, context, CancellationToken.None);
        Assert.False(res1.Success);
        Assert.Contains("role", res1.Output, StringComparison.OrdinalIgnoreCase);

        var noTask = new JsonObject { ["role"] = "researcher" };
        var res2 = await tool.ExecuteAsync(noTask, context, CancellationToken.None);
        Assert.False(res2.Success);
        Assert.Contains("task", res2.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubAgentRunner_ExecutesSuccessfullyAndPublishesEvents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seekclaw_subagent_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var ws = new WorkspaceInfo { Root = tempDir, ProjectKinds = [] };
            var bus = new EventBus();
            using var sub = bus.Subscribe();

            var clientFactory = new MockLlmClientFactory("Sub-agent completed research: found 2 services.");
            var coordinator = new FileLockCoordinator();
            var http = new LlmHttpFactory();
            var breaker = new CircuitBreaker(new RetryConfig());
            var registry = new SubAgentRegistry();
            var runner = new SubAgentRunner(registry, coordinator, http, breaker, clientFactory);
            var prompts = new FilePromptProvider();
            var tool = new InvokeSubAgentTool(prompts, runner);

            var context = new ToolContext
            {
                Workspace = ws,
                Events = bus,
                Agent = new AgentConfig(),
                Coordinator = coordinator,
                Owner = "parent-turn-1",
                SubAgentDepth = 0,
            };

            var args = new JsonObject
            {
                ["role"] = "researcher",
                ["task"] = "Analyze services in project",
            };

            var result = await tool.ExecuteAsync(args, context, CancellationToken.None);
            Assert.True(result.Success);
            Assert.Contains("Sub-agent completed research", result.Output);

            // Read events published to parent bus
            var events = new List<RuntimeEvent>();
            while (sub.Reader.TryRead(out var evt))
            {
                events.Add(evt);
            }

            Assert.Contains(events, e => e is SubAgentStartedEvent start && start.Role == "researcher");
            Assert.Contains(events, e => e is SubAgentCompletedEvent done && done.Role == "researcher");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class MockLlmClientFactory(string responseText) : ILlmClientFactory
    {
        public ILlmClient GetClient(string kind) => new MockLlmClient(responseText);

        private sealed class MockLlmClient(string responseText) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                yield return new LlmCompleted(new LlmCompletion { Text = responseText, FinishReason = "stop" });
            }
        }
    }
}
