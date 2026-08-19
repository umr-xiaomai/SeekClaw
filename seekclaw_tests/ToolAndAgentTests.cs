using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class ToolAndAgentTests
{
    [Fact]
    public void AgentSteeringQueue_DrainsMessagesInOrder()
    {
        var queue = new AgentSteeringQueue();
        Assert.True(queue.TryEnqueue(ChatMessage.User("first")));
        Assert.True(queue.TryEnqueue(ChatMessage.User("second")));

        Assert.Equal(["first", "second"], queue.Drain().Select(message => message.Text));
        Assert.Empty(queue.Drain());
        Assert.True(queue.TryCompleteIfEmpty());
        Assert.False(queue.TryEnqueue(ChatMessage.User("late")));
    }

    [Fact]
    public void EditTool_CountsAndReplacesFirstOccurrence()
    {
        Assert.Equal(2, EditFileTool.CountOccurrences("aXbXc", "X"));
        Assert.Equal(0, EditFileTool.CountOccurrences("abc", "X"));
        Assert.Equal("aYbXc", EditFileTool.ReplaceFirst("aXbXc", "X", "Y"));
    }

    [Fact]
    public void DiffUtil_ProducesUnifiedHunks()
    {
        var diff = DiffUtil.Unified("line1\nline2\nline3\n", "line1\nCHANGED\nline3\n", "test.txt");
        Assert.Contains("--- a/test.txt", diff);
        Assert.Contains("+++ b/test.txt", diff);
        Assert.Contains("-line2", diff);
        Assert.Contains("+CHANGED", diff);
        Assert.Equal("", DiffUtil.Unified("same\n", "same\n", "x"));
    }

    [Fact]
    public void WithoutImages_StripsImagePayloadsButKeepsText()
    {
        var image = new ChatImageAttachment("id", "p.png", "image/png", "AAAA", 4);
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("look at this", [image]),
            ChatMessage.Assistant("I see it"),
            ChatMessage.User("follow up"),
        };

        var stripped = Agent.WithoutImages(messages);
        Assert.All(stripped, message => Assert.Null(message.Images));
        Assert.Equal("look at this", stripped[0].Text);
        Assert.Equal("I see it", stripped[1].Text);
        Assert.Equal("follow up", stripped[2].Text);
    }

    [Fact]
    public void WithoutImages_ReturnsSameListWhenNothingHasImages()
    {
        var plain = new List<ChatMessage> { ChatMessage.User("text only") };
        Assert.Same(plain, Agent.WithoutImages(plain));
    }

    [Fact]
    public void ContextPlanner_KeepsHistoryWithinBudget()
    {
        var model = new ModelConfig { ContextWindow = 4000, MaxOutput = 1000 };
        var big = new string('x', 6000);
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(ChatMessage.User($"question {i} {big}"));
            messages.Add(ChatMessage.Assistant($"answer {i} {big}"));
        }

        var fitted = ContextPlanner.FitToWindow(messages, model, "system prompt");

        // Oldest messages drop, but the most recent 6 are always kept (plus the trim notice).
        Assert.True(fitted.Count < messages.Count);
        Assert.True(fitted.Count <= 7);
        // Newest messages survive.
        Assert.Contains(fitted, m => m.Text.StartsWith("answer 9"));
        // A trim notice is inserted at the head.
        Assert.StartsWith("[Earlier conversation history was trimmed", fitted[0].Text);
    }

    [Fact]
    public void ContextPlanner_LeavesShortHistoryUntouched()
    {
        var model = new ModelConfig { ContextWindow = 128_000, MaxOutput = 8_000 };
        var messages = new List<ChatMessage> { ChatMessage.User("hi"), ChatMessage.Assistant("hello") };
        Assert.Same(messages, ContextPlanner.FitToWindow(messages, model, "sys"));
    }

    [Fact]
    public void ContextPlanner_RepairsMissingToolResultsFromInterruptedTurns()
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
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("inspect"),
            assistant,
            ChatMessage.ToolResult("c1", "read_file", "one", true),
            ChatMessage.User("continue"),
        };

        var repaired = ContextPlanner.FitToWindow(
            messages,
            new ModelConfig { ContextWindow = 128_000, MaxOutput = 8_000 },
            "sys");

        Assert.Equal(5, repaired.Count);
        Assert.Equal("c1", repaired[2].ToolCallId);
        Assert.Equal("c2", repaired[3].ToolCallId);
        Assert.False(repaired[3].ToolSuccess);
        Assert.Contains("did not complete", repaired[3].Text);
        Assert.Equal("continue", repaired[4].Text);
    }

    [Fact]
    public void ContextPlanner_ShrinksOldToolOutputsFirst()
    {
        var model = new ModelConfig { ContextWindow = 12_000, MaxOutput = 2_000 };
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("start"),
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new ToolCallRequest("c1", "read_file", "{}")],
            },
            ChatMessage.ToolResult("c1", "read_file", new string('t', 60_000), true),
        };
        for (var i = 0; i < 8; i++) messages.Add(ChatMessage.User($"follow-up {i}"));

        var fitted = ContextPlanner.FitToWindow(messages, model, "sys");
        var tool = fitted.Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("trimmed to fit", tool.Text);
        Assert.True(tool.Text.Length < 1_000);
    }

    [Fact]
    public void ToolOutputBudget_ScalesWithContextWindow_AndRespectsCap()
    {
        var agent = new AgentConfig { MaxToolOutputChars = 60_000 };
        var small = ContextPlanner.ToolOutputBudget(new ModelConfig { ContextWindow = 8_000 }, agent);
        var large = ContextPlanner.ToolOutputBudget(new ModelConfig { ContextWindow = 1_000_000 }, agent);
        Assert.True(small < large);
        Assert.Equal(60_000, large); // capped by config
        Assert.True(small >= 4_000); // floor
    }

    [Fact]
    public void ToolSchema_BuildsValidJsonSchema()
    {
        var schema = ToolSchema.Object(
            ("path", ToolSchema.String("the path"), true),
            ("limit", ToolSchema.Integer("max lines"), false));

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["path"]!["type"]!.GetValue<string>());
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["path"], required);
    }

    [Fact]
    public void FileWalker_IgnoreMatcher_FiltersFilesAndDirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seekclaw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "*.log\ntemp/\n");
            File.WriteAllText(Path.Combine(tempDir, "test.log"), "log content");
            File.WriteAllText(Path.Combine(tempDir, "main.cs"), "code content");

            var tempSub = Path.Combine(tempDir, "temp");
            Directory.CreateDirectory(tempSub);
            File.WriteAllText(Path.Combine(tempSub, "ignored.txt"), "ignored");

            var matcher = FileWalker.IgnoreMatcher.ForRoot(tempDir);
            Assert.True(matcher.IsIgnored(Path.Combine(tempDir, "test.log"), isDir: false));
            Assert.False(matcher.IsIgnored(Path.Combine(tempDir, "main.cs"), isDir: false));
            Assert.True(matcher.IsIgnored(tempSub, isDir: true));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IgnoreMatcher_SupportsGitignoreNegationAnchoringAndDoubleStar()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seekclaw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), """
                *.log
                !keep.log
                temp/
                /root-only.txt
                **/generated/
                """);
            var matcher = FileWalker.IgnoreMatcher.ForRoot(tempDir);

            // Non-anchored patterns match the basename at any depth.
            Assert.True(matcher.IsIgnored(Path.Combine(tempDir, "a", "b", "debug.log"), isDir: false));
            // Negation re-includes a previously ignored file (last matching rule wins).
            Assert.False(matcher.IsIgnored(Path.Combine(tempDir, "keep.log"), isDir: false));
            Assert.True(matcher.IsIgnored(Path.Combine(tempDir, "other.log"), isDir: false));
            // A leading slash anchors the pattern to the ignore-file root.
            Assert.True(matcher.IsIgnored(Path.Combine(tempDir, "root-only.txt"), isDir: false));
            Assert.False(matcher.IsIgnored(Path.Combine(tempDir, "sub", "root-only.txt"), isDir: false));
            // "**/generated/" matches generated directories at any depth.
            Assert.True(matcher.IsIgnored(Path.Combine(tempDir, "x", "y", "generated"), isDir: true));
            // A trailing slash restricts the pattern to directories.
            Assert.False(matcher.IsIgnored(Path.Combine(tempDir, "temp.txt"), isDir: false));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void McpToolAdapter_BuildName_SanitizesToProviderSafeCharacters()
    {
        Assert.Equal("mcp__My_Server__read_file", McpToolAdapter.BuildName("My Server", "read file"));

        var name = McpToolAdapter.BuildName("带空格的服务器!@#", "工具:名称");
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", name);
        Assert.StartsWith("mcp__", name);

        // Empty segments fall back to a placeholder instead of producing "mcp______".
        Assert.Equal("mcp__tool__tool", McpToolAdapter.BuildName("", ""));
    }

    [Fact]
    public void WebTools_MarkNetworkDependencyTogether()
    {
        // The "联网" toggle must control web_search and web_fetch as one group:
        // both available in global tasks, both hidden when the toggle is off.
        var prompts = new FilePromptProvider();
        var search = new WebSearchTool(prompts);
        var fetch = new WebFetchTool(prompts);

        // Assert through the ITool interface — that is how Agent.ActiveTools
        // inspects tools. A bare class property would NOT override the interface
        // default member and would keep reporting RequiresWorkspace=true, which
        // would wrongly filter web tools out of global tasks.
        ITool searchTool = search;
        ITool fetchTool = fetch;
        Assert.False(searchTool.RequiresWorkspace);
        Assert.False(fetchTool.RequiresWorkspace);
        Assert.True(searchTool.RequiresNetwork);
        Assert.True(fetchTool.RequiresNetwork);
    }

    [Fact]
    public async Task GlobalTurn_NetworkToggle_ControlsWebToolsSentToProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seekclaw-agent-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            // First-run defaults are seeded by code now; keep this turn isolated to its own provider.
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test.local/v1",
                Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000 }],
            });
            var capture = new CapturingClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));

            await using var runtime = SeekClawRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new SeekClawDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            // Global task with the 联网 toggle ON: both web tools reach the provider.
            var online = runtime.Sessions.Create(globalWorkspace, networkEnabled: true);
            await runtime.Agent.RunTurnAsync(online, globalWorkspace, "请搜索花濑HoiLai", CancellationToken.None);
            var onlineNames = capture.LastRequest!.Tools.Select(tool => tool.Name).ToList();
            Assert.Contains("web_search", onlineNames);
            Assert.Contains("web_fetch", onlineNames);
            Assert.Contains("web_fetch", onlineNames);
            Assert.Contains("read_file", onlineNames);
            Assert.Contains("write_file", onlineNames);

            // Global task with the toggle OFF: neither web tool is sent.
            capture.Reset();
            var offline = runtime.Sessions.Create(globalWorkspace, networkEnabled: false);
            await runtime.Agent.RunTurnAsync(offline, globalWorkspace, "请搜索花濑HoiLai", CancellationToken.None);
            var offlineNames = capture.LastRequest!.Tools.Select(tool => tool.Name).ToList();
            Assert.DoesNotContain("web_search", offlineNames);
            Assert.DoesNotContain("web_fetch", offlineNames);
            Assert.Contains("read_file", offlineNames);
            Assert.Contains("write_file", offlineNames);
        }
        finally
        {
            // The SQLite connection pool keeps state.db open even after the runtime
            // is disposed; release pooled handles before cleaning up the temp dir.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class CapturingClientFactory : ILlmClientFactory
    {
        public LlmRequest? LastRequest { get; private set; }

        public ILlmClient GetClient(string kind) => new CapturingClient(this);

        public void Reset() => LastRequest = null;

        private sealed class CapturingClient(CapturingClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.LastRequest = request;
                await Task.Yield();
                yield return new LlmCompleted(new LlmCompletion { Text = "done" });
            }
        }
    }

    [Fact]
    public async Task WebFetchTool_ExtractsTextAndStripsHtml()
    {
        var prompts = new FilePromptProvider();
        var tool = new WebFetchTool(prompts);

        var html = "<html><head><style>body{color:red;}</style></head><body><h1>Title</h1><p>Hello World</p></body></html>";
        var method = typeof(WebFetchTool).GetMethod("ExtractMainText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var extracted = (string)method!.Invoke(null, [html])!;

        Assert.DoesNotContain("color:red", extracted);
        Assert.Contains("Title", extracted);
        Assert.Contains("Hello World", extracted);
    }

    [Fact]
    public void WebSearchTool_ExtractsBingResults()
    {
        const string html = """
            <html><body>
            <li class="b_algo">
              <h2><a href="https://example.com/a">Example <strong>A</strong></a></h2>
              <p>A useful snippet from Bing.</p>
            </li>
            </body></html>
            """;

        var results = WebSearchTool.ExtractBingResults(html, 5);

        var result = Assert.Single(results);
        Assert.Equal("Example A", result.Title);
        Assert.Equal("https://example.com/a", result.Url);
        Assert.Equal("A useful snippet from Bing.", result.Snippet);
    }

    [Fact]
    public void WebSearchTool_ExtractsGoogleResultsAndUnwrapsUrls()
    {
        const string html = """
            <html><body>
            <a href="/url?q=https%3A%2F%2Fexample.com%2Fg&amp;sa=U"><h3>Google Result</h3></a>
            <div class="VwiC3b">A useful snippet from Google.</div>
            </body></html>
            """;

        var results = WebSearchTool.ExtractGoogleResults(html, 5);

        var result = Assert.Single(results);
        Assert.Equal("Google Result", result.Title);
        Assert.Equal("https://example.com/g", result.Url);
        Assert.Equal("A useful snippet from Google.", result.Snippet);
    }

    [Fact]
    public void WebSearchTool_ExtractsBaiduResults()
    {
        const string html = """
            <html><body>
            <div class="result c-container">
              <h3 class="t"><a href="https://www.baidu.com/link?url=abc">Baidu <em>Result</em></a></h3>
              <div class="c-abstract">A useful snippet from Baidu.</div>
            </div>
            </body></html>
            """;

        var results = WebSearchTool.ExtractBaiduResults(html, 5);

        var result = Assert.Single(results);
        Assert.Equal("Baidu Result", result.Title);
        Assert.Equal("https://www.baidu.com/link?url=abc", result.Url);
        Assert.Contains("A useful snippet from Baidu.", result.Snippet);
    }

    [Fact]
    public void CaptureScreenTool_HasVisionRequirementAndValidSchema()
    {
        var prompts = new FilePromptProvider();
        var tool = new CaptureScreenTool(prompts);

        Assert.Equal("capture_screen", tool.Name);
        Assert.True(tool.RequiresVision);
        Assert.False(tool.RequiresWorkspace);
        Assert.False(tool.Mutating);
        Assert.NotNull(tool.ParameterSchema);
    }

    [Fact]
    public async Task CaptureScreenTool_ExecutesSuccessfullyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var prompts = new FilePromptProvider();
        var tool = new CaptureScreenTool(prompts);
        using var runtime = SeekClawRuntime.Create();

        var context = new ToolContext
        {
            Workspace = runtime.Workspace,
            Events = runtime.Events,
            Agent = runtime.ConfigStore.Config.Agent,
        };

        var result = await tool.ExecuteAsync(new System.Text.Json.Nodes.JsonObject(), context, CancellationToken.None);
        Assert.True(result.Success, result.Output);
        Assert.NotNull(result.Images);
        Assert.NotEmpty(result.Images);
        Assert.Equal("image/png", result.Images[0].MediaType);
        Assert.True(result.Images[0].SizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.Images[0].Data));
    }

    [Fact]
    public void ToolRegistry_ResolvesCaptureScreenTool()
    {
        using var runtime = SeekClawRuntime.Create();
        var tool = runtime.Tools.Resolve("capture_screen");
        Assert.NotNull(tool);
        Assert.True(tool.RequiresVision);
    }
}
