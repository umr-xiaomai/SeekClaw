using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.SubAgents;

public sealed class SubAgentRunner(
    ISubAgentRegistry registry,
    IFileLockCoordinator fileLocks,
    ILlmHttpFactory httpFactory,
    CircuitBreaker circuitBreaker,
    ILlmClientFactory? clientFactory = null)
{
    public async Task<SubAgentTaskResult> ExecuteSubTaskAsync(
        SubAgentTaskRequest request,
        ToolContext context,
        CancellationToken ct)
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

        var subAgentId = Guid.NewGuid().ToString("N")[..8];
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
                    if (clientFactory is not null)
                    {
                        services.AddSingleton(clientFactory);
                    }
                    services.AddSingleton<ISessionStore>(new InMemorySessionStore());
                });

            // 1. Filter tools according to role permissions and explicitly exclude invoke_subagent
            isolatedRuntime.Tools.RetainOnly(tool =>
            {
                if (tool.Name.Equals("invoke_subagent", StringComparison.OrdinalIgnoreCase))
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
            isolatedRuntime.ConfigStore.Config.Agent.Mode = def.AllowMutating ? "edit" : "readonly";
            isolatedRuntime.ConfigStore.Config.Agent.AutoVerify = false;

            // 3. Optional model override
            var modelToUse = request.ModelOverride ?? def.DefaultModelOverride;
            if (!string.IsNullOrWhiteSpace(modelToUse))
            {
                var resolved = isolatedRuntime.Models.Resolve(modelToUse);
                if (resolved is not null)
                {
                    isolatedRuntime.ConfigStore.Config.Provider = resolved.Provider.Id;
                    isolatedRuntime.ConfigStore.Config.Model = resolved.Model.Id;
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

            // 5. Create in-memory session and execute turn
            var session = isolatedRuntime.Sessions.Create(
                context.Workspace,
                networkEnabled: context.Agent.NetworkEnabled);

            var turnResult = await isolatedRuntime.Agent.RunTurnAsync(
                session,
                context.Workspace,
                request.Task,
                ct).ConfigureAwait(false);

            forwardCts.Cancel();
            try { await forwardTask.ConfigureAwait(false); } catch { }

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
}
