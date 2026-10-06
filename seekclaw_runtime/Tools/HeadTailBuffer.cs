using System.Text;

namespace SeekClaw.Runtime.Tools;

/// <summary>
/// A capped buffer that preserves a stable prefix ("head") and suffix ("tail"),
/// dropping the middle once it exceeds the configured maximum.
/// Inspired by OpenAI Codex HeadTailBuffer to prevent stdout/stderr pipe buffer deadlocks.
/// </summary>
public sealed class HeadTailBuffer
{
    private readonly int _maxBytes;
    private readonly int _headBudget;
    private readonly int _tailBudget;
    private readonly MemoryStream _head = new();
    private readonly LinkedList<byte[]> _tailChunks = new();
    private int _tailBytes;
    private long _omittedBytes;
    private readonly object _lock = new();

    public HeadTailBuffer(int maxBytes = 128 * 1024)
    {
        _maxBytes = Math.Max(16, maxBytes);
        _headBudget = _maxBytes / 2;
        _tailBudget = _maxBytes - _headBudget;
    }

    public long TotalBytesObserved
    {
        get
        {
            lock (_lock)
            {
                return _head.Length + _tailBytes + _omittedBytes;
            }
        }
    }

    public long OmittedBytes
    {
        get
        {
            lock (_lock)
            {
                return _omittedBytes;
            }
        }
    }

    public void PushChunk(byte[] chunk, int offset, int count)
    {
        if (chunk == null || count <= 0) return;

        lock (_lock)
        {
            // 1. Fill head budget first
            var remainingHead = _headBudget - (int)_head.Length;
            if (remainingHead > 0)
            {
                var toWrite = Math.Min(remainingHead, count);
                _head.Write(chunk, offset, toWrite);
                offset += toWrite;
                count -= toWrite;
            }

            if (count <= 0) return;

            // 2. Add remaining to tail
            var copy = new byte[count];
            Buffer.BlockCopy(chunk, offset, copy, 0, count);
            _tailChunks.AddLast(copy);
            _tailBytes += count;

            // 3. Drop oldest tail chunks to maintain tail budget
            while (_tailBytes > _tailBudget && _tailChunks.First != null)
            {
                var first = _tailChunks.First.Value;
                var excess = _tailBytes - _tailBudget;
                if (first.Length <= excess)
                {
                    _tailBytes -= first.Length;
                    _omittedBytes += first.Length;
                    _tailChunks.RemoveFirst();
                }
                else
                {
                    var keep = first.Length - excess;
                    var trimmed = new byte[keep];
                    Buffer.BlockCopy(first, excess, trimmed, 0, keep);
                    _tailChunks.First.Value = trimmed;
                    _tailBytes -= excess;
                    _omittedBytes += excess;
                    break;
                }
            }
        }
    }

    public string GetFormattedText(Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;

        lock (_lock)
        {
            var headStr = encoding.GetString(_head.ToArray());
            if (_omittedBytes == 0) return headStr;

            var tailBytesArray = new byte[_tailBytes];
            var offset = 0;
            foreach (var chunk in _tailChunks)
            {
                Buffer.BlockCopy(chunk, 0, tailBytesArray, offset, chunk.Length);
                offset += chunk.Length;
            }
            var tailStr = encoding.GetString(tailBytesArray);

            return $"{headStr}\n\n[... {_omittedBytes:N0} bytes omitted ...]\n\n{tailStr}";
        }
    }
}
