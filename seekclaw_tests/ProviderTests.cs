using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;

namespace SeekClaw.Tests;

public sealed class ProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekclaw-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigStore _store;
    private readonly ModelRegistry _registry;

    public ProviderTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        _store.Config.Providers.AddRange(
        [
            new ProviderConfig
            {
                Id = "alpha", Kind = "openai", BaseUrl = "https://alpha.test", Priority = 0,
                Models =
                [
                    new ModelConfig { Id = "big", Alias = "smart", ContextWindow = 200_000 },
                    new ModelConfig { Id = "small", Tags = ["fast"] },
                ],
            },
            new ProviderConfig
            {
                Id = "beta", Kind = "anthropic", BaseUrl = "https://beta.test", Priority = 1,
                Models = [new ModelConfig { Id = "big" }, new ModelConfig { Id = "tiny" }],
            },
            new ProviderConfig
            {
                Id = "off", Kind = "openai", BaseUrl = "https://off.test", Enabled = false,
                Models = [new ModelConfig { Id = "hidden" }],
            },
        ]);
        _registry = new ModelRegistry(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ProviderManager NewManager() => new(
        _store, _registry,
        new LlmClientFactory([]),
        new UsageTracker(new EventBus(), Path.Combine(_dir, "usage.jsonl")),
        new EventBus());

    [Fact]
    public void Registry_ResolvesQualifiedRef_Alias_AndUniqueBareId()
    {
        Assert.Equal("alpha/big", _registry.Resolve("alpha/big")!.Ref);
        Assert.Equal("alpha/big", _registry.Resolve("smart")!.Ref);
        Assert.Equal("beta/tiny", _registry.Resolve("tiny")!.Ref);
        Assert.Null(_registry.Resolve("big"));      // ambiguous across providers
        Assert.Null(_registry.Resolve("unknown"));
    }

    [Fact]
    public void Registry_All_SkipsDisabledProviders_UnlessRequested()
    {
        Assert.DoesNotContain(_registry.All(), m => m.Provider.Id == "off");
        Assert.Contains(_registry.All(includeDisabledProviders: true), m => m.Provider.Id == "off");
    }

    [Fact]
    public void Registry_Search_MatchesRefAliasAndTags()
    {
        Assert.Contains(_registry.Search("smart"), m => m.Ref == "alpha/big");
        Assert.Contains(_registry.Search("fast"), m => m.Ref == "alpha/small");
        Assert.Contains(_registry.Search("beta/"), m => m.Provider.Id == "beta");
    }

    [Fact]
    public void AnthropicBody_GroupsAllToolResultsIntoTheImmediateUserMessage()
    {
        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls =
            [
                new ToolCallRequest("c1", "read_file", "{}"),
                new ToolCallRequest("c2", "list_files", "{}"),
            ],
        };
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "deepseek", Kind = "anthropic", BaseUrl = "https://example.test" },
            Model = new ModelConfig { Id = "deepseek-model" },
            Messages =
            [
                ChatMessage.User("inspect"),
                assistant,
                ChatMessage.ToolResult("c1", "read_file", "one", true),
                ChatMessage.ToolResult("c2", "list_files", "two", true),
            ],
        };

        var messages = AnthropicClient.BuildBody(request)["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var results = messages[2]!["content"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal(["c1", "c2"], results.Select(item => item!["tool_use_id"]!.GetValue<string>()));
    }

    [Fact]
    public void AnthropicBody_AddsCacheCheckpoints_ToStableSystemAndTools()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "anthropic", Kind = "anthropic", BaseUrl = "https://example.test" },
            Model = new ModelConfig
            {
                Id = "model",
                Capabilities = new ModelCapabilities { ToolCalling = true },
            },
            System = "stable system prompt",
            Messages = [ChatMessage.User("hello")],
            Tools =
            [
                new ToolDefinition("read_file", "Read a file", new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "object",
                }),
            ],
        };

        var body = AnthropicClient.BuildBody(request);
        var system = body["system"]!.AsArray();
        Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
        var tools = body["tools"]!.AsArray();
        Assert.Equal("ephemeral", tools[0]!["cache_control"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AnthropicBody_CacheCheckpoints_CanBeDisabledForCompatibleEndpoints()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig
            {
                Id = "compatible",
                Kind = "anthropic",
                BaseUrl = "https://example.test",
                PromptCaching = false,
            },
            Model = new ModelConfig { Id = "model" },
            System = "system prompt",
            Messages = [ChatMessage.User("hello")],
        };

        var body = AnthropicClient.BuildBody(request);
        Assert.Equal("system prompt", body["system"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ReasoningLevel.XHigh)]
    [InlineData(ReasoningLevel.Ultra)]
    public void DeepSeek_ExtendedReasoning_MapsToMax(ReasoningLevel requested)
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig
            {
                Id = "deepseek",
                Kind = "openai",
                BaseUrl = "https://api.deepseek.test/v1",
            },
            Model = new ModelConfig
            {
                Id = "deepseek-reasoner",
                Capabilities = new ModelCapabilities
                {
                    MaxReasoningLevel = ReasoningLevel.Ultra,
                },
            },
            Messages = [ChatMessage.User("think")],
            ReasoningLevel = requested,
        };

        Assert.Equal(ReasoningLevel.Max,
            ReasoningLevelAdapter.Normalize(request.Provider, request.Model, requested));
        Assert.Equal("max",
            OpenAiCompatibleClient.BuildBody(request)["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAiReasoning_UsesProviderMapping_AfterModelCapabilityNormalization()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig
            {
                Id = "custom",
                Kind = "openai",
                BaseUrl = "https://example.test/v1",
                ReasoningEffortMap = new Dictionary<string, string> { ["ultra"] = "xhigh" },
            },
            Model = new ModelConfig
            {
                Id = "reasoner",
                Capabilities = new ModelCapabilities
                {
                    Reasoning = true,
                    MaxReasoningLevel = ReasoningLevel.Ultra,
                },
            },
            Messages = [ChatMessage.User("think")],
            ReasoningLevel = ReasoningLevel.Ultra,
        };

        Assert.Equal("xhigh",
            OpenAiCompatibleClient.BuildBody(request)["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void AnthropicReasoning_MapsNeutralLevelToBudget()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "anthropic", Kind = "anthropic", BaseUrl = "https://example.test" },
            Model = new ModelConfig
            {
                Id = "claude",
                MaxOutput = 64_000,
                Capabilities = new ModelCapabilities
                {
                    Thinking = true,
                    MaxReasoningLevel = ReasoningLevel.Ultra,
                },
            },
            Messages = [ChatMessage.User("think")],
            EnableThinking = true,
            ThinkingBudgetTokens = 4_096,
            ReasoningLevel = ReasoningLevel.High,
        };

        var body = AnthropicClient.BuildBody(request);
        Assert.Equal(8_192, body["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Candidates_WorkspaceOverride_BeatsProfile_ThenStrategy_ThenFallback()
    {
        var profile = _store.Config.GetActiveProfile();
        profile.Provider = "alpha";
        profile.Model = "small";
        profile.Strategy = "quality";
        _store.Config.Routing.Strategies["quality"] = ["beta/big"];
        _store.Config.Routing.Fallback = ["beta/tiny"];

        var manager = NewManager();
        var workspace = new WorkspaceConfig { Model = "alpha/big" };

        var chain = manager.BuildCandidates(workspace).Select(m => m.Ref).ToList();
        Assert.Equal(["alpha/big", "alpha/small", "beta/big", "beta/tiny"], chain);

        // Without the workspace override the profile model leads.
        chain = manager.BuildCandidates(null).Select(m => m.Ref).ToList();
        Assert.Equal(["alpha/small", "beta/big", "beta/tiny"], chain);
    }

    [Fact]
    public void Candidates_FallBackToAllModels_WhenNothingConfigured()
    {
        var manager = NewManager();
        var chain = manager.BuildCandidates(null);
        Assert.NotEmpty(chain);
        Assert.Equal("alpha/big", chain[0].Ref); // provider priority order
    }

    [Fact]
    public void CircuitBreaker_Opens_AfterThreshold_AndRecovers()
    {
        var retry = new RetryConfig { CircuitBreakThreshold = 2, CircuitCooldownSeconds = 60 };
        var breaker = new CircuitBreaker(retry);

        Assert.False(breaker.IsOpen("x/y"));
        breaker.RecordFailure("x/y");
        Assert.False(breaker.IsOpen("x/y"));
        breaker.RecordFailure("x/y");
        Assert.True(breaker.IsOpen("x/y"));

        breaker.RecordSuccess("x/y");
        Assert.False(breaker.IsOpen("x/y"));
    }

    [Fact]
    public void ComputeCost_UsesPerMillionPricing()
    {
        var model = new ModelConfig { InputPricePerMTok = 3, OutputPricePerMTok = 15 };
        var cost = ProviderManager.ComputeCost(model, new TokenUsage(1_000_000, 200_000));
        Assert.Equal(3m + 3m, cost); // 3 for input + 15*0.2 for output
    }

    [Fact]
    public void UsageTracker_Records_AndAggregates()
    {
        var tracker = new UsageTracker(new EventBus(), Path.Combine(_dir, "usage.jsonl"));
        tracker.Record(new UsageEntry { Provider = "p", Model = "m", InputTokens = 100, TotalInputTokens = 140, CachedInputTokens = 40, OutputTokens = 50, Cost = 0.01m, ElapsedMs = 120, Success = true });
        tracker.Record(new UsageEntry { Provider = "p", Model = "m", InputTokens = 200, TotalInputTokens = 260, CachedInputTokens = 60, OutputTokens = 100, Cost = 0.02m, ElapsedMs = 80, Success = false });

        var aggregate = Assert.Single(tracker.Aggregate());
        Assert.Equal(2, aggregate.Calls);
        Assert.Equal(1, aggregate.Failures);
        Assert.Equal(300, aggregate.InputTokens);
        Assert.Equal(400, aggregate.TotalInputTokens);
        Assert.Equal(100, aggregate.CachedInputTokens);
        Assert.Equal(150, aggregate.OutputTokens);
        Assert.Equal(0.03m, aggregate.Cost);
        Assert.Equal(0.5, aggregate.SuccessRate);
    }
}
