using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
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
    /// <summary>Runs a task to completion (used by tests and legacy callers).</summary>
    Task RunNowAsync(string id, CancellationToken ct);

    /// <summary>
    /// Triggers a task without waiting: the run continues in the background while
    /// the caller is acknowledged immediately.
    /// </summary>
    void StartRun(string id);
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
    private static readonly TimeSpan UpcomingNoticeWindow = TimeSpan.FromMinutes(1);

    private readonly IScheduleStore _store;
    private readonly SeekClawRuntime _runtime;
    private readonly IFileLockCoordinator _fileLocks;
    private readonly LlmHttpFactory _sharedHttp;
    private readonly CircuitBreaker _sharedBreaker;
    private readonly ScheduleTurnRunner _turnRunner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly ConcurrentDictionary<string, string> _upcomingNotified = new();
    private readonly CancellationTokenSource _dispose = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _state = new();
    private readonly Dictionary<string, Task> _runs = new(StringComparer.Ordinal);
    private bool _disposed;
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
            if (task.NextRunAt is not { } next) continue;
            if (next > now)
            {
                PublishUpcomingNoticeIfNeeded(task, now, next);
                continue;
            }
            await RunTaskAsync(task, ct).ConfigureAwait(false);
        }
    }

    private void PublishUpcomingNoticeIfNeeded(ScheduledTask task, DateTimeOffset now, DateTimeOffset runAt)
    {
        if (runAt - now > UpcomingNoticeWindow) return;
        var key = runAt.ToString("O");
        if (_upcomingNotified.TryGetValue(task.Id, out var notified) && notified == key) return;
        _upcomingNotified[task.Id] = key;
        _runtime.Events.Publish(new ScheduledTaskUpcomingEvent(task.Id, task.DisplayName, runAt));
    }

    /// <summary>Runs a task immediately, then recomputes its next occurrence.</summary>
    public async Task RunNowAsync(string id, CancellationToken ct)
    {
        var task = _store.Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        await RunTaskAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers a task without blocking the caller. A scheduled task that is still
    /// running from a previous trigger is silently skipped.
    /// </summary>
    public void StartRun(string id)
    {
        var task = _store.Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        _ = RunTaskAsync(task, _lifetime.Token);
    }

    private Task RunTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Claim before waiting for the machine slot, so repeated manual triggers cannot queue duplicates.
            if (!_running.TryAdd(task.Id, 0)) return Task.CompletedTask;
            return _runs[task.Id] = Task.Run(() => RunClaimedAsync(task, ct), CancellationToken.None);
        }
    }

    private async Task RunClaimedAsync(ScheduledTask task, CancellationToken ct)
    {
        // A hung turn must not wedge the scheduler forever: give every run a hard
        // wall-clock budget, then record the timeout and free the task's slot.
        var timeoutSeconds = Math.Clamp(_runtime.ConfigStore.Config.Agent.ScheduledTurnTimeoutSeconds, 60, 86_400);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        runCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var runCt = runCts.Token;
        string? sessionId = null;
        var entered = false;

        try
        {
            await _gate.WaitAsync(runCt).ConfigureAwait(false);
            entered = true;
            var workspace = ResolveWorkspace(task);
            var session = task.SessionId is { Length: > 0 } existing
                ? _runtime.Sessions.Load(workspace, existing)
                    ?? throw new InvalidOperationException("The scheduled conversation no longer exists.")
                : _runtime.Sessions.Create(workspace,
                    reasoningLevel: _runtime.ConfigStore.Config.Agent.ReasoningLevel,
                    networkEnabled: _runtime.ConfigStore.Config.Agent.NetworkEnabled);
            sessionId = session.Header.Id;
            if (task.SessionId is null)
                _runtime.Sessions.UpdateMetadata(workspace, session.Header.Id, title: $"{task.Name}（计划任务）");
            if (task.RunInWorktree && _runtime.Worktrees.Get(workspace.Root, session.Header.Id) is null)
                await _runtime.Worktrees.CreateAsync(workspace.Root, session.Header.Id, ct: runCt).ConfigureAwait(false);
            var prompt = task.Prompt + $"\n\n[Scheduled run: {task.Id}. To stop this follow-up, use schedule action complete with this ID. "
                + "If there is no meaningful change to report, respond exactly [NO_CHANGE].]";
            var result = await _turnRunner(workspace, session, prompt, runCt).ConfigureAwait(false);
            if (result.Maintenance is { } maintenance) await maintenance.ConfigureAwait(false);
            RecordRun(
                task.Id,
                sessionId,
                result.Cancelled ? ScheduleRunStatus.Cancelled : result.Error is null ? ScheduleRunStatus.Success : ScheduleRunStatus.Error,
                result.Error,
                result.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RecordRun(task.Id, sessionId, ScheduleRunStatus.Cancelled, "任务已取消");
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
            RecordRun(task.Id, sessionId, ScheduleRunStatus.Cancelled, $"任务执行超过 {timeoutSeconds} 秒，已中止");
        }
        catch (Exception ex)
        {
            RecordRun(task.Id, sessionId, ScheduleRunStatus.Error, ex.Message);
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            if (entered) _gate.Release();
        }
    }

    private ScheduledTask? RecordRun(
        string id,
        string? sessionId,
        string status,
        string? error = null,
        string? output = null)
    {
        var previous = _store.Get(id);
        if (previous is null) return null; // Deleting a schedule does not invalidate an in-flight turn.
        var updated = _store.RecordRun(id, status, error, output);
        if (updated is null) return null;
        var changed = updated.LastStatus != previous.LastStatus || updated.LastError != previous.LastError || updated.LastOutput != previous.LastOutput;
        var notify = updated.NotificationPolicy == "always"
            || updated.NotificationPolicy == "failures" && status is ScheduleRunStatus.Error or ScheduleRunStatus.Cancelled
            || updated.NotificationPolicy == "changes" && changed && (status != ScheduleRunStatus.Success || output?.Trim() != "[NO_CHANGE]");
        if (notify) _runtime.Events.Publish(new ScheduledTaskCompletedEvent(
            updated.Id, updated.DisplayName, sessionId, status, updated.LastError, updated.LastOutput));
        else _runtime.Events.Publish(new ScheduledTaskUpdatedEvent());
        return updated;
    }

    private WorkspaceInfo ResolveWorkspace(ScheduledTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Workspace))
        {
            if (!Directory.Exists(task.Workspace)) throw new DirectoryNotFoundException("Scheduled workspace no longer exists.");
            return _runtime.Workspaces.Detect(task.Workspace);
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
            _runtime.ShareTaskServices(services);
        });
        turnRuntime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        turnRuntime.Skills.Attach(workspace);
        if (turnRuntime.Mcp.LoadServerConfigs(workspace).Count > 0)
            await turnRuntime.Mcp.ConnectAllAsync(workspace, ct).ConfigureAwait(false);
        return await turnRuntime.Agent.RunTurnAsync(
            session, workspace, prompt, ct, captureCrossSessionMemory: false).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] runs;
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel(); _dispose.Cancel();
            runs = _runs.Values.ToArray();
        }
        try { await Task.WhenAll(runs).ConfigureAwait(false); }
        finally { _lifetime.Dispose(); _dispose.Dispose(); _gate.Dispose(); }
    }
}
