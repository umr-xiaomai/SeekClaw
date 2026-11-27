using System.Globalization;
using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>Owns the terminal rows for transient output beneath committed scrollback.</summary>
public sealed class LiveRegion(TextWriter output)
{
    private string[] _lines = [];
    private TerminalSize _size;
    private int _cursorRow;
    private int _cursorColumn;
    private CliPalette? _palette;

    public void Update(IReadOnlyList<string>? scrollback, IReadOnlyList<string> live, int cursorRowsBelow = 0, int cursorColumn = 0) =>
        Update(scrollback, live, TerminalSize.Read(), cursorRowsBelow, cursorColumn);

    internal void Update(IReadOnlyList<string>? scrollback, IReadOnlyList<string> live, TerminalSize size,
        int cursorRowsBelow = 0, int cursorColumn = 0, bool replaceHistory = false, CliPalette? palette = null)
    {
        var cursorRow = Math.Max(0, live.Count - 1 - Math.Max(0, cursorRowsBelow));
        // Leave one screen row free and keep the edit cursor in view even on a short terminal.
        var rowCount = Math.Min(live.Count, Math.Max(1, size.Height - 1));
        var firstRow = Math.Min(cursorRow, live.Count - rowCount);
        cursorRow -= firstRow;
        cursorColumn = Math.Clamp(cursorColumn, 0, Math.Max(0, size.Width - 1));
        var lines = new string[rowCount];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = Ansi.KeepStyles(live[firstRow + i]).Replace('\r', ' ').Replace('\n', ' ').Replace("\t", "    ");
            // Reserving the last column avoids the terminal's automatic line wrapping.
            lines[i] = Ansi.TruncateVisible(line, size.Width - 1);
        }

        var frame = new StringBuilder();
        var paletteChanged = palette != _palette;
        var synchronized = replaceHistory || _lines.Length > 0 && (paletteChanged || size != _size || scrollback is { Count: > 0 });
        if (replaceHistory)
        {
            // Once a draft has scrolled off-screen, repairing it requires replaying
            // the transcript. Do that and repaint the composer in one synchronized draw.
            frame.Append("\x1b[H\x1b[2J\x1b[3J");
            _lines = [];
            _cursorRow = 0;
            _cursorColumn = 0;
        }

        if (scrollback is not { Count: > 0 } && size == _size)
        {
            UpdateLiveRows(frame, lines, cursorRow, cursorColumn, palette, paletteChanged);
        }
        else
        {
            var promoted = !paletteChanged && size == _size && scrollback is not null ? PromotableRows(scrollback) : 0;
            if (promoted > 0 && promoted == scrollback!.Count && promoted < _lines.Length)
            {
                // An unchanged live prefix becomes part of history without touching its
                // cells. Compare subsequent edits with the remaining live rows.
                _lines = _lines[promoted..];
                _cursorRow -= promoted;
                UpdateLiveRows(frame, lines, cursorRow, cursorColumn, palette, paletteChanged);
            }
            else
            {
                if (promoted == _lines.Length && promoted > 0)
                {
                    MoveToRow(frame, _cursorRow, promoted - 1);
                    frame.Append('\n');
                }
                else
                {
                    EraseLive(frame, fromRow: promoted, resizedWidth: size.Width);
                }

                if (scrollback is not null)
                {
                    for (var i = promoted; i < scrollback.Count; i++)
                        AppendHistoryLine(frame, scrollback[i], palette);
                }

                if (lines.Length > 0)
                {
                    frame.Append(string.Join("\r\n", palette is null ? lines : lines.Select(palette.PaintRow)));
                    MoveCursor(frame, lines.Length - 1, cursorRow, cursorColumn);
                }
            }
        }

