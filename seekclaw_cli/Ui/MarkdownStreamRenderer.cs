using Markdig.Syntax;

namespace SeekClaw.Cli.Ui;

/// <summary>
/// Incremental Markdown renderer for in-flight assistant streams.
///
/// Completed top-level Markdown blocks are rendered once and retained; only the final
/// top-level block is re-rendered as new source arrives. This mirrors Codex's streaming
/// renderer policy without depending on the Desktop/runtime stack.
/// </summary>
public sealed class MarkdownStreamRenderer
{
    private int _width;
    private int _stableSourceLength;
    private readonly List<string> _stableLines = [];

    /// <summary>Renders the current stream and returns the newest rows that fit the live preview.</summary>
    public IReadOnlyList<string> Render (string source, int width, int maxLines)
    {
        if (maxLines <= 0) return [];
        width = Math.Max(20, width);
        if (_width != width) Reset(width);

        if (string.IsNullOrEmpty(source))
        {
            Reset(width);
            return [];
        }

        IReadOnlyList<string> rendered;
        try
        {
            var document = MarkdownAnsi.Parse(source);
            var blocks = document.ToArray();
            var boundary = blocks.Length == 0
                ? source.Length
                : ClampBoundary(source, blocks[^1].Span.Start);

            UpdateStablePrefix(source, boundary, width);
            var pending = MarkdownAnsi.Render(source[boundary..], width);
            rendered = Combine(_stableLines, pending);
        }
        catch (Exception)
        {
            rendered = Fallback(source, width);
        }

        return rendered.TakeLast(maxLines).ToArray();
    }

    public void Reset () => Reset(_width);

    private void Reset (int width)
    {
        _width = width;
        _stableSourceLength = 0;
        _stableLines.Clear();
    }

    private void UpdateStablePrefix (string source, int boundary, int width)
    {
        if (boundary > _stableSourceLength)
        {
            AppendStable(MarkdownAnsi.Render(source[_stableSourceLength..boundary], width));
            _stableSourceLength = boundary;
            return;
        }

        if (boundary >= _stableSourceLength) return;

        // The last top-level block boundary moved backwards, so a previously mutable block
        // was actually complete. Rebuild the stable prefix from scratch.
        _stableSourceLength = 0;
        _stableLines.Clear();
        if (boundary > 0)
        {
            AppendStable(MarkdownAnsi.Render(source[..boundary], width));
            _stableSourceLength = boundary;
        }
    }

    private void AppendStable (IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        if (_stableLines.Count > 0 && !IsBlank(_stableLines[^1]) && !IsBlank(lines[0]))
            _stableLines.Add("");
        _stableLines.AddRange(lines);
    }

    private static IReadOnlyList<string> Combine (IReadOnlyList<string> stable, IReadOnlyList<string> pending)
    {
        var lines = new List<string>(stable);
        if (lines.Count > 0 && pending.Count > 0 && !IsBlank(lines[^1]) && !IsBlank(pending[0]))
            lines.Add("");
        lines.AddRange(pending);
        while (lines.Count > 0 && IsBlank(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static IReadOnlyList<string> Fallback (string source, int width)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n')
            .Select(line => Ansi.TruncateVisible(line, width - 1))
            .ToArray();
        return lines;
    }

    private static int ClampBoundary (string source, int index)
    {
        if (index <= 0) return 0;
        if (index >= source.Length) return source.Length;
        if (char.IsLowSurrogate(source[index])) index--;
        return Math.Clamp(index, 0, source.Length);
    }

    private static bool IsBlank (string line) => string.IsNullOrWhiteSpace(Ansi.StripStyles(line));
}
