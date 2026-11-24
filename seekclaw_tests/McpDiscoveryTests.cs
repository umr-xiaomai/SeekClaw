using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Daemon;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class McpDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "seekclaw-mcp-discovery", Guid.NewGuid().ToString("N"));

    public McpDiscoveryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    [Fact]
    public async Task PrepareAndServerListing_DoNotStartProcessesOrLoadSchemas()
    {
        var config = Config("files", "calendar");
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        var prompts = new PromptRegistry();
        await using var manager = new McpManager(config, registry, prompts, connections: pool);
        await manager.PrepareAsync(Workspace(), CancellationToken.None);
        manager.BeginTurn("first");

        var response = await Search(registry, Context("first"));

        Assert.True(response.Success);
        Assert.Equal(2, JsonNode.Parse(response.Output)!["servers"]!.AsArray().Count);
        Assert.Empty(factory.Transports);
        Assert.Empty(registry.All.OfType<McpToolAdapter>());
        var catalog = Assert.Single(prompts.All, x => x.Id == "mcp:catalog");
        var text = await catalog.Resolver(new PromptRenderContext { Variables = new Dictionary<string, string> { ["tool"] = "tool_search" } }, CancellationToken.None);
        Assert.Contains("files", text);
        Assert.DoesNotContain("inputSchema", text);
    }

    [Fact]
    public async Task Search_LoadsOnlySelectedSchemas_WithoutCallingTools_AndResetPreventsGuessedCalls()
    {
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(Config("files", "calendar"), registry, new PromptRegistry(), connections: pool);
        await manager.PrepareAsync(Workspace(), CancellationToken.None);
        manager.BeginTurn("first");

        var search = await Search(registry, Context("first"), "files", "read_file", limit: 1);

        Assert.True(search.Success);
        var transport = Assert.Single(factory.Transports);
        Assert.Equal("files", transport.Name);
        Assert.Equal(0, transport.Calls);
        var loaded = Assert.Single(registry.All.OfType<McpToolAdapter>(), x => x.IsLoaded("first"));
        Assert.Contains("read", loaded.Name);
        Assert.False(loaded.IsLoaded("other"));
        var result = await loaded.ExecuteAsync(new(), Context("first"), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(1, transport.Calls);
        Assert.NotNull(result.StructuredContent);

        manager.BeginTurn("first");
        Assert.False(loaded.IsLoaded("first"));
        Assert.False((await loaded.ExecuteAsync(new(), Context("first"), CancellationToken.None)).Success);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task SharedPool_SurvivesTurnManagerDisposal_AndActivationsAreIndependent()
    {
        var factory = new FakeFactory();
        var config = Config("files");
        var pool = new McpConnectionPool(transportFactory: factory.Create);
        var firstRegistry = new ToolRegistry();
        await using (var first = new McpManager(config, firstRegistry, new PromptRegistry(), connections: pool))
        {
            await first.PrepareAsync(Workspace(), CancellationToken.None);
            await Search(firstRegistry, Context("first"), "files", "read");
        }
        var transport = Assert.Single(factory.Transports);
        Assert.False(transport.Disposed);
        var secondRegistry = new ToolRegistry();
        await using (var second = new McpManager(config, secondRegistry, new PromptRegistry(), connections: pool))
        {
            await second.PrepareAsync(Workspace(), CancellationToken.None);
            Assert.All(secondRegistry.All.OfType<McpToolAdapter>(), tool => Assert.False(tool.IsLoaded("second")));
            await Search(secondRegistry, Context("second"), "files", "read");
            Assert.Single(factory.Transports);
            Assert.Equal(1, transport.Initializations);
        }
        await pool.DisposeAsync();
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task ConcurrentDiscovery_SingleFlightsConnection_AndOneCancelledWaiterDoesNotStopSibling()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory { BeforeInitialize = async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); } };
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var firstRegistry = new ToolRegistry();
        var secondRegistry = new ToolRegistry();
        await using var first = new McpManager(Config("files"), firstRegistry, new PromptRegistry(), connections: pool);
        await using var second = new McpManager(Config("files"), secondRegistry, new PromptRegistry(), connections: pool);
        using var cancellation = new CancellationTokenSource();
        var firstSearch = Search(firstRegistry, Context("first"), "files", "read", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondSearch = Search(secondRegistry, Context("second"), "files", "read");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstSearch);
        release.SetResult();
        Assert.True((await secondSearch.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(1, Assert.Single(factory.Transports).Initializations);
    }

    [Fact]
    public async Task ConfigurationChangeAndRemoval_RetireOldClient_WithoutStartingReplacementUntilSearch()
    {
        var factory = new FakeFactory();
        var config = Config("files");
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(config, registry, new PromptRegistry(), connections: pool);
        await Search(registry, Context("first"), "files", "read");
        var original = Assert.Single(factory.Transports);
        config.Config.Mcp.Servers["files"].Args = ["changed"];

        var stale = Assert.IsType<McpToolAdapter>(registry.Resolve("mcp__files__read_file"));
        Assert.False((await stale.ExecuteAsync(new(), Context("first"), CancellationToken.None)).Success);
        Assert.Equal(0, original.Calls);
        Assert.Single(factory.Transports);

        await manager.PrepareAsync(Workspace(), CancellationToken.None);

        Assert.True(original.Disposed);
        Assert.Single(factory.Transports);
        Assert.Empty(registry.All.OfType<McpToolAdapter>());
        await Search(registry, Context("first"), "files", "read");
        Assert.Equal(2, factory.Transports.Count);
        config.Config.Mcp.Servers.Remove("files");
        await manager.PrepareAsync(Workspace(), CancellationToken.None);
        Assert.All(factory.Transports, transport => Assert.True(transport.Disposed));
        Assert.Empty(registry.All.OfType<McpToolAdapter>());
    }

    [Fact]
    public async Task TaskSnapshots_RespectLiveGlobalMcpRevocation_WithoutChangingTaskBehavior()
    {
        var source = Config("files");
        source.Config.Agent.Mode = "readonly";
        var task = new TaskConfigStore(new TaskConfigStore(source));
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(task, registry, new PromptRegistry(), connections: pool);
        await Search(registry, Context("first"), "files", "read_file");
        var old = Assert.IsType<McpToolAdapter>(registry.Resolve("mcp__files__read_file"));

        source.Config.Agent.Mode = "full";
        source.Config.Mcp.Servers["files"].Enabled = false;

        Assert.Equal("readonly", task.Config.Agent.Mode);
        Assert.True(task.Config.Mcp.Servers["files"].Enabled);
        Assert.False((await old.ExecuteAsync(new(), Context("first"), CancellationToken.None)).Success);
        Assert.False((await Search(registry, Context("first"), "files", "read_file")).Success);
        Assert.True(Assert.Single(factory.Transports).Disposed);
        Assert.Single(factory.Transports);

        source.Config.Mcp.Servers.Remove("files");
        await manager.PrepareAsync(Workspace(), CancellationToken.None);
        Assert.Empty(manager.LoadServerConfigs(Workspace()));
        Assert.Empty(registry.All.OfType<McpToolAdapter>());
        Assert.Single(factory.Transports);
    }

    [Fact]
    public async Task PersistedWorkspaceMcp_OverridesStaleTaskInlineConfig_WithoutReplacingOtherWorkspaceSettings()
    {
        var oldInline = new WorkspaceConfig { Mode = "readonly", Mcp = new McpConfig
            { Servers = new Dictionary<string, McpServerConfig> { ["files"] = new() { Command = "fake-old" } } } };
        var workspace = new WorkspaceInfo { Root = _directory, ProjectKinds = [], Config = oldInline,
            ProjectStateDirectory = Path.Combine(_directory, "workspace-state") };
        Directory.CreateDirectory(workspace.SeekClawDir);
        var live = new WorkspaceConfig { Mode = "full", Mcp = new McpConfig
            { Servers = new Dictionary<string, McpServerConfig> { ["files"] = new() { Command = "fake-current" } } } };
        await File.WriteAllTextAsync(workspace.ConfigFile, JsonSerializer.Serialize(live, SeekClawJsonContext.Default.WorkspaceConfig));
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(new TaskConfigStore(Config()), registry, new PromptRegistry(), connections: pool);
        await Search(registry, Context("first", workspace), "files", "read_file");
        var old = Assert.IsType<McpToolAdapter>(registry.Resolve("mcp__files__read_file"));
        Assert.Equal("fake-current", manager.LoadServerConfigs(workspace)["files"].Command);
        Assert.Equal("readonly", workspace.Config.Mode);

        live.Mcp.Servers.Clear();
        await File.WriteAllTextAsync(workspace.ConfigFile, JsonSerializer.Serialize(live, SeekClawJsonContext.Default.WorkspaceConfig));

        Assert.False((await old.ExecuteAsync(new(), Context("first", workspace), CancellationToken.None)).Success);
        await manager.PrepareAsync(workspace, CancellationToken.None);
        Assert.Empty(manager.LoadServerConfigs(workspace));
        Assert.True(Assert.Single(factory.Transports).Disposed);
        Assert.Empty(registry.All.OfType<McpToolAdapter>());
        Assert.Equal("readonly", workspace.Config.Mode);
    }

    [Fact]
    public async Task AuthenticationChange_ReplacesRemoteConnection_AndNeverExposesCredentials()
    {
        var factory = new FakeFactory();
        var config = Config("remote");
        var server = config.Config.Mcp.Servers["remote"];
        server.Transport = "http";
        server.Url = "https://example.invalid/mcp";
        server.Headers = new() { ["Authorization"] = "Bearer local-test-only" };
        using var authentication = new McpAuthentication(new McpCredentialStore(Path.Combine(_directory, "credentials")));
        await using var pool = new McpConnectionPool(authentication, factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(config, registry, new PromptRegistry(), connections: pool);
        var response = await Search(registry, Context("first"), "remote", "read");
        Assert.DoesNotContain("local-test-only", response.Output);
        var old = Assert.Single(factory.Transports);

        await authentication.LogoutAsync("remote", server);
        Assert.True(old.Disposed);
        await manager.PrepareAsync(Workspace(), CancellationToken.None);

        Assert.True(old.Disposed);
        await Search(registry, Context("first"), "remote", "read");
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task Search_RespectsReadOnlyDisabledNetworkAndFutureRegistrationCeilings()
    {
        var factory = new FakeFactory();
        var config = Config("files", "remote");
        config.Config.Mcp.Servers["remote"].Transport = "http";
        config.Config.Mcp.Servers["remote"].Url = "https://example.invalid/mcp";
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(config, registry, new PromptRegistry(), connections: pool);
        registry.RetainOnly(tool => tool.Name != "mcp__files__read_secret");
        var workspace = Workspace(new WorkspaceConfig { Mode = "readonly", DisabledTools = ["mcp__files__read_file"] });
        var response = await Search(registry, Context("restricted", workspace, network: false), "files");

        Assert.Empty(JsonNode.Parse(response.Output)!["tools"]!.AsArray());
        Assert.Null(registry.Resolve("mcp__files__read_secret"));
        var blocked = await Search(registry, Context("restricted", workspace, network: false), "remote", "read");
        Assert.Contains("Network access is disabled", blocked.Output);
        Assert.DoesNotContain(factory.Transports, transport => transport.Name == "remote");
        Assert.All(factory.Transports, transport => Assert.Equal(0, transport.Calls));
    }

    [Fact]
    public async Task Discovery_FollowsCatalogPages_AndPromptRegistrationStillWorks()
    {
        var factory = new FakeFactory { Paginated = true };
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        var prompts = new PromptRegistry();
        await using var manager = new McpManager(Config("files"), registry, prompts, connections: pool);
        await Search(registry, Context("first"), "files", "second_page");
        Assert.True(Assert.IsType<McpToolAdapter>(registry.Resolve("mcp__files__second_page")).IsLoaded("first"));
        var remotePrompt = Assert.Single(prompts.All, x => x.Id == "mcp:files:guide");
        var text = await remotePrompt.Resolver(new PromptRenderContext { Variables = new Dictionary<string, string> { ["tool"] = "mcp__files__second_page" } }, CancellationToken.None);
        Assert.Equal("A cached server guide", text);
    }

    [Fact]
    public async Task DisconnectedClient_ReconnectsOnDiscovery_ButToolCallsAreNeverRetried()
    {
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(Config("files"), registry, new PromptRegistry(), connections: pool);
        await Search(registry, Context("first"), "files", "read_file");
        var original = Assert.Single(factory.Transports);
        original.FailCalls = true;
        var tool = Assert.IsType<McpToolAdapter>(registry.Resolve("mcp__files__read_file"));
        Assert.False((await tool.ExecuteAsync(new(), Context("first"), CancellationToken.None)).Success);
        Assert.Equal(1, original.Calls);
        Assert.Single(factory.Transports);

        original.Disconnect();
        await original.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Search(registry, Context("first"), "files", "read_file");
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task Search_ValidatesStructuredArguments_AndHidesUnavailableCatalogs()
    {
        var config = Config();
        var registry = new ToolRegistry();
        await using var manager = new McpManager(config, registry, new PromptRegistry());
        var search = Assert.IsType<McpToolSearchTool>(registry.Resolve("tool_search"));
        Assert.False(search.IsAvailable(Workspace(), true));
        config.Config.Mcp.Servers["remote"] = new McpServerConfig { Transport = "http", Url = "https://example.invalid/mcp" };
        Assert.True(search.IsAvailable(Workspace(), true));
        Assert.False(search.IsAvailable(Workspace(), false));
        config.Config.Mcp.Servers["remote"].Enabled = false;
        Assert.False(search.IsAvailable(Workspace(), true));
        foreach (var arguments in new[] { new JsonObject { ["query"] = new JsonObject() },
                     new JsonObject { ["server"] = new JsonArray() }, new JsonObject { ["limit"] = "five" } })
            Assert.False((await search.ExecuteAsync(arguments, Context("first"), CancellationToken.None)).Success);
    }

    [Fact]
    public async Task CachedRemotePrompts_AreFetchedOnlyForVisibleTools_AndRespectNetworkPermission()
    {
        var config = Config("remote");
        config.Config.Mcp.Servers["remote"].Transport = "http";
        config.Config.Mcp.Servers["remote"].Url = "https://example.invalid/mcp";
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        var prompts = new PromptRegistry();
        await using var manager = new McpManager(config, registry, prompts, connections: pool);
        await manager.ConnectAllAsync(Workspace(), CancellationToken.None);
        var transport = Assert.Single(factory.Transports);
        var remote = Assert.Single(prompts.All, item => item.Id == "mcp:remote:guide");
        var inactive = new PromptRenderContext { Variables = new Dictionary<string, string> { ["tool"] = "tool_search", ["network"] = "enabled" } };
        Assert.Null(await remote.Resolver(inactive, CancellationToken.None));
        Assert.Equal(0, transport.PromptCalls);

        await Search(registry, Context("first"), "remote", "read_file");
        remote = Assert.Single(prompts.All, item => item.Id == "mcp:remote:guide");
        var variables = new Dictionary<string, string> { ["tool"] = "mcp__remote__read_file", ["network"] = "disabled" };
        Assert.Null(await remote.Resolver(new PromptRenderContext { Variables = variables }, CancellationToken.None));
        Assert.Equal(0, transport.PromptCalls);
        variables["network"] = "enabled";
        Assert.Equal("A cached server guide", await remote.Resolver(new PromptRenderContext { Variables = variables }, CancellationToken.None));
        Assert.Equal(1, transport.PromptCalls);
    }

    [Fact]
    public async Task CollidingNames_RemainStableWhenRoleFiltersRejectTheFirstTool()
    {
        var factory = new FakeFactory { CollidingNames = true };
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var registry = new ToolRegistry();
        await using var manager = new McpManager(Config("files"), registry, new PromptRegistry(), connections: pool);
        registry.RetainOnly(tool => tool.Name != "mcp__files__read_file");

        await Search(registry, Context("first"), "files");

        var tool = Assert.Single(registry.All.OfType<McpToolAdapter>());
        Assert.Equal("mcp__files__read_file_2", tool.Name);
        Assert.True(tool.IsLoaded("first"));
    }

    [Fact]
    public async Task PoolCapacity_DoesNotEvictALeasedConnectionBeforeItsRequestStarts()
    {
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new McpServerConfig { Command = "fake-server" };
        var active = pool.UseAsync(Workspace(), "active", config, async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var index = 0; index < 64; index++)
                await pool.GetAsync(Workspace(), $"idle-{index}", config, CancellationToken.None);
            Assert.False(Assert.Single(factory.Transports, transport => transport.Name == "active").Disposed);
            Assert.Equal(1, factory.Transports.Count(transport => transport.Disposed));
        }
        finally { release.TrySetResult(); }
        Assert.True(await active);
    }

    [Fact]
    public async Task DaemonStatus_ReflectsLazySiblingConnectionsAndMetadata_AndExplicitReloadInvalidatesOldAdapters()
    {
        var config = Config("files");
        config.Config.Mcp.Servers["files"].Args = ["--flag"];
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        await using var runtime = SeekClawRuntime.CreateIsolated(Workspace(), configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_directory, "daemon.db")));
            services.AddSingleton(pool);
        });
        var admin = new DaemonAdminApi(runtime, Workspace(), new FileLockCoordinator());
        var notifications = new ConcurrentQueue<string>();
        void Changed() => notifications.Enqueue(admin.ListMcpServers());
        admin.McpStatusChanged += Changed;
        try
        {
            Assert.False(JsonNode.Parse(admin.ListMcpServers())![0]!["connected"]!.GetValue<bool>());
            Assert.Empty(factory.Transports);
            var registry = new ToolRegistry();
            await using var sibling = new McpManager(config, registry, new PromptRegistry(), connections: pool);
            await Search(registry, Context("first"), "files", "read_file");
            var original = Assert.Single(factory.Transports);
            var oldAdapter = Assert.IsType<McpToolAdapter>(registry.Resolve("mcp__files__read_file"));

            var connected = JsonNode.Parse(admin.ListMcpServers())![0]!;
            Assert.True(connected["connected"]!.GetValue<bool>());
            Assert.Equal(3, connected["toolCount"]!.GetValue<int>());
            Assert.Equal("fake-server", connected["command"]!.GetValue<string>());
            Assert.Equal("--flag", connected["args"]![0]!.GetValue<string>());
            Assert.Equal("global", connected["scope"]!.GetValue<string>());
            Assert.Contains(notifications, json => JsonNode.Parse(json)![0]!["connected"]!.GetValue<bool>());

            await runtime.Mcp.ConnectAllAsync(runtime.Workspace, CancellationToken.None);

            Assert.True(original.Disposed);
            Assert.Equal(2, factory.Transports.Count);
            Assert.False((await oldAdapter.ExecuteAsync(new(), Context("first"), CancellationToken.None)).Success);
            Assert.All(factory.Transports, transport => Assert.Equal(0, transport.Calls));
            Assert.True(JsonNode.Parse(admin.ListMcpServers())![0]!["connected"]!.GetValue<bool>());
        }
        finally { admin.McpStatusChanged -= Changed; }
    }

    [Fact]
    public async Task SharedStatus_FollowsDisconnectionAndRecovery_WithoutAnotherManagerReload()
    {
        var config = Config("files");
        var factory = new FakeFactory();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        var firstRegistry = new ToolRegistry();
        await using var first = new McpManager(config, firstRegistry, new PromptRegistry(), connections: pool);
        await using var observer = new McpManager(config, new ToolRegistry(), new PromptRegistry(), connections: pool);
        await observer.PrepareAsync(Workspace(), CancellationToken.None);
        await Search(firstRegistry, Context("first"), "files", "read_file");
        Assert.True(Assert.Single(observer.Status).Connected);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (!Assert.Single(observer.Status).Connected) disconnected.TrySetResult(); }
        pool.StatusChanged += Changed;
        try
        {
            Assert.Single(factory.Transports).Disconnect();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("disconnected", Assert.Single(observer.Status).Error);
            await Search(firstRegistry, Context("first"), "files", "read_file");
            Assert.True(Assert.Single(observer.Status).Connected);
            Assert.Equal(2, factory.Transports.Count);
        }
        finally { pool.StatusChanged -= Changed; }
    }

    [Fact]
    public async Task Logout_RevokesAllWorkspaceConnections_WithoutStartingReplacements()
    {
        var factory = new FakeFactory();
        var config = Config("remote");
        var server = config.Config.Mcp.Servers["remote"];
        server.Transport = "http";
        server.Url = "https://example.invalid/mcp";
        using var authentication = new McpAuthentication(new McpCredentialStore(Path.Combine(_directory, "credentials")));
        await using var pool = new McpConnectionPool(authentication, factory.Create);
        var registry = new ToolRegistry();
        await using var first = new McpManager(config, registry, new PromptRegistry(), connections: pool);
        await Search(registry, Context("first"), "remote", "read_file");
        var other = new WorkspaceInfo { Root = Path.Combine(_directory, "other"), ProjectKinds = [] };
        Directory.CreateDirectory(other.Root);
        var otherRegistry = new ToolRegistry();
        await using var second = new McpManager(config, otherRegistry, new PromptRegistry(), connections: pool);
        await Search(otherRegistry, Context("second", other), "remote", "read_file");
        Assert.Equal(2, factory.Transports.Count);

        await authentication.LogoutAsync("remote", server);

        Assert.All(factory.Transports, transport => Assert.True(transport.Disposed));
        Assert.False(Assert.Single(first.GetStatus(Workspace())).Connected);
        Assert.False(Assert.Single(second.GetStatus(other)).Connected);
        Assert.Equal(2, factory.Transports.Count);
    }

    [Fact]
    public async Task SubAgentRole_WithAnExactRemoteTool_CanDiscoverOnlyItsAllowedSchema()
    {
        var config = CreateAgentConfig("full");
        var factory = new FakeFactory();
        var model = new DiscoveryModel("read_file");
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        await using var runtime = SeekClawRuntime.CreateIsolated(Workspace(), configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_directory, "role.db")));
            services.AddSingleton<ILlmClientFactory>(model);
            services.AddSingleton(pool);
        });
        runtime.SubAgents.Register(new SubAgentDefinition("remote-reader", "Read a single MCP tool", "subagent/researcher",
            new HashSet<string> { "mcp__files__read_file" }, AllowMutating: false));
        var parent = runtime.Sessions.Create(runtime.Workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await runtime.SubAgentRunner.ExecuteSubTaskAsync(new SubAgentTaskRequest("remote-reader", "Inspect files"), new ToolContext
        {
            Workspace = runtime.Workspace, SessionWorkspace = runtime.Workspace, Events = runtime.Events,
            Agent = config.Config.Agent, SessionId = parent.Header.Id, Owner = parent.Header.Id,
        }, timeout.Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("done", result.Output);
        Assert.Contains(model.Requests[0].Tools, tool => tool.Name == "tool_search");
        var remote = Assert.Single(model.Requests[1].Tools, tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Equal("mcp__files__read_file", remote.Name);
        Assert.Equal(1, Assert.Single(factory.Transports).Calls);
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("manual", false)]
    [InlineData("readonly", false)]
    public async Task Agent_DiscoveryLoadsTheNextRequest_AndCannotBypassSameRequestOrPermissions(string mode, bool canExecute)
    {
        var config = CreateAgentConfig(mode);
        var factory = new FakeFactory();
        var model = new DiscoveryModel();
        var approval = new DenyingApprovalGate();
        await using var pool = new McpConnectionPool(transportFactory: factory.Create);
        await using var runtime = SeekClawRuntime.CreateIsolated(Workspace(), configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(config);
            services.AddSingleton(new SeekClawDatabase(Path.Combine(_directory, "agent.db")));
            services.AddSingleton<ILlmClientFactory>(model);
            services.AddSingleton<IToolApprovalGate>(approval);
            services.AddSingleton(pool);
        });
        var session = runtime.Sessions.Create(runtime.Workspace);

        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "Use the files server.", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("done", result.Text);
        Assert.Equal(3, model.Requests.Count);
        Assert.Contains(model.Requests[0].Tools, tool => tool.Name == "tool_search");
        Assert.DoesNotContain(model.Requests[0].Tools, tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Equal(mode != "readonly", model.Requests[1].Tools.Any(tool => tool.Name == "mcp__files__write_file"));
        Assert.False(Assert.Single(session.Messages, message => message.ToolCallId == "guessed").ToolSuccess);
        Assert.Equal(canExecute, Assert.Single(session.Messages, message => message.ToolCallId == "loaded").ToolSuccess);
        Assert.Equal(canExecute ? 1 : 0, Assert.Single(factory.Transports).Calls);
        Assert.Equal(mode == "manual" ? 1 : 0, approval.Requests);

        await runtime.Agent.RunTurnAsync(session, runtime.Workspace, "New turn", CancellationToken.None);
        Assert.DoesNotContain(model.Requests[3].Tools, tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Single(factory.Transports);
    }

    private ConfigStore CreateAgentConfig(string mode)
    {
        var config = Config("files");
        config.Config.Providers.Clear();
        config.Config.Providers.Add(new ProviderConfig { Id = "test", Kind = "openai", BaseUrl = "https://example.invalid/v1",
            Models = [new ModelConfig { Id = "model", ContextWindow = 128_000, MaxOutput = 512 }] });
        config.Config.Provider = "test";
        config.Config.Model = "model";
        config.Config.Routing.Fallback = ["test/model"];
        config.Config.Agent.EnableCrossSessionMemory = false;
        config.Config.Agent.AutoVerify = false;
        config.Config.Agent.Mode = mode;
        return config;
    }

    private sealed class DiscoveryModel(string toolName = "write_file") : ILlmClientFactory
    {
        private readonly string _remoteName = "mcp__files__" + toolName;
        private readonly string _query = toolName;
        public List<LlmRequest> Requests { get; } = [];
        public ILlmClient GetClient(string kind) => new Client(this);
        private sealed class Client(DiscoveryModel owner) : ILlmClient
        {
            public string Kind => "openai";
            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                owner.Requests.Add(request with { Messages = request.Messages.ToList() });
                yield return new LlmCompleted(owner.Requests.Count switch
                {
                    1 => new LlmCompletion { FinishReason = "tool_calls", ToolCalls = [
                        new ToolCallRequest("search", "tool_search", new JsonObject { ["server"] = "files", ["query"] = owner._query }.ToJsonString()),
                        new ToolCallRequest("guessed", owner._remoteName, "{}") ] },
                    2 => new LlmCompletion { FinishReason = "tool_calls", ToolCalls = [new ToolCallRequest("loaded", owner._remoteName, "{}")] },
                    _ => new LlmCompletion { FinishReason = "stop", Text = "done" },
                });
            }
        }
    }

    private sealed class DenyingApprovalGate : IToolApprovalGate
    {
        public int Requests { get; private set; }
        public ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Requests++;
            return ValueTask.FromResult(ToolApprovalDecision.Deny("Rejected by the test approval channel."));
        }
    }

    private ConfigStore Config(params string[] names)
    {
        var store = new ConfigStore(Path.Combine(_directory, Guid.NewGuid() + ".json"), Path.Combine(_directory, Guid.NewGuid() + ".state"));
        foreach (var name in names) store.Config.Mcp.Servers[name] = new McpServerConfig { Command = "fake-server" };
        return store;
    }

    private WorkspaceInfo Workspace(WorkspaceConfig? config = null) => new() { Root = _directory, ProjectKinds = [], Config = config };
    private ToolContext Context(string session, WorkspaceInfo? workspace = null, bool network = true) => new()
    {
        Workspace = workspace ?? Workspace(), Events = new EventBus(), Agent = new AgentConfig { Mode = "full" },
        SessionId = session, NetworkEnabled = network,
    };
    private Task<ToolResult> Search(ToolRegistry registry, ToolContext context, string? server = null,
        string? query = null, CancellationToken ct = default, int limit = 5) => registry.Resolve("tool_search")!.ExecuteAsync(
            new JsonObject { ["server"] = server, ["query"] = query, ["limit"] = limit }, context, ct);

    private sealed class FakeFactory
    {
        public ConcurrentBag<FakeTransport> Transports { get; } = [];
        public Func<CancellationToken, Task>? BeforeInitialize { get; init; }
        public bool Paginated { get; init; }
        public bool CollidingNames { get; init; }
        public IMcpTransport Create(string name, McpServerConfig _) { var transport = new FakeTransport(name, this); Transports.Add(transport); return transport; }
    }

    private sealed class FakeTransport(string name, FakeFactory factory) : IMcpTransport
    {
        private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
        public string Name => name;
        public bool Disposed { get; private set; }
        public bool FailCalls { get; set; }
        public int Initializations { get; private set; }
        public int Calls { get; private set; }
        public int PromptCalls { get; private set; }
        public ChannelReader<JsonObject> Reader => _incoming.Reader;
        public ChannelReader<JsonObject> Incoming => _incoming.Reader;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task SendAsync(JsonObject message, CancellationToken ct)
        {
            var method = message["method"]!.GetValue<string>();
            if (message["id"] is null) return;
            JsonObject result;
            switch (method)
            {
                case "initialize":
                    Initializations++;
                    if (factory.BeforeInitialize is not null) await factory.BeforeInitialize(ct);
                    result = new() { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() };
                    break;
                case "tools/list":
                    if (factory.CollidingNames)
                        result = new() { ["tools"] = new JsonArray(Tool("read.file", "First"), Tool("read/file", "Second")) };
                    else if (message["params"]?["cursor"] is not null)
                        result = new() { ["tools"] = new JsonArray(Tool("second_page", "Second page tool")) };
                    else result = new() { ["tools"] = new JsonArray(Tool("read_file", "Read project files"),
                            Tool("read_secret", "Read restricted material"), Tool("write_file", "Write project files")),
                        ["nextCursor"] = factory.Paginated ? "next" : null };
                    break;
                case "prompts/list": result = new() { ["prompts"] = new JsonArray(new JsonObject { ["name"] = "guide" }) }; break;
                case "prompts/get":
                    PromptCalls++;
                    result = new() { ["messages"] = new JsonArray(new JsonObject
                        { ["content"] = new JsonObject { ["type"] = "text", ["text"] = "A cached server guide" } }) }; break;
                case "tools/call":
                    Calls++;
                    if (FailCalls)
                    {
                        await _incoming.Writer.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Lost tool response" } }, ct);
                        return;
                    }
                    result = new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }),
                        ["structuredContent"] = new JsonObject { ["ok"] = true } }; break;
                default: throw new InvalidOperationException(method);
            }
            await _incoming.Writer.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]!.DeepClone(), ["result"] = result }, ct);
        }
        public void Disconnect() => _incoming.Writer.TryComplete();
        public ValueTask DisposeAsync() { Disposed = true; _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
        private static JsonObject Tool(string name, string description) => new()
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        };
    }
}
