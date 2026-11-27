namespace SeekClaw.Cli.Ui;

/// <summary>
/// Keeps the source for committed history and the prefix of the current reply already sent to
/// the terminal. A retry or a Markdown reflow can invalidate that prefix after it has scrolled
/// off-screen. In that case, rebuild from the committed cells in the next draw.
/// </summary>
internal sealed class TerminalHistory
{
    private const int MaxReplayRows = 16_000;
    private readonly Queue<Cell> _cells = new();
    private readonly List<string> _pending = [];
    private IReadOnlyList<string> _draft = [];
    private int _draftRows;
    private CliPalette? _draftPalette;
    private int _retainedRows;
    private bool _repair;

    private sealed record Cell(IReadOnlyList<string> Rows, string? Source = null, string? WorkingDirectory = null, bool UserPrompt = false);

    internal readonly record struct Draw(IReadOnlyList<string> Rows, bool Replay = false);

    // Completed rows enter history as they arrive. Long streams can also spill
    // earlier rows of ordinary text when their live preview would exceed the screen.
    public int ShowDraft(IReadOnlyList<string> rows, int availableRows, int spillableRows, int stableRows = 0, CliPalette? palette = null)
    {
        var overflow = Math.Min(Math.Max(0, rows.Count - availableRows), spillableRows);
        var required = Math.Max(stableRows, overflow);
        var matches = MatchesDraft(rows, palette);
        var count = matches ? Math.Max(required, _draftRows) : required;
        SyncDraft(rows, count, palette, matches);
        return count;
    }

    public void CompleteAssistant(string source, IReadOnlyList<string> rows, string? workingDirectory = null, CliPalette? palette = null)
    {
        if (rows.Count > 0)
        {
            SyncDraft(rows, rows.Count, palette, MatchesDraft(rows, palette));
            Remember(new Cell(rows, source, workingDirectory));
        }
        else
        {
            DiscardDraft();
        }
        _draft = [];
        _draftRows = 0;
        _draftPalette = null;
    }

    public void DiscardDraft()
    {
        if (_draftRows > 0) _repair = true;
        _draft = [];
        _draftRows = 0;
        _draftPalette = null;
    }

    public void AddPrompt(string source, int contentWidth, CliPalette? palette = null)
    {
        var rows = PromptSurface.HistoryRows(source, contentWidth, palette ?? CliPalette.Default);
        _pending.AddRange(rows);
        Remember(new Cell(rows, source, UserPrompt: true));
    }

    public void Add(string row)
    {
        // Only assistant Markdown cells produce terminal hyperlinks.
        row = Ansi.KeepStyles(row, allowHyperlinks: false);
        _pending.Add(row);
        Remember(new Cell([row]));
    }

    public void AddRange(IEnumerable<string> rows)
    {
        foreach (var row in rows) Add(row);
    }

    public Draw TakeDraw(int assistantWidth, bool replay = false, CliPalette? palette = null)
    {
        Draw draw;
        if (_repair || replay)
        {
            var rows = new List<string>();
            foreach (var cell in _cells)
            {
                if (cell.Source is not { } source)
                    rows.AddRange(cell.Rows);
                else if (cell.UserPrompt)
                    rows.AddRange(PromptSurface.HistoryRows(source, assistantWidth, palette ?? CliPalette.Default));
                else
                    rows.AddRange(AssistantCell.Render(source, assistantWidth, cell.WorkingDirectory, palette));
            }
            if (rows.Count > MaxReplayRows) rows.RemoveRange(0, rows.Count - MaxReplayRows);
            for (var i = 0; i < _draftRows; i++) rows.Add(_draft[i]);
            draw = new Draw(rows, Replay: true);
        }
        else
        {
            draw = new Draw(_pending.ToArray());
        }
        _pending.Clear();
        _repair = false;
        return draw;
    }

    private void SyncDraft(IReadOnlyList<string> rows, int count, CliPalette? palette, bool matches)
    {
        if (!matches || count < _draftRows) _repair = true;
        if (!_repair)
            for (var i = _draftRows; i < count; i++) _pending.Add(rows[i]);
        _draft = count > 0 ? rows : [];
        _draftRows = count;
        _draftPalette = palette;
    }

    private bool MatchesDraft(IReadOnlyList<string> rows, CliPalette? palette)
    {
        if (_draftRows > rows.Count) return false;
        if (ReferenceEquals(_draft, rows)) return true;

        // A theme change may recolor the visible portion of a reply. Leave the
        // emitted prefix alone unless its content or hyperlink targets changed.
        for (var i = 0; i < _draftRows; i++)
        {
            if (string.Equals(_draft[i], rows[i], StringComparison.Ordinal)) continue;
            if (_draftPalette == palette || !string.Equals(Ansi.StripSgr(_draft[i]), Ansi.StripSgr(rows[i]), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void Remember(Cell cell)
    {
        _cells.Enqueue(cell);
        _retainedRows += cell.Rows.Count;
        while (_retainedRows > MaxReplayRows && _cells.Count > 1)
            _retainedRows -= _cells.Dequeue().Rows.Count;
    }
}

internal static class AssistantCell
{
    public static IReadOnlyList<string> Render(string source, int width, string? workingDirectory = null, CliPalette? palette = null) =>
        Format(MarkdownAnsi.Render(source, width, workingDirectory, palette));

    public static IReadOnlyList<string> Format(IReadOnlyList<string> body)
    {
        if (body.Count == 0) return [];
        var rows = new string[body.Count + 1];
        rows[0] = "";
        for (var i = 0; i < body.Count; i++)
            rows[i + 1] = (i == 0 ? "• ".Style(Ansi.Dim) : "  ") + body[i];
        return rows;
    }
}
