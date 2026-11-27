using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.Execution;

/// <summary>Owns processes across agent turns. Handles are scoped to a conversation, never to a tool call.</summary>
/// <remarks>A supplied output budget selects the tool transcript and its dedicated cursor. The unbudgeted view reads the raw stream.</remarks>
public sealed class CommandSessionManager(ExecutionRecordStore? records = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CommandSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _starts = new(1, 1);
    private bool _disposed;
    public const int MaximumSessions = 32;

    public async Task<JsonObject> StartAsync(string owner, string command, string directory, int timeoutSeconds,
        bool pty = false, CancellationToken ct = default, int? maxOutputChars = null)
    {
        var session = await StartSessionAsync(owner, command, directory, timeoutSeconds, pty, keepInputOpen: true, ct)
            .ConfigureAwait(false);
        return session.Read(0, maxOutputChars);
    }

    internal async Task<JsonObject> ExecAsync(string owner, string command, string directory, int timeoutSeconds,
        bool tty, int yieldMilliseconds, CancellationToken ct, int? maxOutputChars = null)
    {
        var session = await StartSessionAsync(owner, command, directory, timeoutSeconds, tty, keepInputOpen: tty, ct)
            .ConfigureAwait(false);

        // Yielding returns the same session. Its process lifetime is independent of this wait.
        try
        {
            await session.Completion.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(yieldMilliseconds, 0, 30_000)), ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        return session.Read(0, maxOutputChars);
    }

    private async Task<CommandSession> StartSessionAsync(string owner, string command, string directory,
        int timeoutSeconds, bool pty, bool keepInputOpen, CancellationToken ct)
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
            while (_sessions.Count >= MaximumSessions)
            {
                var oldest = _sessions.Values.Where(session => session.Completion.IsCompleted)
                    .MinBy(session => session.LastAccess);
                if (oldest is null)
                    throw new InvalidOperationException("Command session limit reached; terminate an active session first.");
                if (_sessions.TryRemove(oldest.Id, out var completed)) await completed.DisposeAsync().ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            var session = new CommandSession(owner, command, directory, Math.Clamp(timeoutSeconds, 1, 86_400), pty, keepInputOpen);
            _sessions[session.Id] = session;
            if (records is not null)
            {
                try { records.Put("command", session.Id, owner, session.Metadata()); }
                catch { _sessions.TryRemove(session.Id, out _); await session.DisposeAsync().ConfigureAwait(false); throw; }
                _ = RecordCompletionAsync(session);
            }
            return session;
        }
        finally { _starts.Release(); }
    }

    public async Task<JsonObject> ReadAsync(string owner, string id, long? cursor = null, int waitMilliseconds = 0,
        CancellationToken ct = default, int? maxOutputChars = null) =>
        await ReadAsync(Get(owner, id), cursor, waitMilliseconds, maxOutputChars, ct).ConfigureAwait(false);

    private static async Task<JsonObject> ReadAsync(CommandSession session, long? cursor, int waitMilliseconds,
        int? maxOutputChars, CancellationToken ct)
    {
        if (cursor is long position) session.ValidateCursor(position, maxOutputChars.HasValue);
        if (waitMilliseconds > 0)
            await session.WaitForOutputAsync(cursor, Math.Min(waitMilliseconds, 60_000), maxOutputChars.HasValue, ct)
                .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return session.Read(cursor, maxOutputChars);
    }

    public Task<JsonObject> WriteAsync(string owner, string id, string text, bool closeInput, CancellationToken ct) =>
        WriteAsync(owner, id, text, closeInput, ct, cursor: null, waitMilliseconds: 0);

    internal async Task<JsonObject> WriteAsync(string owner, string id, string text, bool closeInput, CancellationToken ct,
        long? cursor, int waitMilliseconds, int? maxOutputChars = null)
    {
        var session = Get(owner, id);
        if (cursor is long position) session.ValidateCursor(position, maxOutputChars.HasValue);
        await session.WriteAsync(text, closeInput, ct).ConfigureAwait(false);
        return await ReadAsync(session, cursor, waitMilliseconds, maxOutputChars, ct).ConfigureAwait(false);
    }

    public async Task<JsonObject> StopAsync(string owner, string id, bool remove, CancellationToken ct,
        int? maxOutputChars = null)
    {
        var session = Get(owner, id);
        session.Cancel();
        await session.Completion.WaitAsync(ct).ConfigureAwait(false);
        var result = session.Read(null, maxOutputChars);
        if (remove && _sessions.TryRemove(id, out var removed)) await removed.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    public JsonArray List(string owner, int? maxOutputChars = null) => new(_sessions.Values.Where(s => s.Owner == owner)
        .Select(s => (JsonNode)s.Read(null, maxOutputChars, includeOutput: false)).ToArray());

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
        private readonly CommandProcess _process;
        private readonly ProcessJobScope _job;
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenRegistration _killRegistration;
        private readonly SemaphoreSlim _input = new(1, 1);
        private readonly Lock _gate = new();
        private readonly CommandOutputBuffer _rawOutput = new();
        private readonly CommandOutputBuffer _toolOutput = new();
        private readonly TerminalTranscript? _transcript;
        private long _rawCursor;
        private long _toolCursor;
        private string _status = "running";
        private string? _error;
        private bool _inputClosed;
        private bool _stopRequested;
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

        public CommandSession(string owner, string command, string directory, int timeout, bool pty, bool keepInputOpen)
        {
            Owner = owner;
            Command = command;
            _job = new ProcessJobScope();
            try { _process = CommandProcess.Start(command, directory, pty); }
            catch { _job.Dispose(); throw; }
            if (_process.Pty) _transcript = new TerminalTranscript(_toolOutput);
            _processId = _process.Process.Id;
            _processStartedAt = _process.Process.StartTime.ToUniversalTime().ToString("O");
            _directory = Path.GetFullPath(directory);
            _job.AssignProcess(_process.Process, _process.IsProcessGroupLeader);
            if (!keepInputOpen)
            {
                _process.Input.Close();
                _inputClosed = true;
            }
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
                        var chars = buffer.AsSpan(0, count);
                        _rawOutput.Append(chars);
                        _transcript?.Append(chars);
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
                lock (_gate)
                {
                    if (_timeout.IsCancellationRequested) SetCancellationStatus();
                    else { _status = "exited"; _exitCode = _process.Process.ExitCode; }
                }
            }
            catch (OperationCanceledException) { lock (_gate) SetCancellationStatus(); }
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

        private void SetCancellationStatus() => _status = _stopRequested ? "cancelled" : "timed_out";

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

        public void ValidateCursor(long cursor, bool toolView)
        {
            lock (_gate)
                if (cursor < 0 || cursor > OutputBuffer(toolView).End)
                    throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        public JsonObject Read(long? cursor, int? maxOutputChars = null, bool includeOutput = true)
        {
            lock (_gate)
            {
                LastAccess = DateTimeOffset.UtcNow;
                var toolView = maxOutputChars.HasValue;
                var buffer = OutputBuffer(toolView);
                if (includeOutput && toolView) _transcript?.PublishPrompt();
                ref var delivered = ref (toolView ? ref _toolCursor : ref _rawCursor);
                var requested = cursor ?? delivered;
                if (requested < 0 || requested > buffer.End) throw new ArgumentOutOfRangeException(nameof(cursor));
                var from = Math.Max(requested, buffer.Start);
                var until = includeOutput ? Math.Min(buffer.End, from + 16 * 1024) : buffer.End;
                var result = new JsonObject
                {
                    ["sessionId"] = Id, ["status"] = _status,
                    ["pty"] = _process.Pty, ["cursor"] = until, ["endCursor"] = buffer.End,
                    ["hasMore"] = until < buffer.End,
                    ["truncated"] = requested < buffer.Start, ["error"] = _error,
                    ["exitCode"] = _exitCode,
                };
                if (!toolView) result["command"] = Command;
                if (includeOutput)
                {
                    var page = buffer.Slice(from, until);
                    result["output"] = page;
                    while (maxOutputChars is int budget && page.Length > 0 && result.ToJsonString().Length > budget)
                    {
                        var length = page.Length / 2;
                        if (length > 0 && char.IsHighSurrogate(page[length - 1])) length--;
                        page = page[..length];
                        result["output"] = page;
                    }
                    until = from + page.Length;
                    result["cursor"] = until;
                    result["hasMore"] = until < buffer.End;
                    // Explicit reads may replay a page without rewinding implicit polling.
                    delivered = Math.Max(delivered, until);
                }
                return result;
            }
        }

        private CommandOutputBuffer OutputBuffer(bool toolView) => toolView && _transcript is not null
            ? _toolOutput
            : _rawOutput;

        public async Task WaitForOutputAsync(long? cursor, int milliseconds, bool toolView, CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    var position = cursor ?? (toolView ? _toolCursor : _rawCursor);
                    if (position < OutputBuffer(toolView).End || _status != "running"
                        || (toolView && _transcript?.HasPromptUpdate == true)) return;
                    changed = _changed.Task;
                }
                var remaining = duration - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) return;
                try { await changed.WaitAsync(remaining, ct).ConfigureAwait(false); }
                catch (TimeoutException) { return; }
            }
        }

        public JsonObject Metadata()
        {
            lock (_gate) return new JsonObject { ["id"] = Id, ["workspace"] = _directory, ["status"] = _status,
                ["processId"] = _processId, ["processStartedAt"] = _processStartedAt };
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Signal() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(); }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposed || _timeout.IsCancellationRequested) return;
                _stopRequested = true;
                _timeout.Cancel();
            }
        }

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
