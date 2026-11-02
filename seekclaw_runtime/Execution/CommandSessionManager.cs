using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.Execution;

/// <summary>Owns processes across agent turns. Handles are scoped to a conversation, never to a tool call.</summary>
public sealed class CommandSessionManager(ExecutionRecordStore? records = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CommandSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _starts = new(1, 1);
    private bool _disposed;
    public const int MaximumSessions = 32;

    public async Task<JsonObject> StartAsync(string owner, string command, string directory, int timeoutSeconds,
        bool pty = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        await _starts.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var item in _sessions.Where(x => x.Value.Completion.IsCompleted
                         && DateTimeOffset.UtcNow - x.Value.LastAccess > TimeSpan.FromMinutes(15)))
                if (_sessions.TryRemove(item.Key, out var expired)) await expired.DisposeAsync().ConfigureAwait(false);
            if (_sessions.Count >= MaximumSessions)
                throw new InvalidOperationException("Command session limit reached; close a completed session first.");
            var session = new CommandSession(owner, command, directory, Math.Clamp(timeoutSeconds, 1, 86_400), pty);
            _sessions[session.Id] = session;
            if (records is not null)
            {
                try { records.Put("command", session.Id, owner, session.Metadata()); }
                catch { _sessions.TryRemove(session.Id, out _); await session.DisposeAsync().ConfigureAwait(false); throw; }
                _ = RecordCompletionAsync(session);
            }
            return session.Read(0);
        }
        finally { _starts.Release(); }
    }

    public async Task<JsonObject> ReadAsync(string owner, string id, long? cursor = null, int waitMilliseconds = 0,
        CancellationToken ct = default)
    {
        var session = Get(owner, id);
        if (waitMilliseconds > 0)
            await session.WaitForOutputAsync(cursor, Math.Clamp(waitMilliseconds, 0, 60_000), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return session.Read(cursor);
    }

    public async Task<JsonObject> WriteAsync(string owner, string id, string text, bool closeInput, CancellationToken ct)
    {
        var session = Get(owner, id);
        await session.WriteAsync(text, closeInput, ct).ConfigureAwait(false);
        return session.Read(null);
    }

    public async Task<JsonObject> StopAsync(string owner, string id, bool remove, CancellationToken ct)
    {
        var session = Get(owner, id);
        session.Cancel();
        await session.Completion.WaitAsync(ct).ConfigureAwait(false);
        var result = session.Read(null);
        if (remove && _sessions.TryRemove(id, out var removed)) await removed.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    public JsonArray List(string owner) => new(_sessions.Values.Where(s => s.Owner == owner)
        .Select(s => (JsonNode)s.Read(null, includeOutput: false)).ToArray());

    private CommandSession Get(string owner, string id) => _sessions.TryGetValue(id, out var session)
        && session.Owner == owner ? session : throw new KeyNotFoundException("Command session not found in this conversation.");

    private async Task RecordCompletionAsync(CommandSession session)
    {
        await session.Completion.ConfigureAwait(false);
        try { records!.Put("command", session.Id, session.Owner, session.Metadata()); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { /* A dead process cannot hold an archive lease. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _starts.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions.Values) session.Cancel();
            foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
            _sessions.Clear();
        }
        finally { _starts.Release(); }
    }

    private sealed class CommandSession : IAsyncDisposable
    {
        private const int OutputLimit = 128 * 1024;
        private readonly CommandProcess _process;
        private readonly ProcessJobScope _job = new();
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenRegistration _killRegistration;
        private readonly SemaphoreSlim _input = new(1, 1);
        private readonly Lock _gate = new();
        private readonly StringBuilder _output = new();
        private long _start;
        private long _end;
        private long _cursor;
        private string _status = "running";
        private string? _error;
        private bool _inputClosed;
        private bool _disposed;
        private int? _exitCode;
        private readonly int _processId;
        private readonly string _processStartedAt;
        private readonly string _directory;
        private TaskCompletionSource _changed = NewSignal();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Owner { get; }
        public string Command { get; }
        public DateTimeOffset LastAccess { get; private set; } = DateTimeOffset.UtcNow;
        public Task Completion { get; }

        public CommandSession(string owner, string command, string directory, int timeout, bool pty)
        {
            Owner = owner;
            Command = command;
            _process = CommandProcess.Start(command, directory, pty);
            _processId = _process.Process.Id;
            _processStartedAt = _process.Process.StartTime.ToUniversalTime().ToString("O");
            _directory = Path.GetFullPath(directory);
            _job.AssignProcess(_process.Process, _process.IsProcessGroupLeader);
            _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            _killRegistration = _timeout.Token.Register(() => _job.Terminate(_process.Process));
            Completion = ObserveAsync();
        }

        private async Task PumpAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096, leaveOpen: true);
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), _timeout.Token).ConfigureAwait(false)) > 0)
                {
                    lock (_gate)
                    {
                        _output.Append(buffer, 0, count);
                        _end += count;
                        if (_output.Length > OutputLimit)
                        {
                            var discarded = _output.Length - OutputLimit;
                            _output.Remove(0, discarded);
                            _start += discarded;
                        }
                        Signal();
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }

        private async Task ObserveAsync()
        {
            var stdout = PumpAsync(_process.Output);
            var stderr = _process.Error is null ? Task.CompletedTask : PumpAsync(_process.Error);
            try
            {
                await _process.Process.WaitForExitAsync(_timeout.Token).ConfigureAwait(false);
                // A surviving descendant must not retain output handles indefinitely.
                _process.ClosePseudoConsole();
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lock (_gate) { _status = "exited"; _exitCode = _process.Process.ExitCode; }
            }
            catch (OperationCanceledException) { lock (_gate) _status = "cancelled"; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            { lock (_gate) { _status = "error"; _error = ex.Message; } }
            finally
            {
                _job.Terminate(_process.Process);
                _process.ClosePseudoConsole();
                await _killRegistration.DisposeAsync().ConfigureAwait(false);
                _timeout.Cancel();
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                lock (_gate) Signal();
            }
        }

        public async Task WriteAsync(string text, bool close, CancellationToken ct)
        {
            await _input.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || Completion.IsCompleted || _inputClosed) throw new InvalidOperationException("Command stdin is closed.");
                if (_process.Pty && OperatingSystem.IsWindows()) text = text.Replace("\r\n", "\r").Replace('\n', '\r');
                var bytes = Encoding.UTF8.GetBytes(text);
                await _process.Input.WriteAsync(bytes, ct).ConfigureAwait(false);
                await _process.Input.FlushAsync(ct).ConfigureAwait(false);
                if (close) { _process.Input.Close(); _inputClosed = true; }
            }
            finally { _input.Release(); }
        }

        public JsonObject Read(long? cursor, bool includeOutput = true)
        {
            lock (_gate)
            {
                LastAccess = DateTimeOffset.UtcNow;
                var requested = cursor ?? _cursor;
                if (requested < 0 || requested > _end) throw new ArgumentOutOfRangeException(nameof(cursor));
                var from = Math.Max(requested, _start);
                var until = includeOutput ? Math.Min(_end, from + 16 * 1024) : _end;
                var result = new JsonObject
                {
                    ["sessionId"] = Id, ["command"] = Command, ["status"] = _status,
                    ["pty"] = _process.Pty, ["cursor"] = until, ["endCursor"] = _end,
                    ["hasMore"] = until < _end,
                    ["truncated"] = requested < _start, ["error"] = _error,
                    ["exitCode"] = _exitCode,
                };
                if (includeOutput) { result["output"] = _output.ToString((int)(from - _start), (int)(until - from)); _cursor = until; }
                return result;
            }
        }

        public async Task WaitForOutputAsync(long? cursor, int milliseconds, CancellationToken ct)
        {
            Task changed;
            lock (_gate)
            {
                if ((cursor ?? _cursor) < _end || _status != "running") return;
                changed = _changed.Task;
            }
            await Task.WhenAny(changed, Task.Delay(milliseconds, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        public JsonObject Metadata()
        {
            lock (_gate) return new JsonObject { ["id"] = Id, ["workspace"] = _directory, ["status"] = _status,
                ["processId"] = _processId, ["processStartedAt"] = _processStartedAt };
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Signal() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(); }

        public void Cancel() { lock (_gate) { if (!_disposed) _timeout.Cancel(); } }

        public async ValueTask DisposeAsync()
        {
            Cancel();
            await Completion.ConfigureAwait(false);
            await _killRegistration.DisposeAsync().ConfigureAwait(false);
            await _input.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _process.Dispose(); _job.Dispose(); _timeout.Dispose();
                }
            }
            finally { _input.Release(); }
        }
    }
}
