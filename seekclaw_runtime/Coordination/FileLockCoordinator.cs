namespace SeekClaw.Runtime.Coordination;

/// <summary>
/// Centralized file write-lock coordinator (Task Coordinator). One instance per
/// daemon process is the single source of truth for which task currently owns a
/// write lock on a file, so concurrent agent turns cannot silently overwrite the
/// same source file. Agent write/edit tools acquire a lease before mutating a
/// file and release it afterwards; on contention the caller waits (bounded by a
/// timeout) and re-reads the latest content once the lock is granted.
/// </summary>
public interface IFileLockCoordinator
{
    /// <summary>Owner currently holding the write lock on the file, or null.</summary>
    string? GetOwner(string workspaceRoot, string filePath);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the file write lock to become free
    /// and then takes it for <paramref name="owner"/>. Returns false when the wait
    /// times out; throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> cancels before the lock is granted.
    /// </summary>
    Task<bool> TryAcquireAsync(
        string workspaceRoot,
        string filePath,
        string owner,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>Releases the lock when it is currently held by <paramref name="owner"/>.</summary>
    void Release(string workspaceRoot, string filePath, string owner);

    /// <summary>Releases every lock currently held by <paramref name="owner"/> (turn end / cancel).</summary>
    void ReleaseAll(string owner);

    /// <summary>Snapshot of the current file-to-task ownership table, for diagnostics.</summary>
    IReadOnlyList<FileLockEntry> Snapshot();
}

/// <summary>One row of the file-task ownership table.</summary>
public sealed record FileLockEntry(
    string WorkspaceRoot,
    string FilePath,
    string Owner,
    DateTimeOffset AcquiredAt);

/// <summary>Per-turn identity used by tools when taking file write locks.</summary>
public sealed record FileLockScope(string Owner);

/// <summary>In-process centralized file lock manager.</summary>
public sealed class FileLockCoordinator : IFileLockCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileLockState> _locks = new(StringComparer.Ordinal);

    public string? GetOwner(string workspaceRoot, string filePath)
    {
        var state = FindState(Key(workspaceRoot, filePath));
        return state?.Owner;
    }

    public async Task<bool> TryAcquireAsync(
        string workspaceRoot,
        string filePath,
        string owner,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var key = Key(workspaceRoot, filePath);
        var state = GetOrCreateState(key, workspaceRoot, filePath);
        if (!await state.Gate.WaitAsync(timeout, ct).ConfigureAwait(false))
            return false;

        lock (_gate)
        {
            state.Owner = owner;
            state.AcquiredAt = DateTimeOffset.UtcNow;
        }
        return true;
    }

    public void Release(string workspaceRoot, string filePath, string owner)
    {
        var key = Key(workspaceRoot, filePath);
        FileLockState? state;
        lock (_gate)
        {
            state = FindState(key);
            if (state is null || !string.Equals(state.Owner, owner, StringComparison.Ordinal))
                return;
            state.Owner = null;
        }
        state.Gate.Release();
    }

    public void ReleaseAll(string owner)
    {
        List<FileLockState> released;
        lock (_gate)
        {
            released = _locks.Values
                .Where(state => string.Equals(state.Owner, owner, StringComparison.Ordinal))
                .ToList();
            foreach (var state in released) state.Owner = null;
        }
        foreach (var state in released) state.Gate.Release();
    }

    public IReadOnlyList<FileLockEntry> Snapshot()
    {
        lock (_gate)
        {
            return _locks
                .Where(pair => pair.Value.Owner is not null)
                .Select(pair => new FileLockEntry(
                    pair.Value.WorkspaceRoot,
                    pair.Value.FilePath,
                    pair.Value.Owner!,
                    pair.Value.AcquiredAt))
                .OrderBy(entry => entry.WorkspaceRoot, StringComparer.Ordinal)
                .ThenBy(entry => entry.FilePath, StringComparer.Ordinal)
                .ToList();
        }
    }

    private FileLockState? FindState(string key)
    {
        lock (_gate)
        {
            return _locks.TryGetValue(key, out var state) ? state : null;
        }
    }

    private FileLockState GetOrCreateState(string key, string workspaceRoot, string filePath)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out var state))
            {
                state = new FileLockState { WorkspaceRoot = workspaceRoot, FilePath = filePath };
                _locks[key] = state;
            }
            return state;
        }
    }

    private static string Key(string workspaceRoot, string filePath) =>
        PathKey(workspaceRoot) + "|" + PathKey(filePath);

    private static string PathKey(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private sealed class FileLockState
    {
        public required string WorkspaceRoot { get; init; }
        public required string FilePath { get; init; }
        /// <summary>Mutated only under the coordinator gate; the semaphore guarantees exclusion.</summary>
        public string? Owner { get; set; }
        public DateTimeOffset AcquiredAt { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

/// <summary>No-op coordinator used by single-turn (CLI) runtimes where no concurrency exists.</summary>
public sealed class NoopFileLockCoordinator : IFileLockCoordinator
{
    public string? GetOwner(string workspaceRoot, string filePath) => null;
    public Task<bool> TryAcquireAsync(
        string workspaceRoot, string filePath, string owner, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(true);
    public void Release(string workspaceRoot, string filePath, string owner) { }
    public void ReleaseAll(string owner) { }
    public IReadOnlyList<FileLockEntry> Snapshot() => [];
}
