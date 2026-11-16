using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Browser;

/// <summary>Reverse RPC on one authenticated local IPC connection; replies cannot cross clients.</summary>
internal sealed class DesktopBrowserBridge(
    Func<string, JsonObject, Task> send,
    TimeSpan? timeout = null) : IBrowserBridge, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _disconnected = new();
    public bool Enabled { get; set; }

    public async Task<JsonObject> ExecuteAsync(string scope, string sessionId, JsonObject command, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Open SeekClaw Desktop to use the conversation browser.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _disconnected.Token);
        lifetime.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            await send("browser.request", new JsonObject
            {
                ["requestId"] = id, ["scope"] = scope, ["sessionId"] = sessionId,
                ["command"] = command.DeepClone(),
            }).WaitAsync(lifetime.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(_disconnected.IsCancellationRequested
                ? "Desktop browser disconnected. The action was not retried."
                : "Desktop browser timed out. Inspect the page before retrying any action.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
            if (!completion.Task.IsCompleted)
            {
                // Best effort: do not delay cancellation on an unavailable Desktop.
                _ = NotifyCancelledAsync(id);
            }
        }
    }

    private async Task NotifyCancelledAsync(string id)
    {
        try { await send("browser.cancel", new JsonObject { ["requestId"] = id }).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    public bool Complete(string id, JsonObject result)
        => _pending.TryRemove(id, out var pending) && pending.TrySetResult(result.DeepClone().AsObject());

    public void Dispose()
    {
        Enabled = false;
        _disconnected.Cancel();
        foreach (var item in _pending.Values) item.TrySetException(new IOException("Desktop browser disconnected."));
        _pending.Clear();
        // In-flight calls may still inspect the source; it lives with this connection.
    }
}
