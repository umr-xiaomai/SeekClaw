using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Cli.Commands;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Cli.Tests;

public sealed class ExecCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-exec-tests", Guid.NewGuid().ToString("N"));
    public ExecCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void JsonEvents_DescribeStreamReplacementAndRetryForConsumers()
    {
        var reset = ExecCommand.ProjectEvent(new AssistantStreamResetEvent("test/model", 7, 12))!;
        Assert.Equal("message.reset", reset["type"]!.GetValue<string>());
        Assert.Equal(7, reset["textLength"]!.GetValue<int>());
        Assert.Equal(12, reset["thinkingLength"]!.GetValue<int>());
        var retry = ExecCommand.ProjectEvent(new ProviderRetryEvent("test/model", 1, "ResponseEnded", TimeSpan.FromSeconds(2)))!;
        Assert.Equal("provider.retry", retry["type"]!.GetValue<string>());
        Assert.Equal(2000, retry["delayMs"]!.GetValue<double>());
    }

    [Fact]
    public async Task JsonMode_ProducesOnlyVersionedJsonLines_AndWritesValidatedOutput()
    {
        var factory = new ClientFactory("{\"answer\":42}");
        await using var runtime = Runtime(factory);
        var schema = Path.Combine(_root, "schema.json");
        File.WriteAllText(schema, """{"type":"object","required":["answer"],"properties":{"answer":{"type":"integer"}},"additionalProperties":false}""");
        var path = Path.Combine(_root, "answer.json");
        var stdout = new StringWriter(); var stderr = new StringWriter();
        var code = await ExecCommand.RunAsync(runtime, new ExecOptions("answer", Json: true, OutputFile: path, SchemaFile: schema),
            new StringReader("piped input"), stdout, stderr, CancellationToken.None);
        Assert.Equal(0, code);
        var events = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!.AsObject()).ToList();
        Assert.All(events, item => Assert.Equal(1, item["version"]!.GetValue<int>()));
        Assert.Equal("thread.started", events[0]["type"]!.GetValue<string>());
        Assert.Equal("turn.completed", events[^1]["type"]!.GetValue<string>());
        Assert.Equal(42, JsonNode.Parse(File.ReadAllText(path))!["answer"]!.GetValue<int>());
        Assert.Contains(factory.Requests.SelectMany(x => x.Messages), message => message.Text.Contains("piped input", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidStructuredOutput_ReturnsThree_AndPreservesExistingOutputFile()
    {
        await using var runtime = Runtime(new ClientFactory("not JSON"));
        var schema = Path.Combine(_root, "schema.json"); File.WriteAllText(schema, """{"type":"object"}""");
        var path = Path.Combine(_root, "answer.json"); File.WriteAllText(path, "preserve");
        var stdout = new StringWriter();
        var code = await ExecCommand.RunAsync(runtime, new ExecOptions("answer", Json: true, SchemaFile: schema, OutputFile: path),
            null, stdout, new StringWriter(), CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Equal("preserve", File.ReadAllText(path));
        var final = JsonNode.Parse(stdout.ToString().Trim().Split('\n')[^1])!;
        Assert.Equal("turn.failed", final["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(final["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Resume_AppendsToTheSameConversation_AndKeepsPriorContext()
    {
        var factory = new ClientFactory("complete");
        await using var runtime = Runtime(factory);
        var errors = new StringWriter();
        var first = await ExecCommand.RunAsync(runtime, new ExecOptions("original context"), null, new StringWriter(), errors, CancellationToken.None);
        var session = Assert.Single(runtime.Sessions.List(runtime.Workspace));
        var second = await ExecCommand.RunAsync(runtime, new ExecOptions("follow up", Resume: session.Id), null, new StringWriter(), errors, CancellationToken.None);
        Assert.True(first == 0 && second == 0, errors.ToString());
        Assert.Single(runtime.Sessions.List(runtime.Workspace));
        Assert.Contains(factory.Requests[^1].Messages, x => x.Text == "original context");
        Assert.Contains(factory.Requests[^1].Messages, x => x.Text == "follow up");
    }

    [Theory]
    [InlineData("", false, 1800)]
    [InlineData("prompt", true, 1800)]
    [InlineData("prompt", false, 0)]
    public async Task InvalidArguments_ReturnTwoWithoutInvokingTheModel(string prompt, bool conflicting, int timeout)
    {
        var factory = new ClientFactory("unused");
        await using var runtime = Runtime(factory);
        var code = await ExecCommand.RunAsync(runtime, new ExecOptions(prompt, Resume: conflicting ? "id" : null, Continue: conflicting, TimeoutSeconds: timeout),
            null, new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.Equal(2, code); Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task GuardrailMode_DeniesMutationsWithoutBlockingOnInput_AndFailsTheRun()
    {
        var factory = new ClientFactory("done", toolFirst: true);
        await using var runtime = Runtime(factory);
        var code = await ExecCommand.RunAsync(runtime, new ExecOptions("create file"), null, new StringWriter(), new StringWriter(), CancellationToken.None);
        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(_root, "should-not-exist.txt")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"type\":\"invalid-type\"}")]
    public async Task InvalidSchema_IsAnArgumentErrorBeforeModelExecution(string content)
    {
        var factory = new ClientFactory("unused");
        await using var runtime = Runtime(factory);
        var path = Path.Combine(_root, "schema.json"); File.WriteAllText(path, content);
        var output = new StringWriter();
        var code = await ExecCommand.RunAsync(runtime, new ExecOptions("run", Json: true, SchemaFile: path), null, output, new StringWriter(), CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Empty(factory.Requests);
        Assert.Equal(2, JsonNode.Parse(output.ToString().Trim())!["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public async Task Cancellation_AlwaysEmitsATerminalJsonEvent()
    {
        await using var runtime = Runtime(new ClientFactory("unused"));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var output = new StringWriter();
        var code = await ExecCommand.RunAsync(runtime, new ExecOptions("run", Json: true), null, output, new StringWriter(), cancel.Token);
        Assert.Equal(130, code);
        var last = JsonNode.Parse(output.ToString().Trim().Split('\n')[^1])!;
        Assert.Equal("turn.failed", last["type"]!.GetValue<string>());
        Assert.Equal(130, last["exitCode"]!.GetValue<int>());
    }

    private SeekClawRuntime Runtime(ClientFactory factory)
    {
        var config = new ConfigStore(Path.Combine(_root, "config.json"), Path.Combine(_root, "state.json"));
        config.Config.Providers = [new ProviderConfig { Id = "test", Kind = "openai", BaseUrl = "https://example.invalid/v1", Models = [new ModelConfig { Id = "model", ContextWindow = 16000, MaxOutput = 256 }] }];
        config.Config.Provider = "test"; config.Config.Model = "model";
        config.Config.Routing.Fallback = ["test/model"];
        config.Config.Agent.AutoVerify = false;
        config.Config.Agent.EnableCrossSessionMemory = false;
        return SeekClawRuntime.CreateIsolated(new WorkspaceInfo { Root = _root, ProjectKinds = [] }, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_root, "data.db")));
            services.AddSingleton<ILlmClientFactory>(factory);
            services.AddSingleton<IUsageTracker>(provider => new UsageTracker(provider.GetRequiredService<SeekClaw.Runtime.Events.IEventBus>(), Path.Combine(_root, "usage.jsonl")));
        });
    }

    private sealed class ClientFactory(string text, bool toolFirst = false) : ILlmClientFactory, ILlmClient
    {
        public string Kind => "openai";
        public List<LlmRequest> Requests { get; } = [];
        public ILlmClient GetClient(string kind) => this;
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new LlmCompleted(toolFirst && Requests.Count == 1
                ? new LlmCompletion { ToolCalls = [new ToolCallRequest("call", "write_file", """{"path":"should-not-exist.txt","content":"blocked"}""")] }
                : new LlmCompletion { Text = text, FinishReason = "stop" });
        }
    }
}
