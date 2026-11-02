using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.SubAgents;

public sealed class SubAgentRunner(
    ISubAgentRegistry registry,
    IFileLockCoordinator fileLocks,
    ILlmHttpFactory httpFactory,
    CircuitBreaker circuitBreaker,
    ILlmClientFactory? clientFactory = null,
    RuntimeTaskServices? taskServices = null,
    IConfigStore? configStore = null)
{
    public async Task<SubAgentTaskResult> ExecuteSubTaskAsync(
        SubAgentTaskRequest request,
        ToolContext context,
        CancellationToken ct)
    {
        if (taskServices is not null)
        {
            var task = await StartAsync(request, context, ct).ConfigureAwait(false);
            var id = task["id"]!.GetValue<string>();
            while (task["status"]?.GetValue<string>() == "running")
            {
                if (ct.IsCancellationRequested)
                {
                    await taskServices.SubAgents.CancelAsync(Parent(context), id, CancellationToken.None).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                task = await taskServices.SubAgents.WaitAsync(Parent(context), id, 1000, CancellationToken.None).ConfigureAwait(false);
            }
            return new SubAgentTaskResult(task["status"]?.GetValue<string>() == "completed",
                task["output"]?.GetValue<string>() ?? "", task["error"]?.GetValue<string>());
        }
        return await RunSessionAsync(request, context, null, null, ct).ConfigureAwait(false);
    }

    public static string Parent(ToolContext context) => string.IsNullOrWhiteSpace(context.SessionId) ? context.Owner : context.SessionId;

    public async Task<System.Text.Json.Nodes.JsonObject> StartAsync(SubAgentTaskRequest request, ToolContext context, CancellationToken ct)
    {
        if (taskServices is null) throw new InvalidOperationException("Persistent sub-agent services are unavailable.");
        if (context.SubAgentDepth >= 1) throw new InvalidOperationException("Sub-agents cannot delegate further tasks.");
        if (registry.Resolve(request.Role) is null) throw new ArgumentException("Unknown sub-agent role.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Task);
        var sessions = new SessionStore(taskServices.Database);
        var session = sessions.Create(context.Workspace, networkEnabled: context.NetworkEnabled && context.Agent.NetworkEnabled);
        var id = Guid.NewGuid().ToString("N");
        if (request.Isolated)
            await taskServices.Worktrees.CreateAsync(context.Workspace.Root, session.Header.Id, ct: ct).ConfigureAwait(false);
        var record = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id, ["sessionId"] = session.Header.Id, ["workspace"] = context.Workspace.Root,
            ["role"] = request.Role, ["model"] = request.ModelOverride,
            ["mode"] = context.Workspace.Config?.Mode ?? context.Agent.Mode,
            ["networkEnabled"] = context.NetworkEnabled && context.Agent.NetworkEnabled,
        };
        // Capture the small immutable permission/configuration context; no child depends on its parent's DI lifetime.
        return await taskServices.SubAgents.StartAsync(Parent(context), record, async token =>
        {
            var result = await RunSessionAsync(request, context, session, id, token).ConfigureAwait(false);
            return new AgentTurnResult(result.Output, token.IsCancellationRequested, result.Error);
        }, ct).ConfigureAwait(false);
    }

    public async Task<System.Text.Json.Nodes.JsonObject> SendAsync(string id, string instruction, ToolContext context, CancellationToken ct)
    {
        if (taskServices is null) throw new InvalidOperationException("Persistent sub-agent services are unavailable.");
        if (context.SubAgentDepth >= 1) throw new InvalidOperationException("Sub-agents cannot control sibling tasks.");
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        var record = taskServices.SubAgents.Get(Parent(context), id);
        if (record["workspace"]!.GetValue<string>() != context.Workspace.Root)
            throw new InvalidOperationException("Sub-agent workspace changed; return to the original workspace before resuming.");
        var session = new SessionStore(taskServices.Database).Load(context.Workspace, record["sessionId"]!.GetValue<string>())
            ?? throw new InvalidOperationException("Sub-agent session is missing.");
        var request = new SubAgentTaskRequest(record["role"]!.GetValue<string>(), instruction, record["model"]?.GetValue<string>());
        // Preserve the original ceiling if the parent has gained broader permissions since spawning.
        var mode = AgentModeExtensions.Parse(record["mode"]?.GetValue<string>());
        var current = AgentModeExtensions.Parse(context.Workspace.Config?.Mode ?? context.Agent.Mode);
        var limitedMode = mode.IsReadOnly() ? mode : current.IsReadOnly() ? current
            : mode == AgentMode.Manual || current == AgentMode.Manual ? AgentMode.Manual
            : mode == AgentMode.Guardrail || current == AgentMode.Guardrail ? AgentMode.Guardrail : current;
        var config = CloneConfig(context.Workspace.Config);
        config.Mode = limitedMode.ToModeString();
        var workspace = new WorkspaceInfo { Root = context.Workspace.Root, ProjectKinds = context.Workspace.ProjectKinds, Config = config,
            ConfigurationRoot = context.Workspace.ConfigurationRoot };
        var limitedContext = new ToolContext { Workspace = workspace, Events = context.Events, Agent = context.Agent,
            SessionId = context.SessionId, Owner = context.Owner, Coordinator = context.Coordinator,
            SessionWorkspace = context.SessionWorkspace, NetworkEnabled = context.NetworkEnabled && (record["networkEnabled"]?.GetValue<bool>() ?? false),
            ApprovalGate = context.ApprovalGate, SubAgentDepth = context.SubAgentDepth };
        session.Header.NetworkEnabled &= record["networkEnabled"]?.GetValue<bool>() ?? false;
        return await taskServices.SubAgents.StartAsync(Parent(context), record, async token =>
        {
            var result = await RunSessionAsync(request, limitedContext, session, id, token).ConfigureAwait(false);
            return new AgentTurnResult(result.Output, token.IsCancellationRequested, result.Error);
        }, ct).ConfigureAwait(false);
    }

    private async Task<SubAgentTaskResult> RunSessionAsync(SubAgentTaskRequest request, ToolContext context,
        AgentSession? savedSession, string? persistentId, CancellationToken ct)
    {
        if (context.SubAgentDepth >= 1)
        {
            return new SubAgentTaskResult(false, "", "Sub-agents cannot invoke further sub-agents (maximum delegation depth reached).");
        }

        var def = registry.Resolve(request.Role);
        if (def is null)
        {
            var valid = string.Join(", ", registry.All.Select(r => $"'{r.Role}'"));
            return new SubAgentTaskResult(false, "", $"Unknown sub-agent role '{request.Role}'. Supported roles: {valid}.");
        }

        var subAgentId = persistentId ?? Guid.NewGuid().ToString("N")[..8];
        var childOwner = $"{context.Owner}/subagent-{subAgentId}";
        var childScope = new SubAgentScope(context.SubAgentDepth + 1, subAgentId, def.Role);
        var coordinator = context.Coordinator ?? fileLocks;

        context.Events.Publish(new SubAgentStartedEvent(
            subAgentId,
            def.Role,
            request.Task,
            request.ModelOverride ?? def.DefaultModelOverride));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var isolatedRuntime = SeekClawRuntime.CreateIsolated(
                context.Workspace,
                coordinator,
                childOwner,
                childScope,
                services =>
                {
                    services.AddSingleton(httpFactory);
                    services.AddSingleton(circuitBreaker);
                    taskServices?.ShareWith(services);
                    if (configStore is not null) services.AddSingleton<IConfigStore>(new TaskConfigStore(configStore));
                    if (clientFactory is not null)
                    {
                        services.AddSingleton(clientFactory);
                    }
                    if (context.ApprovalGate is not null)
                    {
                        services.AddSingleton(context.ApprovalGate);
                    }
                    if (savedSession is null) services.AddSingleton<ISessionStore>(new InMemorySessionStore());
                });

            if (isolatedRuntime.Mcp.LoadServerConfigs(context.Workspace).Count > 0)
                await isolatedRuntime.Mcp.ConnectAllAsync(context.Workspace, ct).ConfigureAwait(false);

            // Filter after MCP registration, so role restrictions cover remote tools too.
            isolatedRuntime.Tools.RetainOnly(tool =>
            {
                if (tool.Name is "invoke_subagent" or "subagent")
                    return false;
                if (!def.AllowMutating && tool.Mutating)
                    return false;
                if (def.AllowedTools is { Count: > 0 } && !def.AllowedTools.Contains(tool.Name))
                    return false;
                return true;
            });

            // 2. Configure system prompt, mode, and max steps
            isolatedRuntime.ConfigStore.Config.Agent.SystemPrompt = def.PromptKey;
            isolatedRuntime.ConfigStore.Config.Agent.MaxSteps = def.MaxSteps;
            var parentMode = AgentModeExtensions.Parse(context.Workspace.Config?.Mode ?? context.Agent.Mode);
            isolatedRuntime.ConfigStore.Config.Agent.Mode = def.AllowMutating
                ? parentMode.ToModeString()
                : "readonly";
            isolatedRuntime.ConfigStore.Config.Agent.AutoVerify = false;
            // Workspace overrides must not replace the specialist prompt or raise its permission ceiling.
            var childConfig = CloneConfig(context.Workspace.Config);
            childConfig.Mode = isolatedRuntime.ConfigStore.Config.Agent.Mode;
            childConfig.SystemPrompt = def.PromptKey; childConfig.AutoVerify = false;
            var childWorkspace = new WorkspaceInfo { Root = context.Workspace.Root,
                ProjectKinds = context.Workspace.ProjectKinds, Config = childConfig, ConfigurationRoot = context.Workspace.ConfigurationRoot };

            // 3. Optional model override
            var modelToUse = request.ModelOverride ?? def.DefaultModelOverride;
            if (!string.IsNullOrWhiteSpace(modelToUse))
            {
                var resolved = isolatedRuntime.Models.Resolve(modelToUse);
                if (resolved is null) throw new ArgumentException($"Unknown sub-agent model: {modelToUse}");
                else
                {
                    isolatedRuntime.ConfigStore.Config.Provider = resolved.Provider.Id;
                    isolatedRuntime.ConfigStore.Config.Model = resolved.Model.Id;
                    childConfig.Provider = resolved.Provider.Id;
                    childConfig.Model = resolved.Model.Id;
                }
            }

            // 4. Forward events from isolated runtime to parent event bus for live UI updates
            using var childSubscription = isolatedRuntime.Events.Subscribe();
            using var forwardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var forwardTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in childSubscription.Reader.ReadAllAsync(forwardCts.Token).ConfigureAwait(false))
                    {
                        switch (evt)
                        {
                            case ToolCallStartedEvent tool:
                                context.Events.Publish(new SubAgentProgressEvent(
                                    subAgentId, def.Role, $"Using {tool.ToolName}", tool.ArgumentSummary));
                                break;
                            case StatusEvent status:
                                context.Events.Publish(new SubAgentProgressEvent(
                                    subAgentId, def.Role, status.Status, status.Detail));
                                break;
                            case WorkflowEvent wf:
                                context.Events.Publish(new SubAgentProgressEvent(
                                    subAgentId, def.Role, wf.Label, wf.Detail));
                                break;
                            case FileDiffEvent diff:
                                context.Events.Publish(diff);
                                break;
                            case UsageRecordedEvent usage:
                                context.Events.Publish(usage);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, forwardCts.Token);

            // 5. Create in-memory session and execute turn. The forwarding task must be
            // stopped in every path, including unexpected exceptions, or it keeps the
            // isolated event subscription and its parent callback alive after cancellation.
            AgentTurnResult turnResult;
            try
            {
                var session = savedSession ?? isolatedRuntime.Sessions.Create(
                    childWorkspace,
                    networkEnabled: context.NetworkEnabled && context.Agent.NetworkEnabled);

                turnResult = await isolatedRuntime.Agent.RunTurnAsync(
                    session,
                    childWorkspace,
                    request.Task,
                    ct).ConfigureAwait(false);
                if (turnResult.Maintenance is { } maintenance) await maintenance.ConfigureAwait(false);
            }
            finally
            {
                forwardCts.Cancel();
                try { await forwardTask.ConfigureAwait(false); } catch { }
            }

            stopwatch.Stop();

            if (turnResult.Cancelled)
            {
                context.Events.Publish(new SubAgentCompletedEvent(
                    subAgentId, def.Role, false, "Sub-agent was cancelled", stopwatch.Elapsed));
                return new SubAgentTaskResult(false, "", "Sub-agent execution was cancelled.", stopwatch.Elapsed);
            }

            if (turnResult.Error is not null)
            {
                context.Events.Publish(new SubAgentCompletedEvent(
                    subAgentId, def.Role, false, turnResult.Error, stopwatch.Elapsed));
                return new SubAgentTaskResult(false, "", turnResult.Error, stopwatch.Elapsed);
            }

            var output = string.IsNullOrWhiteSpace(turnResult.Text)
                ? $"Sub-agent [{def.Role}] completed the task."
                : turnResult.Text;

            context.Events.Publish(new SubAgentCompletedEvent(
                subAgentId, def.Role, true, $"Completed in {stopwatch.Elapsed.TotalSeconds:0.1}s", stopwatch.Elapsed));

            return new SubAgentTaskResult(true, output, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            context.Events.Publish(new SubAgentCompletedEvent(
                subAgentId, def.Role, false, "Sub-agent was cancelled", stopwatch.Elapsed));
            return new SubAgentTaskResult(false, "", "Sub-agent execution was cancelled.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            context.Events.Publish(new SubAgentCompletedEvent(
                subAgentId, def.Role, false, ex.Message, stopwatch.Elapsed));
            return new SubAgentTaskResult(false, "", $"Sub-agent error: {ex.Message}", stopwatch.Elapsed);
        }
        finally
        {
            coordinator.ReleaseAll(childOwner);
        }
    }

    private static WorkspaceConfig CloneConfig(WorkspaceConfig? config) => config is null ? new WorkspaceConfig()
        : System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(config, SeekClawJsonContext.Default.WorkspaceConfig),
            SeekClawJsonContext.Default.WorkspaceConfig)!;
}
