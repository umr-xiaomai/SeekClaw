using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>
/// Double-buffered bottom-of-screen live area. Finalized content scrolls above it;
/// the live area itself is redrawn in place each frame (no flicker, no log spam).
/// Each frame is emitted as a single console write.
/// </summary>
public sealed class LiveRegion(TextWriter output)
{
    private int _liveLines;
    private int _cursorRowsBelow;
    private string _lastFrame = "";

    private static int Width
    {
        get
        {
            try { return Math.Max(20, Console.WindowWidth); }
            catch (IOException) { return 120; }
        }
    }

    /// <summary>Pushes finalized lines into scrollback and redraws the live area beneath them.</summary>
    public void Update(IReadOnlyList<string>? scrollback, IReadOnlyList<string> live, int cursorRowsBelow = 0, int cursorColumn = 0)
    {
        var width = Width;
        var frame = new StringBuilder();

        EraseLive(frame);

        // Scrollback lines may wrap naturally — they scroll away and never need repositioning.
        if (scrollback is { Count: > 0 })
            foreach (var line in scrollback)
                frame.Append(line).Append('\n');

        var trimmed = live.Select(l => Ansi.TruncateVisible(l, width - 1)).ToList();
        frame.Append(string.Join('\n', trimmed));

        _liveLines = trimmed.Count;
        _cursorRowsBelow = Math.Clamp(cursorRowsBelow, 0, Math.Max(0, _liveLines - 1));
        if (_cursorRowsBelow > 0) frame.Append("\x1b[").Append(_cursorRowsBelow).Append('A');
        if (_cursorRowsBelow > 0 || cursorColumn > 0) frame.Append('\r');
        if (cursorColumn > 0) frame.Append("\x1b[").Append(cursorColumn).Append('C');

        var rendered = frame.ToString();
        var hadScrollback = scrollback is { Count: > 0 };
        if (!hadScrollback && rendered == _lastFrame) return; // nothing changed — skip the write

        output.Write(rendered);
        output.Flush();
        _lastFrame = hadScrollback ? "" : rendered;
    }

    /// <summary>Clears the live area (turn finished or app exiting).</summary>
    public void Clear()
    {
        var frame = new StringBuilder();
        EraseLive(frame);
        output.Write(frame.ToString());
        output.Flush();
        _liveLines = 0;
        _cursorRowsBelow = 0;
        _lastFrame = "";
    }

    private void EraseLive(StringBuilder frame)
    {
        frame.Append('\r');
        var rowsToTop = _liveLines - _cursorRowsBelow - 1;
        if (rowsToTop > 0) frame.Append("\x1b[").Append(rowsToTop).Append('A');
        frame.Append("\x1b[0J");
    }
}
