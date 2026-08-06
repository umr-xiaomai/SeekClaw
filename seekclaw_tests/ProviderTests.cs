using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;

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
        // The first-run seed now ships code-defined defaults; drop them so these tests
        // exercise exactly the isolated provider set they declare below.
        _store.Config.Providers.Clear();
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

    private ProviderManager NewManager(ILlmHttpFactory? httpFactory = null, CircuitBreaker? breaker = null) => new(
        _store, _registry,
        new LlmClientFactory([]),
        httpFactory ?? new LlmHttpFactory(),
        new UsageTracker(new EventBus(), Path.Combine(_dir, "usage.jsonl")),
        new EventBus(),
        breaker);

    [Fact]
    public async Task FetchModels_ReadsOpenAiDataAndDeduplicatesIds()
    {
        var ids = await NewManager(new StubHttpFactory(
            """{"data":[{"id":"new-model"},{"id":"new-model"},{"id":"other"}]}""")).FetchModelsAsync(
                _store.Config.Providers[0], "https://models.test/catalog");

        Assert.Equal(["new-model", "other"], ids);
    }

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
    public void OpenAiBody_MapsMultipleImagesToImageUrlContentParts()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "openai", Kind = "openai", BaseUrl = "https://example.test/v1" },
            Model = new ModelConfig { Id = "vision-model" },
            Messages =
            [
                ChatMessage.User("比较这两张图片",
                [
                    new ChatImageAttachment("first", "first.png", "image/png", "AQID", 3),
                    new ChatImageAttachment("second", "second.webp", "image/webp", "BAUG", 3),
                ]),
            ],
        };

        var body = OpenAiCompatibleClient.BuildBody(request);
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(3, content.Count);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("比较这两张图片", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AQID",
            content[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("data:image/webp;base64,BAUG",
            content[2]!["image_url"]!["url"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Null(body["stream_options"]);
    }

    [Fact]
    public void OpenAiBody_DeepSeekAssistantThinking_SendsReasoningContentBack()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "deepseek", Kind = "openai", BaseUrl = "https://api.deepseek.com" },
            Model = new ModelConfig { Id = "deepseek-v4-flash" },
            Messages =
            [
                ChatMessage.User("go"),
                new ChatMessage { Role = ChatRole.Assistant, Text = "", Thinking = "previous thinking" },
            ],
        };

        var body = OpenAiCompatibleClient.BuildBody(request);
        var assistant = body["messages"]![1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("previous thinking", assistant["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAiBody_NonDeepSeekAssistantThinking_OmitsReasoningContent()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "openai", Kind = "openai", BaseUrl = "https://example.test/v1" },
            Model = new ModelConfig { Id = "gpt" },
            Messages =
            [
                ChatMessage.User("go"),
                new ChatMessage { Role = ChatRole.Assistant, Text = "", Thinking = "private thinking" },
            ],
        };

        var body = OpenAiCompatibleClient.BuildBody(request);
        Assert.Null(body["messages"]![1]!["reasoning_content"]);
    }

    [Fact]
    public void OpenAiBody_ReasoningModel_UsesMaxCompletionTokens()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "openai", Kind = "openai", BaseUrl = "https://api.openai.com/v1" },
            Model = new ModelConfig
            {
                Id = "gpt-5.5",
                MaxOutput = 128_000,
                Capabilities = new ModelCapabilities { Reasoning = true },
            },
            Messages = [ChatMessage.User("hi")],
            MaxTokens = 128_000,
        };

        var body = OpenAiCompatibleClient.BuildBody(request);
        Assert.Equal(128_000, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(body["max_tokens"]);
    }

    [Fact]
    public void OpenAiBody_DeepSeekReasoningFlag_KeepsMaxTokens()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "deepseek", Kind = "openai", BaseUrl = "https://api.deepseek.com" },
            Model = new ModelConfig
            {
                Id = "deepseek-v4-flash",
                MaxOutput = 8_192,
                Capabilities = new ModelCapabilities { Reasoning = true },
            },
            Messages = [ChatMessage.User("hi")],
            MaxTokens = 8_192,
        };

        var body = OpenAiCompatibleClient.BuildBody(request);
        Assert.Equal(8_192, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["max_completion_tokens"]);
    }

    [Fact]
    public void AnthropicBody_MapsMultipleImagesToBase64ContentParts()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "anthropic", Kind = "anthropic", BaseUrl = "https://example.test" },
            Model = new ModelConfig { Id = "vision-model" },
            Messages =
            [
                ChatMessage.User("分别描述",
                [
                    new ChatImageAttachment("first", "first.jpg", "image/jpeg", "AQID", 3),
                    new ChatImageAttachment("second", "second.gif", "image/gif", "BAUG", 3),
                ]),
            ],
        };

        var body = AnthropicClient.BuildBody(request);
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(3, content.Count);
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("image/jpeg", content[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("AQID", content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("image/gif", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("text", content[2]!["type"]!.GetValue<string>());
        Assert.Equal("分别描述", content[2]!["text"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void OpenAiNonStreamingResponse_ParsesVisionTextThinkingToolsAndUsage()
    {
        var response = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "choices": [{
                "finish_reason": "stop",
                "message": {
                  "content": "图中是一只猫。",
                  "reasoning_content": "检查图像主体",
                  "tool_calls": [{"id":"call_1","function":{"name":"lookup","arguments":"{\"q\":\"cat\"}"}}]
                }
              }],
              "usage": {"prompt_tokens": 120, "completion_tokens": 18, "prompt_tokens_details": {"cached_tokens": 20}}
            }
            """);

        var completion = OpenAiCompatibleClient.ParseCompletion(response, "vision");

        Assert.Equal("图中是一只猫。", completion.Text);
        Assert.Equal("检查图像主体", completion.Thinking);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal("lookup", Assert.Single(completion.ToolCalls).Name);
        Assert.Equal(120, completion.Usage.TotalInputTokens);
        Assert.Equal(20, completion.Usage.CachedInputTokens);
        Assert.Equal(18, completion.Usage.OutputTokens);
    }

    [Fact]
    public void AnthropicNonStreamingResponse_ParsesVisionTextThinkingToolsAndUsage()
    {
        var response = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "type": "message",
              "content": [
                {"type":"thinking","thinking":"检查图像主体"},
                {"type":"text","text":"图中是一只猫。"},
                {"type":"tool_use","id":"tool_1","name":"lookup","input":{"q":"cat"}}
              ],
              "stop_reason": "end_turn",
              "usage": {"input_tokens": 120, "output_tokens": 18, "cache_read_input_tokens": 20}
            }
            """);

        var completion = AnthropicClient.ParseCompletion(response, "vision");

        Assert.Equal("图中是一只猫。", completion.Text);
        Assert.Equal("检查图像主体", completion.Thinking);
        Assert.Equal("end_turn", completion.FinishReason);
        Assert.Equal("lookup", Assert.Single(completion.ToolCalls).Name);
        Assert.Equal(120, completion.Usage.TotalInputTokens);
        Assert.Equal(20, completion.Usage.CachedInputTokens);
        Assert.Equal(18, completion.Usage.OutputTokens);
    }

    [Fact]
    public async Task OpenAiVisionResponse_FallsBackToSse_WhenGatewayIgnoresStreamFalse()
    {
        const string body = """
            data: {"choices":[{"delta":{"content":"图像已读取。"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var client = new OpenAiCompatibleClient(new StubHttpFactory(body, "text/event-stream"));
        var request = VisionRequest("openai");
        LlmCompletion? completion = null;

        await foreach (var evt in client.StreamAsync(request, CancellationToken.None))
            if (evt is LlmCompleted done) completion = done.Completion;

        Assert.NotNull(completion);
        Assert.Equal("图像已读取。", completion.Text);
        Assert.Equal("stop", completion.FinishReason);
    }

    [Fact]
    public async Task AnthropicVisionResponse_FallsBackToSse_WhenGatewayIgnoresStreamFalse()
    {
        const string body = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":5}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"图像已读取。"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var client = new AnthropicClient(new StubHttpFactory(body, "text/event-stream"));
        var request = VisionRequest("anthropic");
        LlmCompletion? completion = null;

        await foreach (var evt in client.StreamAsync(request, CancellationToken.None))
            if (evt is LlmCompleted done) completion = done.Completion;

        Assert.NotNull(completion);
        Assert.Equal("图像已读取。", completion.Text);
        Assert.Equal("end_turn", completion.FinishReason);
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
    public void AnthropicReasoning_LongThinkingUsesNearlyTheWholeOutputWindow()
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
            ThinkingBudgetTokens = 16_384,
            ReasoningLevel = ReasoningLevel.Ultra,
        };

        var body = AnthropicClient.BuildBody(request);
        // 16_384 × 16 would be 262_144; the budget is capped only by max_tokens minus a
        // small answer reserve, so a long reasoning phase is never cut off at half.
        Assert.Equal(64_000 - 4_096, body["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void AnthropicReasoning_SmallOutputWindow_StillReservesRoomForAnswer()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "anthropic", Kind = "anthropic", BaseUrl = "https://example.test" },
            Model = new ModelConfig
            {
                Id = "claude-small",
                MaxOutput = 8_192,
                Capabilities = new ModelCapabilities
                {
                    Thinking = true,
                    MaxReasoningLevel = ReasoningLevel.Max,
                },
            },
            Messages = [ChatMessage.User("think")],
            EnableThinking = true,
            ThinkingBudgetTokens = 16_384,
            ReasoningLevel = ReasoningLevel.High,
        };

        var body = AnthropicClient.BuildBody(request);
        // min(16_384 × 2, 8_192 - 2_048) → 6_144
        Assert.Equal(6_144, body["thinking"]!["budget_tokens"]!.GetValue<int>());
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
    public void BuildCandidates_RespectsInjectedSharedCircuitBreaker()
    {
        // The daemon injects one process-wide breaker into every turn runtime; an
        // opened circuit must be respected by a manager built around that breaker.
        var breaker = new CircuitBreaker(new RetryConfig { CircuitBreakThreshold = 1, CircuitCooldownSeconds = 60 });
        breaker.RecordFailure("alpha/big");

        var candidates = NewManager(breaker: breaker).BuildCandidates();

        Assert.DoesNotContain(candidates, candidate => candidate.Ref == "alpha/big");
        Assert.Contains(candidates, candidate => candidate.Ref == "beta/big");
    }

    [Fact]
    public void Di_LastWins_WhenDaemonOverridesHttpFactoryAndBreaker()
    {
        // The daemon registers process-wide instances AFTER AddSeekClawRuntime; the
        // container must resolve those instances for the turn runtime's providers.
        var breaker = new CircuitBreaker(new RetryConfig { CircuitBreakThreshold = 1, CircuitCooldownSeconds = 60 });
        breaker.RecordFailure("alpha/big");

        var services = new ServiceCollection()
            .AddSeekClawRuntime()
            .AddSingleton<IConfigStore>(_store)
            .AddSingleton(new SeekClawDatabase(Path.Combine(_dir, "di.db")))
            .AddSingleton<ILlmHttpFactory>(new LlmHttpFactory())
            .AddSingleton(breaker)
            .BuildServiceProvider();

        var manager = services.GetRequiredService<IProviderManager>();
        Assert.DoesNotContain(manager.BuildCandidates(), candidate => candidate.Ref == "alpha/big");
        Assert.Contains(manager.BuildCandidates(), candidate => candidate.Ref == "beta/big");
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

    [Fact]
    public async Task StreamAsync_ContinuesPastNonRetryableFallbackFailure()
    {
        var store = _store;
        store.Config.Routing.Retry.MaxAttempts = 1;
        store.Config.Routing.Strategies.Clear();
        store.Config.Routing.Fallback = ["beta/tiny", "gamma/ok"];
        store.Config.GetActiveProfile().Provider = "alpha";
        store.Config.GetActiveProfile().Model = "big";
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "gamma", Kind = "gamma", BaseUrl = "https://gamma.test", Priority = 2,
            Models = [new ModelConfig { Id = "ok" }],
        });

        var manager = NewManagerWithClients(
            store, _registry, Path.Combine(_dir, "usage2.jsonl"),
            new StubLlmClient("openai", new LlmException("local server failed", 500, retryable: true)),
            new StubLlmClient("anthropic", new LlmException("HTTP 401: x-api-key header is required", 401, retryable: false)),
            new StubLlmClient("gamma", null, new LlmCompleted(new LlmCompletion { Text = "ok" })));

        var request = new LlmRequest
        {
            Provider = store.Config.Providers[0],
            Model = store.Config.Providers[0].Models[0],
            Messages = [ChatMessage.User("hi")],
        };

        var text = "";
        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var evt in manager.StreamAsync(_ => request, null, CancellationToken.None))
                if (evt is LlmCompleted done) text = done.Completion.Text;
        });

        // A non-retryable rejection by a fallback candidate must not abort the chain:
        // the working third candidate still completes the stream.
        Assert.Null(ex);
        Assert.Equal("ok", text);
    }

    [Fact]
    public async Task StreamAsync_AllCandidatesFailed_AggregatesEveryModelError()
    {
        var store = _store;
        store.Config.Routing.Retry.MaxAttempts = 1;
        store.Config.Routing.Strategies.Clear();
        store.Config.Routing.Fallback = ["beta/tiny"];
        store.Config.GetActiveProfile().Provider = "alpha";
        store.Config.GetActiveProfile().Model = "big";

        var manager = NewManagerWithClients(
            store, _registry, Path.Combine(_dir, "usage3.jsonl"),
            new StubLlmClient("openai", new LlmException("local server exploded", 500, retryable: true)),
            new StubLlmClient("anthropic", new LlmException("HTTP 401: x-api-key header is required", 401, retryable: false)));

        var request = new LlmRequest
        {
            Provider = store.Config.Providers[0],
            Model = store.Config.Providers[0].Models[0],
            Messages = [ChatMessage.User("hi")],
        };

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in manager.StreamAsync(_ => request, null, CancellationToken.None)) { }
        });

        // The final error must explain every attempted model, the active model first,
        // instead of surfacing only the last fallback's confusing 401.
        var aggregate = Assert.IsType<LlmException>(ex);
        Assert.Contains("alpha/big", aggregate.Message);
        Assert.Contains("local server exploded", aggregate.Message);
        Assert.Contains("beta/tiny", aggregate.Message);
        Assert.Contains("HTTP 401: x-api-key header is required", aggregate.Message);
    }

    [Fact]
    public async Task StreamAsync_FailoverDisabled_StopsAfterActiveModelFails()
    {
        var store = _store;
        store.Config.Routing.FailoverEnabled = false;
        store.Config.Routing.Retry.MaxAttempts = 1;
        store.Config.Routing.Strategies.Clear();
        store.Config.Routing.Fallback = ["beta/tiny"];
        store.Config.GetActiveProfile().Provider = "alpha";
        store.Config.GetActiveProfile().Model = "big";

        var alpha = new StubLlmClient("openai", new LlmException("local server exploded", 500, retryable: true));
        var beta = new StubLlmClient("anthropic", new LlmException("HTTP 401: x-api-key header is required", 401, retryable: false));
        var manager = NewManagerWithClients(store, _registry, Path.Combine(_dir, "usage5.jsonl"), alpha, beta);

        var request = new LlmRequest
        {
            Provider = store.Config.Providers[0],
            Model = store.Config.Providers[0].Models[0],
            Messages = [ChatMessage.User("hi")],
        };

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in manager.StreamAsync(_ => request, null, CancellationToken.None)) { }
        });

        // With failover disabled only the active model is tried; the turn stops with the
        // real error and the fallback provider is never contacted.
        Assert.NotNull(ex);
        Assert.Equal("local server exploded", ex.Message);
        Assert.Equal(1, alpha.Calls);
        Assert.Equal(0, beta.Calls);
    }

    [Fact]
    public async Task StreamAsync_ActiveModelNonRetryableError_SurfacesImmediately()
    {
        var store = _store;
        store.Config.Routing.Retry.MaxAttempts = 1;
        store.Config.Routing.Strategies.Clear();
        store.Config.Routing.Fallback = ["beta/tiny"];
        store.Config.GetActiveProfile().Provider = "alpha";
        store.Config.GetActiveProfile().Model = "big";

        var manager = NewManagerWithClients(
            store, _registry, Path.Combine(_dir, "usage4.jsonl"),
            new StubLlmClient("openai", new LlmException("HTTP 401: bad key", 401, retryable: false)),
            new StubLlmClient("anthropic", new LlmException("HTTP 401: x-api-key header is required", 401, retryable: false)));

        var request = new LlmRequest
        {
            Provider = store.Config.Providers[0],
            Model = store.Config.Providers[0].Models[0],
            Messages = [ChatMessage.User("hi")],
        };

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in manager.StreamAsync(_ => request, null, CancellationToken.None)) { }
        });

        // A non-retryable failure of the model the user actually selected is the real
        // problem; it must be reported directly instead of being hidden by failover.
        Assert.NotNull(ex);
        Assert.Equal("HTTP 401: bad key", ex.Message);
    }

    private static ProviderManager NewManagerWithClients(
        ConfigStore store,
        ModelRegistry registry,
        string usageFile,
        params ILlmClient[] clients) => new(
        store,
        registry,
        new LlmClientFactory(clients),
        new LlmHttpFactory(),
        new UsageTracker(new EventBus(), usageFile),
        new EventBus());

    private sealed class StubLlmClient(string kind, LlmException? error = null, params LlmStreamEvent[] events) : ILlmClient
    {
        public int Calls { get; private set; }

        public string Kind => kind;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (error is not null) throw error;
            foreach (var evt in events) yield return evt;
        }
    }

[Fact]
    public void ToolArguments_ParsesValidJsonUnchanged()
    {
        var raw = """{"path": "a.txt", "content": "hello"}""";
        var (obj, json) = ToolArguments.Parse(raw);
        Assert.NotNull(obj);
        Assert.Equal(raw, json);
    }

    [Fact]
    public void ToolArguments_RepairsTruncatedJson()
    {
        // Cut off mid-string by the output-token cap.
        var (obj, json) = ToolArguments.Parse("""{"path": "a.txt", "content": "hello""");
        Assert.NotNull(obj);
        Assert.Equal("hello", obj!["content"]!.GetValue<string>());

        // Cut off mid-object with an open brace.
        var (obj2, json2) = ToolArguments.Parse("""{"a": {"b": 1""");
        Assert.NotNull(obj2);
        Assert.Equal(1, obj2!["a"]!["b"]!.GetValue<int>());
    }

    [Fact]
    public void ToolArguments_ExtractsBalancedObjectFromTrailingText()
    {
        var (obj, json) = ToolArguments.Parse("""{"a": 1} and some trailing notes""");
        Assert.NotNull(obj);
        Assert.Equal("""{"a": 1}""", json);
    }

    [Fact]
    public void ToolArguments_Unrecoverable_ReturnsEmptyObject()
    {
        var (obj, json) = ToolArguments.Parse("this is not json at all");
        Assert.Null(obj);
        Assert.Equal("{}", json);
        Assert.Equal("{}", ToolArguments.Sanitize("not json"));
        Assert.Equal("{}", ToolArguments.Sanitize(""));
    }

    [Fact]
    public void ExtractErrorMessage_HandlesStringErrorsAndNonObjectBodies()
    {
        Assert.Equal("plain message", OpenAiCompatibleClient.ExtractErrorMessage("""{"error": "plain message"}"""));
        Assert.Equal("nested", OpenAiCompatibleClient.ExtractErrorMessage("""{"error": {"message": "nested"}}"""));
        Assert.Equal("top", OpenAiCompatibleClient.ExtractErrorMessage("""{"message": "top"}"""));
        // A JSON array body must not throw "The node must be of type 'JsonObject'.".
        Assert.StartsWith("[", OpenAiCompatibleClient.ExtractErrorMessage("""["not","an","object"]"""));
    }

    [Fact]
    public void ParseCompletion_NonObjectRoot_ThrowsCleanLlmException()
    {
        var ex = Assert.Throws<LlmException>(() =>
            OpenAiCompatibleClient.ParseCompletion(JsonNode.Parse("""[1, 2]"""), "local"));
        Assert.Contains("invalid non-streaming response", ex.Message);
    }

    [Fact]
    public async Task StreamAsync_SkipsNonObjectSseFrames()
    {
        // Some gateways emit non-object frames (arrays, scalars) in the event stream;
        // they must be skipped instead of aborting the turn with a JsonNode type error.
        var body = "data: [1, 2]\n\ndata: \"scalar\"\n\ndata: [DONE]\n\n";
        var client = new OpenAiCompatibleClient(new StubHttpFactory(body, "text/event-stream"));
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "local", Kind = "openai", BaseUrl = "https://example.test", TimeoutSeconds = 10 },
            Model = new ModelConfig { Id = "m" },
            Messages = [ChatMessage.User("hi")],
        };

        LlmCompletion? completion = null;
        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var evt in client.StreamAsync(request, CancellationToken.None))
                if (evt is LlmCompleted done) completion = done.Completion;
        });

        Assert.Null(ex);
        Assert.NotNull(completion);
    }

    [Fact]
    public void BuildBody_SanitizesMalformedToolArguments()
    {
        var assistant = new ChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls = [new ToolCallRequest("c1", "write_file", """{"path": "a.txt", "content": "oops""")],
        };
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "local", Kind = "openai", BaseUrl = "https://example.test" },
            Model = new ModelConfig { Id = "m" },
            Messages = [assistant],
        };

        var body = OpenAiCompatibleClient.BuildBody(request);
        var arguments = body["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        Assert.NotNull(JsonNode.Parse(arguments) as JsonObject); // valid JSON in the outbound request
    }

    private static LlmRequest VisionRequest(string kind) => new()
    {
        Provider = new ProviderConfig
        {
            Id = $"{kind}-vision",
            Kind = kind,
            BaseUrl = "https://example.test",
            TimeoutSeconds = 10,
        },
        Model = new ModelConfig { Id = "vision-model" },
        Messages =
        [
            ChatMessage.User("描述图片",
            [
                new ChatImageAttachment("image", "image.png", "image/png", "AQID", 3),
            ]),
        ],
    };

    private sealed class StubHttpFactory(string body, string? mediaType = null) : ILlmHttpFactory
    {
        public HttpClient GetClient(ProviderConfig provider) => new(new StubHandler(body, mediaType));
    }

    private sealed class StubHandler(string body, string? mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType ?? "application/json"),
            });
    }
}
