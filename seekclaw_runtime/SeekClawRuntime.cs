using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Projects;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Skills;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;
using SeekClaw.Runtime.Verification;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime;

/// <summary>
/// Composition root and facade of the SeekClaw runtime. Every front end
/// (CLI today; GUI / Web / daemon clients tomorrow) drives the runtime through this type.
/// </summary>
public sealed class SeekClawRuntime : IAsyncDisposable, IDisposable
{
    private readonly ServiceProvider _services;

    public WorkspaceInfo Workspace { get; private set; }

    public IConfigStore ConfigStore => _services.GetRequiredService<IConfigStore>();
    public IEventBus Events => _services.GetRequiredService<IEventBus>();
    public IPromptProvider Prompts => _services.GetRequiredService<IPromptProvider>();
    public IPromptRegistry PromptRegistry => _services.GetRequiredService<IPromptRegistry>();
    public IModelRegistry Models => _services.GetRequiredService<IModelRegistry>();
    public IProviderManager Providers => _services.GetRequiredService<IProviderManager>();
    public IUsageTracker Usage => _services.GetRequiredService<IUsageTracker>();
    public IHealthChecker Health => _services.GetRequiredService<IHealthChecker>();
    public IToolRegistry Tools => _services.GetRequiredService<IToolRegistry>();
    public IWorkspaceManager Workspaces => _services.GetRequiredService<IWorkspaceManager>();
    public ISessionStore Sessions => _services.GetRequiredService<ISessionStore>();
    public IProjectStore Projects => _services.GetRequiredService<IProjectStore>();
    public SkillManager Skills => _services.GetRequiredService<SkillManager>();
    public IMcpManager Mcp => _services.GetRequiredService<IMcpManager>();
    public Agent Agent => _services.GetRequiredService<Agent>();

    private SeekClawRuntime(ServiceProvider services, WorkspaceInfo workspace)
    {
        _services = services;
        Workspace = workspace;
    }

    public static SeekClawRuntime Create(string? startDirectory = null)
        => CreateCore(startDirectory, null);

    /// <summary>
    /// Creates an isolated runtime for one concurrent agent turn. The daemon uses one
    /// instance per task so workspace prompts, skills, MCP registrations and event
    /// subscriptions cannot leak between tasks.
    /// </summary>
    internal static SeekClawRuntime CreateIsolated(WorkspaceInfo workspace)
    {
        SeekClawPaths.EnsureCreated();

        var services = new ServiceCollection().AddSeekClawRuntime().BuildServiceProvider();
        var runtime = new SeekClawRuntime(services, workspace);
        runtime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        runtime.Skills.Attach(workspace);
        runtime.RegisterBasePromptContributions();
        runtime.RegisterBuiltinTools();
        return runtime;
    }

    internal static SeekClawRuntime Create(
        string startDirectory,
        IConfigStore configStore,
        string? databaseFile = null)
        => CreateCore(startDirectory, services =>
        {
            services.AddSingleton(configStore);
            if (databaseFile is not null)
                services.AddSingleton(new SeekClawDatabase(databaseFile));
        });

    private static SeekClawRuntime CreateCore(
        string? startDirectory,
        Action<IServiceCollection>? configureServices)
    {
        SeekClawPaths.EnsureCreated();

        var serviceCollection = new ServiceCollection().AddSeekClawRuntime();
        configureServices?.Invoke(serviceCollection);
        var services = serviceCollection.BuildServiceProvider();
        var workspace = services.GetRequiredService<IWorkspaceManager>().Detect(startDirectory);

        var runtime = new SeekClawRuntime(services, workspace);
        runtime.Prompts.SetWorkspaceRoot(workspace.PromptsDir);
        runtime.Skills.Attach(workspace);
        runtime.RegisterBasePromptContributions();
        runtime.RegisterBuiltinTools();
        return runtime;
    }