        Write(frame, synchronized);
        _lines = lines;
        _cursorRow = cursorRow;
        _cursorColumn = cursorColumn;
        _size = size;
        _palette = palette;
    }

    internal bool CanEraseAfterResize(TerminalSize size)
    {
        if (size == _size || _lines.Length == 0) return true;
        if (size.Height < _lines.Length) return false;
        if (!OperatingSystem.IsWindows() || size.Width >= _size.Width) return true;

        // ConPTY can already have wrapped or scrolled part of the old live area.
        // Erasing from a guessed row can overwrite a committed prompt above it.
        return _lines.All(line => WrappedRows(line, size.Width, int.MaxValue) == 1);
    }

    public void Clear()
    {
        var frame = new StringBuilder();
        EraseLive(frame);
        Write(frame);
        _lines = [];
        _cursorRow = 0;
        _cursorColumn = 0;
    }

    private void UpdateLiveRows(StringBuilder frame, string[] lines, int cursorRow, int cursorColumn,
        CliPalette? palette, bool paletteChanged)
    {
        var row = _cursorRow;
        for (var i = 0; i < Math.Max(lines.Length, _lines.Length); i++)
        {
            var current = i < lines.Length ? lines[i] : "";
            if (i < _lines.Length && !paletteChanged)
            {
                if (current == _lines[i]) continue;
                if (TryAppendToRow(frame, row, i, _lines[i], current, palette))
                {
                    row = i;
                    continue;
                }
            }

            if (i >= _lines.Length && i > 0)
            {
                // A newline allocates a row even when the live region touches the bottom.
                MoveToRow(frame, row, i - 1);
                frame.Append('\n');
            }
            else
            {
                MoveToRow(frame, row, i);
            }
            frame.Append("\x1b[2K");
            if (i < lines.Length) frame.Append(palette?.PaintRow(current) ?? current);
            row = i;
        }

        if (frame.Length > 0 || cursorRow != _cursorRow || cursorColumn != _cursorColumn)
            MoveCursor(frame, row, cursorRow, cursorColumn);
    }

    private static bool TryAppendToRow(StringBuilder frame, int cursorRow, int row, string previous, string current,
        CliPalette? palette)
    {
        if (previous.Length == 0 || current.Length <= previous.Length ||
            !current.StartsWith(previous, StringComparison.Ordinal)) return false;

        // An append can reuse the existing cells when the new text starts a fresh
        // grapheme and the previous row left the terminal in the default style.
        var lastEscape = previous.LastIndexOf('\x1b');
        if (lastEscape >= 0 && !previous.AsSpan(lastEscape).StartsWith(Ansi.Reset, StringComparison.Ordinal))
            return false;
        var previousText = Ansi.StripStyles(previous);
        var currentText = Ansi.StripStyles(current);
        if (Array.BinarySearch(StringInfo.ParseCombiningCharacters(currentText), previousText.Length) < 0)
            return false;

        MoveCursor(frame, cursorRow, row, TextWidth.Of(previousText));
        if (palette is null) frame.Append(current.AsSpan(previous.Length));
        else frame.Append(palette.PaintRow(current[previous.Length..]));
        return true;
    }

    private int PromotableRows(IReadOnlyList<string> scrollback)
    {
        // Commit identical preview rows in place. Painting them again can make an
        // otherwise unchanged answer flash when the provider sends completion.
        var count = Math.Min(_lines.Length, scrollback.Count);
        var index = 0;
        while (index < count && _lines[index] == HistoryLine(scrollback[index])) index++;
        return index;
    }

    private static string HistoryLine(string line) =>
        Ansi.KeepStyles(line).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private static void AppendHistoryLine(StringBuilder frame, string line, CliPalette? palette)
    {
        var text = HistoryLine(line);
        if (palette is null) frame.Append(text).Append("\r\n");
        else
            foreach (var row in text.Split("\r\n"))
                frame.Append(palette.PaintRow(row)).Append("\r\n");
    }

    private void EraseLive(StringBuilder frame, int fromRow = 0, int? resizedWidth = null)
    {
        if (_lines.Length == 0) return;
        var cursorRow = _cursorRow;
        var lastRow = _lines.Length - 1;
        if (OperatingSystem.IsWindows() && resizedWidth is > 0 && resizedWidth < _size.Width)
        {
            // ConPTY reflows the existing console buffer before the client sees the resize.
            // Count the extra rows still on screen so the old draft is fully erased.
            cursorRow = 0;
            var rowCount = 0;
            for (var i = 0; i < _lines.Length; i++)
            {
                if (i == _cursorRow)
                    cursorRow = rowCount + WrappedRows(_lines[i], resizedWidth.Value, _cursorColumn) - 1;
                rowCount += WrappedRows(_lines[i], resizedWidth.Value, int.MaxValue);
            }
            lastRow = rowCount - 1;
        }

        MoveToRow(frame, cursorRow, fromRow);
        frame.Append("\x1b[2K");
        for (var i = fromRow + 1; i <= lastRow; i++) frame.Append("\x1b[1B\x1b[2K");
        MoveToRow(frame, lastRow, fromRow);
    }

    private static int WrappedRows(string line, int width, int maxColumns)
    {
        var plain = Ansi.StripStyles(line);
        var rows = 1;
        var column = 0;
        var elements = StringInfo.GetTextElementEnumerator(plain);
        while (elements.MoveNext())
        {
            var cells = Math.Min(width, TextWidth.OfElement(elements.GetTextElement()));
            if (cells > maxColumns) break;
            if (column + cells > width)
            {
                rows++;
                column = 0;
            }
            column += cells;
            maxColumns -= cells;
        }
        return rows;
    }

    private static void MoveCursor(StringBuilder frame, int row, int targetRow, int column)
    {
        MoveToRow(frame, row, targetRow);
        if (column > 0) frame.Append("\x1b[").Append(column).Append('C');
    }

    private static void MoveToRow(StringBuilder frame, int row, int targetRow)
    {
        frame.Append('\r');
        var distance = targetRow - row;
        if (distance != 0)
            frame.Append("\x1b[").Append(Math.Abs(distance)).Append(distance > 0 ? 'B' : 'A');
    }

    private void Write(StringBuilder frame, bool synchronized = false)
    {
        if (frame.Length == 0) return;
        if (synchronized) frame.Insert(0, "\x1b[?2026h").Append("\x1b[?2026l");
        output.Write(frame.ToString());
        output.Flush();
    }
}
