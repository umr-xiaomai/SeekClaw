using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Scheduling;

/// <summary>Executes a scheduled task's agent turn; overridable in tests.</summary>
public delegate Task<AgentTurnResult> ScheduleTurnRunner(
    WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct);

/// <summary>Manual trigger surface used by daemon admin methods.</summary>
public interface IScheduleService
{
    Task RunNowAsync(string id, CancellationToken ct);
}

/// <summary>
/// Background scheduler hosted by the daemon. Fires enabled tasks when their cron
/// expression comes due, runs one agent turn per occurrence (in an isolated runtime,
/// sharing the daemon's HTTP pool / circuit breaker / file locks) and records the
/// outcome. A task that is still running when its next tick arrives is skipped.
/// </summary>
public sealed class ScheduleService : IScheduleService, IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly IScheduleStore _store;
    private readonly SeekClawRuntime _runtime;
    private readonly IFileLockCoordinator _fileLocks;
    private readonly LlmHttpFactory _sharedHttp;
    private readonly CircuitBreaker _sharedBreaker;
    private readonly ScheduleTurnRunner _turnRunner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly CancellationTokenSource _dispose = new();
    private TimeProvider _clock = TimeProvider.System;

    public ScheduleService(
        IScheduleStore store,
        SeekClawRuntime runtime,
        IFileLockCoordinator fileLocks,
        LlmHttpFactory sharedHttp,
        CircuitBreaker sharedBreaker,
        ScheduleTurnRunner? turnRunner = null,
        TimeProvider? clock = null)
    {
        _store = store;
        _runtime = runtime;
        _fileLocks = fileLocks;
        _sharedHttp = sharedHttp;
        _sharedBreaker = sharedBreaker;
        _turnRunner = turnRunner ?? RunIsolatedTurnAsync;
        if (clock is not null) _clock = clock;
    }

    /// <summary>Runs the scheduler loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _dispose.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await TickAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A failed tick must never kill the scheduler; retry on the next interval.
            }
            try
            {
                await Task.Delay(TickInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs every enabled task whose next run time has passed.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var task in _store.List())
        {
            if (!task.Enabled || _running.ContainsKey(task.Id)) continue;
            if (task.NextRunAt is not { } next || next > now) continue;
            await RunTaskAsync(task, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a task immediately, then recomputes its next occurrence.</summary>
    public async Task RunNowAsync(string id, CancellationToken ct)
    {
        var task = _store.Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        await RunTaskAsync(task, ct).ConfigureAwait(false);
    }

    private async Task RunTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        // Serialize executions so scheduled runs cannot pile up on the same machine,
        // and skip a task that is still running from a previous tick.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (!_running.TryAdd(task.Id, 0))
        {
            _gate.Release();
            return;
        }
        try
        {
            var workspace = ResolveWorkspace(task);
            var session = _runtime.Sessions.Create(
                workspace,
                reasoningLevel: _runtime.ConfigStore.Config.Agent.ReasoningLevel,
                networkEnabled: true);
            _runtime.Sessions.UpdateMetadata(workspace, session.Header.Id, title: $"{task.Name}（计划任务）");
            var result = await _turnRunner(workspace, session, task.Prompt, ct).ConfigureAwait(false);
            _store.RecordRun(
                task.Id,
                result.Error is null ? ScheduleRunStatus.Success : ScheduleRunStatus.Error,
                result.Error,
                result.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _store.RecordRun(task.Id, ScheduleRunStatus.Cancelled, "任务已取消");
        }
        catch (Exception ex)
        {
            _store.RecordRun(task.Id, ScheduleRunStatus.Error, ex.Message);
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            _gate.Release();
        }
    }

    private WorkspaceInfo ResolveWorkspace(ScheduledTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Workspace))
        {
            try
            {
                return _runtime.Workspaces.Detect(task.Workspace);
            }
            catch (Exception)
            {
                // Fall back to the global scope when the target path cannot be resolved.
            }
        }
        return _runtime.Workspaces.CreateGlobal();
    }

    private async Task<AgentTurnResult> RunIsolatedTurnAsync(
        WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct)
    {
        var owner = $"{session.Header.Id}/{Guid.NewGuid().ToString("N")[..8]}";
        await using var turnRuntime = SeekClawRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
        {
            services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
            services.AddSingleton(_sharedBreaker);
        });
        turnRuntime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        turnRuntime.Skills.Attach(workspace);
        if (turnRuntime.Mcp.LoadServerConfigs(workspace).Count > 0)
            await turnRuntime.Mcp.ConnectAllAsync(workspace, ct).ConfigureAwait(false);
        return await turnRuntime.Agent.RunTurnAsync(session, workspace, prompt, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _dispose.Cancel();
        _dispose.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
