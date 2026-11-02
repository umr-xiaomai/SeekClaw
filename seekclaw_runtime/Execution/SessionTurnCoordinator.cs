using System.Security.Cryptography;
using System.Text;

namespace SeekClaw.Runtime.Execution;

/// <summary>Serializes a conversation across foreground, scheduled, and delegated turns.</summary>
public sealed class SessionTurnCoordinator(string? lockDirectory = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false); }
        catch { ReleaseReference(key, entry); throw; }
        FileStream? file = null;
        try
        {
            if (lockDirectory is not null)
            {
                Directory.CreateDirectory(lockDirectory);
                var path = Path.Combine(lockDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try { file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                    catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
                    { await Task.Delay(50, ct).ConfigureAwait(false); }
                }
            }
            return new Lease(() => { file?.Dispose(); entry.Semaphore.Release(); ReleaseReference(key, entry); });
        }
        catch { file?.Dispose(); entry.Semaphore.Release(); ReleaseReference(key, entry); throw; }
    }

    private void ReleaseReference(string key, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Users != 0) return;
            _entries.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
