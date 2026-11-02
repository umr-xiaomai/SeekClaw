using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class StreamRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-stream-tests", Guid.NewGuid().ToString("N"));

    public StreamRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", true)]
    public async Task Clients_ClassifyPrematureResponseAsRetryable_WithoutLosingCause(string kind, bool vision)
    {
        var prefix = vision ? "{\"partial\":" : kind == "openai"
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"
            : "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n";
        using var http = new FaultingHttpFactory(prefix, vision);
        ILlmClient client = kind == "openai" ? new OpenAiCompatibleClient(http) : new AnthropicClient(http);
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "test", Kind = kind, BaseUrl = "https://test.invalid" },
            Model = new ModelConfig { Id = "model" },
            Messages = [ChatMessage.User("test", vision ? [new("image", "test.png", "image/png", "AQID", 3)] : null)],
        };
        var observed = new List<LlmStreamEvent>();

        var error = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var evt in client.StreamAsync(request, CancellationToken.None)) observed.Add(evt);
        });

        Assert.True(error.Retryable);
        Assert.Contains("test", error.Message);
        Assert.Equal(HttpRequestError.ResponseEnded, Assert.IsType<HttpIOException>(error.InnerException!.GetBaseException()).HttpRequestError);
        Assert.DoesNotContain(observed, evt => evt is LlmCompleted);
        if (!vision) Assert.Contains(observed, evt => evt is LlmTextDelta { Text: "partial" });
    }

    [Fact]
    public void FollowUpHistory_IdentifiesInterruptedResponses_WithoutReplayingIncompleteThinking()
    {
        var failed = new ChatMessage
        {
            Role = ChatRole.Assistant, Text = "partial", Thinking = "unfinished thought", ThinkingSignature = "unfinished signature",
            FinishReason = "error", ErrorMessage = "ResponseEnded",
        };
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "deepseek", BaseUrl = "https://test.invalid" },
            Model = new ModelConfig { Id = "deepseek-model" },
            Messages = [ChatMessage.User("test"), failed, ChatMessage.User("continue")],
            EnableThinking = true,
        };
        var openAiMessage = OpenAiCompatibleClient.BuildBody(request)["messages"]![1]!;
        Assert.Contains("partial", openAiMessage["content"]!.GetValue<string>());
        Assert.Contains("failed before completion", openAiMessage["content"]!.GetValue<string>());
        Assert.Null(openAiMessage["reasoning_content"]);
        var anthropicContent = AnthropicClient.BuildBody(request)["messages"]![1]!["content"]!.AsArray();
        var block = Assert.Single(anthropicContent)!;
        Assert.Equal("text", block["type"]!.GetValue<string>());
        Assert.Contains("failed before completion", block["text"]!.GetValue<string>());
        Assert.Equal("partial", failed.Text); // diagnostics affect API history, not the stored answer
    }

    [Fact]
    public async Task CompletedOpenAiFrame_DoesNotFailWhenTrailingHttpBodyIsCutOff()
    {
        const string frame = "data: {\"choices\":[{\"delta\":{\"content\":\"complete\"},\"finish_reason\":\"stop\"}]}\n\n";
        using var http = new FaultingHttpFactory(frame, false);
        var client = new OpenAiCompatibleClient(http);
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "test", BaseUrl = "https://test.invalid" },
            Model = new ModelConfig { Id = "model" }, Messages = [ChatMessage.User("test")],
        };
        var events = new List<LlmStreamEvent>();
        await foreach (var evt in client.StreamAsync(request, CancellationToken.None)) events.Add(evt);
        Assert.Equal("complete", Assert.Single(events.OfType<LlmCompleted>()).Completion.Text);
        Assert.Single(events.OfType<LlmTextDelta>());
    }

    [Fact]
    public async Task ActualTruncatedHttpResponse_IsClassifiedAndRetriedWithStreamReplacement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync();
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        config.Config.Providers.Clear();
        var provider = new ProviderConfig
        {
            Id = "loopback", BaseUrl = $"http://127.0.0.1:{port}",
            Models = [new ModelConfig { Id = "model" }],
        };
        config.Config.Providers.Add(provider);
        config.Config.Provider = provider.Id;
        config.Config.Model = "model";
        config.Config.Routing.FailoverEnabled = false;
        config.Config.Routing.Retry.MaxAttempts = 2;
        config.Config.Routing.Retry.BaseDelaySeconds = 0;
        using var http = new LlmHttpFactory();
        var bus = new EventBus();
        var usage = new UsageTracker(bus, Path.Combine(_root, "usage.jsonl"));
        var manager = new ProviderManager(config, new ModelRegistry(config),
            new LlmClientFactory([new OpenAiCompatibleClient(http)]), http, usage, bus);
        var events = new List<LlmStreamEvent>();
        await foreach (var evt in manager.StreamAsync(model => new LlmRequest
                       {
                           Provider = model.Provider, Model = model.Model, Messages = [ChatMessage.User("test")],
                       }, null, timeout.Token)) events.Add(evt);
        await server;

        Assert.Equal("complete", Assert.Single(events.OfType<LlmCompleted>()).Completion.Text);
        Assert.Single(events.OfType<LlmStreamReset>());
        Assert.Equal(["partial", "complete"], events.OfType<LlmTextDelta>().Select(evt => evt.Text));
        var calls = File.ReadAllLines(Path.Combine(_root, "usage.jsonl")).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.False(calls[0]["success"]!.GetValue<bool>());
        Assert.True(calls[1]["success"]!.GetValue<bool>());

        async Task ServeAsync()
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                // Drain the request so closing a deliberately short response produces
                // ResponseEnded rather than a TCP reset caused by unread request data.
                var headers = new StringBuilder();
                var single = new byte[1];
                while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(single, timeout.Token);
                    headers.Append((char)single[0]);
                    if (headers.Length > 64_000) throw new InvalidDataException("Test request headers too large.");
                }
                var lengthHeader = headers.ToString().Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = int.Parse(lengthHeader.Split(':')[1]);
                await stream.ReadExactlyAsync(new byte[length], timeout.Token);
                var body = attempt == 0
                    ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"
                    : "data: {\"choices\":[{\"delta\":{\"content\":\"complete\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                var bytes = Encoding.UTF8.GetBytes(body);
                var declaredLength = bytes.Length + (attempt == 0 ? 100 : 0);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {declaredLength}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
                await stream.WriteAsync(bytes, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task Retry_ReplacesOnlyCurrentGeneration_AndNeverReexecutesCompletedTools()
    {
        var client = new ScriptedClient(
            new([new LlmCompleted(new LlmCompletion { ToolCalls = [new("first", "count_once", "{}")] })]),
            new([new LlmThinkingDelta("old thinking"), new LlmTextDelta("old text"), new LlmToolCallStarted("partial", "count_once")], Interrupted()),
            new([new LlmTextDelta("done"), new LlmCompleted(new LlmCompletion { Text = "done", FinishReason = "stop" })]));
        await using var runtime = Runtime(client);
        var tool = new CountingTool();
        runtime.Tools.Register(tool);
        using var subscription = runtime.Events.Subscribe();
        var session = runtime.Sessions.Create(runtime.Workspace);

        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "run the counter", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("done", result.Text);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(3, client.Disposals);
        Assert.All(client.Requests.Skip(1), request => Assert.Single(request.Messages, message => message.Role == ChatRole.Tool));
        var saved = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!;
        Assert.Single(saved.Messages, message => message.Role == ChatRole.Tool);
        Assert.DoesNotContain(saved.Messages, message => message.Text == "old text" || message.Thinking == "old thinking");
        Assert.Equal("done", saved.Messages[^1].Text);
        Assert.Equal("stop", saved.Messages[^1].FinishReason);
        var events = Drain(subscription);
        var reset = Assert.Single(events.OfType<AssistantStreamResetEvent>());
        Assert.Equal(8, reset.TextLength);
        Assert.Equal(12, reset.ThinkingLength);
        Assert.Single(events.OfType<ProviderRetryEvent>());
        Assert.Single(events.OfType<ToolCallStartedEvent>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedRetries_PersistFailureAndLastPartialOutput_AndAllowFollowUp(bool partial)
    {
        LlmStreamEvent[] output = partial
            ? [new LlmThinkingDelta("partial thought"), new LlmTextDelta("partial answer"), new LlmToolCallStarted("unsafe", "count_once")]
            : [];
        var client = new ScriptedClient(new(output, Interrupted()), new(output, Interrupted()),
            new([new LlmCompleted(new LlmCompletion { Text = "recovered", FinishReason = "stop" })]));
        await using var runtime = Runtime(client);
        var tool = new CountingTool();
        runtime.Tools.Register(tool);
        using var subscription = runtime.Events.Subscribe();
        var session = runtime.Sessions.Create(runtime.Workspace);

        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "test", CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.False(result.Cancelled);
        Assert.Equal(partial ? "partial answer" : "", result.Text);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(2, client.Requests.Count);
        var reloaded = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!;
        var failed = Assert.Single(reloaded.Messages, message => message.Role == ChatRole.Assistant);
        Assert.Equal("error", failed.FinishReason);
        Assert.Equal(result.Error, failed.ErrorMessage);
        Assert.Equal(partial ? "partial answer" : "", failed.Text);
        Assert.Equal(partial ? "partial thought" : null, failed.Thinking);
        Assert.Null(failed.ToolCalls);
        Assert.NotNull(Assert.Single(Drain(subscription).OfType<TurnCompletedEvent>()).Error);

        var next = await runtime.Agent.RunTurnAsync(reloaded, runtime.Workspace, "continue", CancellationToken.None);
        Assert.Null(next.Error);
        Assert.Equal("recovered", next.Text);
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task CancelDuringRetryBackoff_PreservesCurrentThinking_WithoutStartingAnotherRequest()
    {
        var client = new ScriptedClient(new Attempt([new LlmThinkingDelta("unfinished")], Interrupted()));
        await using var runtime = Runtime(client, retryDelay: 30);
        using var subscription = runtime.Events.Subscribe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = runtime.Sessions.Create(runtime.Workspace);
        var task = runtime.Agent.RunTurnAsync(session, runtime.Workspace, "test", cts.Token);
        while (await subscription.Reader.ReadAsync(cts.Token) is not ProviderRetryEvent) { }
        cts.Cancel();

        var result = await task;

        Assert.True(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Single(client.Requests);
        var message = runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!.Messages[^1];
        Assert.Equal("cancelled", message.FinishReason);
        Assert.Equal("unfinished", message.Thinking);
        Assert.Null(message.ErrorMessage);
    }

    [Fact]
    public async Task CompletedToolCalls_AreNeverRetriedEvenIfClientWouldThrowAfterCompletion()
    {
        var client = new ScriptedClient(
            new Attempt([new LlmCompleted(new LlmCompletion { Text = "done", FinishReason = "stop" })], Interrupted()));
        await using var runtime = Runtime(client);
        var session = runtime.Sessions.Create(runtime.Workspace);

        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "test", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Single(client.Requests);
        Assert.Equal(1, client.Disposals);
    }

    [Fact]
    public async Task NonRetryableFailure_AfterPartialOutput_IsRecordedWithoutRetry()
    {
        var client = new ScriptedClient(new Attempt([new LlmTextDelta("partial")], new LlmException("rejected", retryable: false)));
        await using var runtime = Runtime(client);
        var session = runtime.Sessions.Create(runtime.Workspace);
        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "test", CancellationToken.None);
        Assert.Equal("rejected", result.Error);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task MissingCompletion_IsNeverReportedAsSuccess()
    {
        var client = new ScriptedClient(new Attempt([new LlmTextDelta("incomplete")]));
        await using var runtime = Runtime(client);
        var session = runtime.Sessions.Create(runtime.Workspace);
        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "test", CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal("error", runtime.Sessions.Load(runtime.Workspace, session.Header.Id)!.Messages[^1].FinishReason);
    }

    private SeekClawRuntime Runtime(ScriptedClient client, int retryDelay = 0)
    {
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        config.Config.Providers.Clear();
        config.Config.Providers.Add(new ProviderConfig
        {
            Id = "test", Kind = "openai", BaseUrl = "https://test.invalid",
            Models = [new ModelConfig { Id = "model", ContextWindow = 64_000, MaxOutput = 1024 }],
        });
        config.Config.Provider = "test";
        config.Config.Model = "model";
        config.Config.Routing.Fallback = [];
        config.Config.Routing.FailoverEnabled = false;
        config.Config.Routing.Retry.MaxAttempts = 2;
        config.Config.Routing.Retry.BaseDelaySeconds = retryDelay;
        config.Config.Routing.Retry.MaxDelaySeconds = retryDelay;
        config.Config.Agent.EnableContextCompaction = false;
        config.Config.Agent.EnableCrossSessionMemory = false;
        config.Config.Agent.AutoVerify = false;
        var workspace = new WorkspaceManager().CreateGlobal(Path.Combine(_root, "global"));
        return SeekClawRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_root, "test.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton<ILlmClientFactory>(new LlmClientFactory([client]));
            services.AddSingleton<IUsageTracker>(sp => new UsageTracker(
                sp.GetRequiredService<IEventBus>(), Path.Combine(_root, "usage.jsonl")));
            services.AddSingleton(new CircuitBreaker(config.Config.Routing.Retry));
        });
    }

    private static LlmException Interrupted() => new("The response ended prematurely. (ResponseEnded)", inner:
        new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

    private static List<RuntimeEvent> Drain(IEventSubscription subscription)
    {
        var events = new List<RuntimeEvent>();
        while (subscription.Reader.TryRead(out var evt)) events.Add(evt);
        return events;
    }

    private sealed record Attempt(LlmStreamEvent[] Events, Exception? Error = null);

    private sealed class ScriptedClient(params Attempt[] attempts) : ILlmClient
    {
        public string Kind => "openai";
        public List<LlmRequest> Requests { get; } = [];
        public int Disposals { get; private set; }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var attempt = attempts[Math.Min(Requests.Count, attempts.Length - 1)];
            Requests.Add(request);
            try
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                foreach (var evt in attempt.Events) yield return evt;
                if (attempt.Error is not null) throw attempt.Error;
            }
            finally { Disposals++; }
        }
    }

    private sealed class CountingTool : ITool
    {
        public string Name => "count_once";
        public string Description => "A side effect that must only happen once.";
        public bool Mutating => true;
        public bool RequiresWorkspace => false;
        public string StatusLabel => "Counting";
        public JsonObject ParameterSchema => new() { ["type"] = "object" };
        public int Calls { get; private set; }
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ToolResult.Ok("counted"));
        }
    }

    private sealed class FaultingHttpFactory : ILlmHttpFactory, IDisposable
    {
        private readonly HttpClient _http;
        public FaultingHttpFactory(string prefix, bool vision) => _http = new HttpClient(new FaultingHandler(prefix, vision));
        public HttpClient GetClient(ProviderConfig provider) => _http;
        public void Dispose() => _http.Dispose();
    }

    private sealed class FaultingHandler(string prefix, bool vision) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new FaultingStream(Encoding.UTF8.GetBytes(prefix)));
            content.Headers.ContentType = new MediaTypeHeaderValue(vision ? "application/json" : "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FaultingStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position == Length) throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            return base.ReadAsync(buffer, cancellationToken);
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            CopyAndFailAsync(destination, cancellationToken);
        private async Task CopyAndFailAsync(Stream destination, CancellationToken ct)
        {
            await destination.WriteAsync(ToArray(), ct);
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        }
    }
}
