using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Runtime.SubAgents;

/// <summary>Durable task identity and independently controlled in-process execution.</summary>
public sealed class SubAgentTaskManager(ExecutionRecordStore records, IEventBus? events = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RunningTask> _running = new(StringComparer.Ordinal);
    private readonly SessionTurnCoordinator _operations = new(records.LockDirectory);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _state = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private bool _disposed;

    public JsonObject Get(string parent, string id)
    {
        var record = records.Get("subagent", id);
        if (record is null || record["parent"]?.GetValue<string>() != parent)
            throw new KeyNotFoundException("Sub-agent task not found in this conversation.");
        if (record["status"]?.GetValue<string>() == "running" && !ProcessIdentity.IsAlive(record))
        {
            record["status"] = "interrupted";
            records.Put("subagent", id, parent, record);
        }
        return record;
    }

    public JsonArray List(string parent) => new(records.List("subagent", parent)
        .Select(r => (JsonNode)Get(parent, r["id"]!.GetValue<string>())).ToArray());

    public async Task<JsonObject> StartAsync(string parent, JsonObject record,
        Func<CancellationToken, Task<AgentTurnResult>> run, CancellationToken ct)
    {
        var id = record["id"]!.GetValue<string>();
        using var lease = await _operations.AcquireAsync(id, ct).ConfigureAwait(false);
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (records.Get("subagent", id) is { } existing && existing["parent"]?.GetValue<string>() != parent)
                throw new InvalidOperationException("Sub-agent task belongs to another conversation.");
            if (_running.TryGetValue(id, out var active) && !active.Task.IsCompleted)
                throw new InvalidOperationException("Sub-agent is already running; wait or cancel it before sending another instruction.");
            if (_running.TryRemove(id, out var previous)) previous.Cancellation.Dispose();
            if (records.Get("subagent", id) is { } stored && stored["status"]?.GetValue<string>() == "running"
                && ProcessIdentity.IsAlive(stored) && stored["instance"]?.GetValue<string>() != _instance)
                throw new InvalidOperationException("Sub-agent is still running in another runtime.");
            if (!_slots.Wait(0))
                throw new InvalidOperationException("Sub-agent concurrency limit reached.");
            record = (JsonObject)record.DeepClone();
            record["parent"] = parent; record["status"] = "running";
            record["instance"] = _instance; record["processId"] = Environment.ProcessId;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                record["processStartedAt"] = process.StartTime.ToUniversalTime().ToString("O");
            record["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"); record.Remove("error");
            try { records.Put("subagent", id, parent, record); }
            catch { _slots.Release(); throw; }
            var snapshot = (JsonObject)record.DeepClone();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var running = new RunningTask(cancellation);
            _running[id] = running;
            // Schedule after registration so status/cancel always observe a complete handle.
            running.Task = Task.Run(async () =>
            {
                try
                {
                    var result = await run(cancellation.Token).ConfigureAwait(false);
                    record["status"] = result.Cancelled ? "cancelled" : result.Error is null ? "completed" : "failed";
                    record["output"] = result.Text; record["error"] = result.Error;
                    if (result.Maintenance is { } maintenance) await maintenance.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { record["status"] = "cancelled"; }
                catch (Exception ex) { record["status"] = "failed"; record["error"] = ex.Message; }
                finally
                {
                    record["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
                    try { records.Put("subagent", id, parent, record); }
                    finally
                    {
                        _slots.Release();
                        events?.Publish(new SubAgentTaskChangedEvent(parent, id, record["status"]!.GetValue<string>()));
                    }
                }
            }, CancellationToken.None);
            return snapshot;
        }
    }

    public async Task<JsonObject> WaitAsync(string parent, string id, int milliseconds, CancellationToken ct)
    {
        Get(parent, id); // Scope check applies even when the task is live.
        if (_running.TryGetValue(id, out var running))
            await Task.WhenAny(running.Task, Task.Delay(Math.Clamp(milliseconds, 0, 60_000), ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return Get(parent, id);
    }

    public async Task<JsonObject> CancelAsync(string parent, string id, CancellationToken ct)
    {
        var record = Get(parent, id);
        RunningTask? running;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _running.TryGetValue(id, out running);
            running?.Cancellation.Cancel();
        }
        if (running is not null)
        {
            await running.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        else if (record["status"]?.GetValue<string>() == "running")
            throw new InvalidOperationException("Sub-agent is owned by another running runtime.");
        return Get(parent, id);
    }

    public async ValueTask DisposeAsync()
    {
        RunningTask[] tasks;
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
            tasks = _running.Values.ToArray();
        }
        try { await Task.WhenAll(tasks.Select(x => x.Task)).ConfigureAwait(false); }
        finally
        {
            foreach (var run in tasks) run.Cancellation.Dispose();
            _running.Clear(); _lifetime.Dispose(); _slots.Dispose();
        }
    }
    private sealed class RunningTask(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