    /// <summary>Re-detects the workspace (used after directory changes or init).</summary>
    public void RefreshWorkspace(string? startDirectory = null)
    {
        Workspace = Workspaces.Detect(startDirectory ?? Directory.GetCurrentDirectory());
        Prompts.SetWorkspaceRoot(Workspace.PromptsDir);
        Skills.Attach(Workspace);
    }

    public Task<IReadOnlyList<McpServerStatus>> ConnectMcpAsync(CancellationToken ct) =>
        Mcp.ConnectAllAsync(Workspace, ct);

    private void RegisterBasePromptContributions()
    {
        var registry = PromptRegistry;
        var prompts = Prompts;
        var configStore = ConfigStore;

        // Main system prompt — key configurable, overridable per workspace.
        registry.Register(new PromptContribution("system", PromptSlot.System, (ctx, _) =>
        {
            var key = ctx.Variables.TryGetValue("scope", out var scope) && scope == "global"
                ? "system/global"
                : ctx.WorkspaceConfig?.SystemPrompt ?? configStore.Config.Agent.SystemPrompt;
            return ValueTask.FromResult(prompts.TryGet(key));
        }));

        // Developer prompts per detected project kind (dotnet, node, python, rust, unity, vue…).
        registry.Register(new PromptContribution("developer", PromptSlot.Developer, (ctx, _) =>
        {
            var parts = ctx.ProjectKinds
                .Select(kind => prompts.TryGet($"developer/{kind}"))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToList();
            return ValueTask.FromResult<string?>(parts.Count == 0 ? null : string.Join("\n\n", parts));
        }));

        // Workspace memory (MEMORY.md), injected through the builtin/memory template.
        registry.Register(new PromptContribution("memory", PromptSlot.Memory, (ctx, _) =>
        {
            if (!ctx.Variables.TryGetValue("memory", out var memory) || string.IsNullOrWhiteSpace(memory))
                return ValueTask.FromResult<string?>(null);
            return ValueTask.FromResult<string?>(prompts.TryGet("builtin/memory") ?? memory);
        }));
    }

    private void RegisterBuiltinTools()
    {
        var prompts = Prompts;
        foreach (var tool in new ITool[]
                 {
                     new ReadFileTool(prompts),
                     new WriteFileTool(prompts),
                     new EditFileTool(prompts),
                     new ListDirTool(prompts),
                     new GlobTool(prompts),
                     new GrepTool(prompts),
                     new BashTool(prompts),
                     new WebSearchTool(prompts),
                     new WebFetchTool(prompts),
                 })
            Tools.Register(tool);
    }

    public async ValueTask DisposeAsync()
    {
        await Mcp.DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

public static class RuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddSeekClawRuntime(this IServiceCollection services)
    {
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IConfigStore>(_ => new ConfigStore());
        services.AddSingleton<IPromptProvider>(_ => new FilePromptProvider());
        services.AddSingleton<IPromptRegistry, PromptRegistry>();
        services.AddSingleton<PromptComposer>();

        services.AddSingleton<ILlmHttpFactory, LlmHttpFactory>();
        services.AddSingleton<ILlmClient, OpenAiCompatibleClient>();
        services.AddSingleton<ILlmClient, AnthropicClient>();
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddSingleton<IModelRegistry, ModelRegistry>();
        services.AddSingleton<IUsageTracker>(sp => new UsageTracker(sp.GetRequiredService<IEventBus>()));
        services.AddSingleton<IHealthChecker, HealthChecker>();
        services.AddSingleton<IProviderManager, ProviderManager>();

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        services.AddSingleton<SeekClawDatabase>();
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IProjectStore, ProjectStore>();
        services.AddSingleton<IVerifier, BuildVerifier>();
        services.AddSingleton<SkillManager>();
        services.AddSingleton<ISkillManager>(sp => sp.GetRequiredService<SkillManager>());
        services.AddSingleton<IMcpManager, McpManager>();
        services.AddSingleton<Agent>();
        return services;
    }
}
