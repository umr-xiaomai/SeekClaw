using System.Text;

namespace SeekClaw.Runtime.IO;

internal static class ProtocolText
{
    public const int MaximumMessageChars = 16 * 1024 * 1024;

    public static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct, int maximumChars = MaximumMessageChars)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > maximumChars) throw new IOException("Protocol response exceeds the configured text limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}

/// <summary>A buffered line reader that also bounds lines without newline delimiters.</summary>
internal sealed class BoundedLineReader(StreamReader reader, int maximumChars)
{
    private readonly char[] _buffer = new char[8192];
    private int _offset;
    private int _count;

    public async ValueTask<string?> ReadLineAsync(CancellationToken ct = default)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            }
            var end = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
            var take = (end < 0 ? _count : end) - _offset;
            if (line.Length + take > maximumChars) throw new IOException("Protocol line exceeds the configured limit.");
            line.Append(_buffer, _offset, take);
            _offset += take;
            if (end < 0) continue;
            _offset++;
            return line.ToString().TrimEnd('\r');
        }
    }
}
