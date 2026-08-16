using SeekClaw.Runtime.Agents;
using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>A REPL slash command surfaced in the completion menu.</summary>
public sealed record SlashCommand (string Name, string ArgsHint, string Description, bool SubmitsOnSelect);

/// <summary>Immutable snapshot of the editable input area for the shared terminal renderer.</summary>
public sealed record LineEditorFrame (IReadOnlyList<string> Lines, int CursorRowsBelow, int CursorColumn);

/// <summary>
/// Interactive line editor with a Claude-Code-style slash command menu:
/// typing "/" opens a filtered command list under the input line
/// (↑/↓ select · tab complete · enter run · esc dismiss), plus input history.
/// CJK-aware cursor math; long input scrolls horizontally within one row.
/// </summary>
public sealed class LineEditor (
    IReadOnlyList<SlashCommand> commands,
    List<string> history,
    Func<string>? getModeText = null,
    Action<LineEditorFrame?>? setFrame = null,
    Action<string>? submitLine = null)
{
    private const int MaxMenuRows = 8;
    private const string Prompt = "❯ ";
    private const string Continuation = "  ";
    private const string Placeholder = "type a message · / for commands · ctrl+j newline";

    private readonly StringBuilder _buffer = new();
    private int _cursor;               // char index into _buffer
    private int _selected;
    private bool _menuSuppressed;
    private string _lastFilter = "";
    private int _historyIndex;
    private string _draft = "";
    private string _kill = "";

    /// <summary>Reads one line; returns null on Ctrl+C/Ctrl+D with an empty buffer (caller exits).</summary>
    public string? ReadLine ()
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(Prompt);
            return Console.In.ReadLine();
        }

        _buffer.Clear();
        _cursor = 0;
        _selected = 0;
        _menuSuppressed = false;
        _lastFilter = "";
        _historyIndex = history.Count;
        _draft = "";

        var previousTreatCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            Render();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var submitted = HandleKey(key, out var result);
                if (submitted)
                {
                    Finish();
                    return result;
                }
                Render();
            }
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatCtrlC;
        }
    }

    // ---------------------------------------------------------------- key handling

    private bool HandleKey (ConsoleKeyInfo key, out string? result)
    {
        result = null;
        var menu = CurrentMenu();

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (menu.Count > 0)
                {
                    var command = menu[Math.Clamp(_selected, 0, menu.Count - 1)];
                    if (command.SubmitsOnSelect)
                    {
                        SetBuffer(command.Name);
                        result = command.Name;
                        return true;
                    }
                    SetBuffer(command.Name + " ");
                    return false;
                }
                result = _buffer.ToString();
                return true;

            case ConsoleKey.Tab when menu.Count > 0:
            {
                var command = menu[Math.Clamp(_selected, 0, menu.Count - 1)];
                SetBuffer(command.ArgsHint.Length > 0 ? command.Name + " " : command.Name);
                return false;
            }

            case ConsoleKey.A when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _cursor = 0;
                return false;

            case ConsoleKey.E when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _cursor = _buffer.Length;
                return false;

            case ConsoleKey.K when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_cursor < _buffer.Length)
                {
                    _kill = _buffer.ToString(_cursor, _buffer.Length - _cursor);
                    _buffer.Remove(_cursor, _buffer.Length - _cursor);
                    OnBufferChanged();
                }
                return false;

            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_cursor > 0)
                {
                    _kill = _buffer.ToString(0, _cursor);
                    _buffer.Remove(0, _cursor);
                    _cursor = 0;
                    OnBufferChanged();
                }
                return false;

            case ConsoleKey.W when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                DeleteWordBackward();
                return false;

            case ConsoleKey.Y when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                InsertKill();
                return false;

            case ConsoleKey.J when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _buffer.Insert(_cursor, '\n');
                _cursor++;
                OnBufferChanged();
                return false;

            case ConsoleKey.LeftArrow when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                MoveWordBackward();
                return false;

            case ConsoleKey.RightArrow when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                MoveWordForward();
                return false;

            case ConsoleKey.UpArrow:
                if (menu.Count > 0)
                    _selected = (_selected - 1 + menu.Count) % menu.Count;
                else
                    HistoryUp();
                return false;

            case ConsoleKey.DownArrow:
                if (menu.Count > 0)
                    _selected = (_selected + 1) % menu.Count;
                else
                    HistoryDown();
                return false;

            case ConsoleKey.Escape:
                if (menu.Count > 0)
                    _menuSuppressed = true;
                else
                    SetBuffer("");
                return false;

            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    var prev = PrevIndex(_cursor);
                    _buffer.Remove(prev, _cursor - prev);
                    _cursor = prev;
                    OnBufferChanged();
                }
                return false;

            case ConsoleKey.Delete:
                if (_cursor < _buffer.Length)
                {
                    _buffer.Remove(_cursor, NextIndex(_cursor) - _cursor);
                    OnBufferChanged();
                }
                return false;

            case ConsoleKey.LeftArrow:
                if (_cursor > 0) _cursor = PrevIndex(_cursor);
                return false;

            case ConsoleKey.RightArrow:
                if (_cursor < _buffer.Length) _cursor = NextIndex(_cursor);
                return false;

            case ConsoleKey.Home:
                _cursor = 0;
                return false;

            case ConsoleKey.End:
                _cursor = _buffer.Length;
                return false;

            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                if (_buffer.Length > 0)
                {
                    SetBuffer(""); // first Ctrl+C clears the line, like Claude Code
                    return false;
                }
                result = null;
                return true;

            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control) && _buffer.Length == 0:
                result = null;
                return true;

            default:
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _buffer.Insert(_cursor, key.KeyChar);
                    _cursor++;
                    OnBufferChanged();
                }
                return false;
        }
    }

    private void SetBuffer (string text)
    {
        _buffer.Clear();
        _buffer.Append(text);
        _cursor = _buffer.Length;
        OnBufferChanged();
    }

    private void OnBufferChanged () => _menuSuppressed = false;

    private void DeleteWordBackward ()
    {
        if (_cursor == 0) return;
        var end = _cursor;
        while (_cursor > 0 && char.IsWhiteSpace(_buffer[_cursor - 1])) _cursor--;
        while (_cursor > 0 && !char.IsWhiteSpace(_buffer[_cursor - 1])) _cursor--;
        _kill = _buffer.ToString(_cursor, end - _cursor);
        _buffer.Remove(_cursor, end - _cursor);
        OnBufferChanged();
    }

    private void InsertKill ()
    {
        if (_kill.Length == 0) return;
        _buffer.Insert(_cursor, _kill);
        _cursor += _kill.Length;
        OnBufferChanged();
    }

    private void MoveWordBackward ()
    {
        while (_cursor > 0 && char.IsWhiteSpace(_buffer[_cursor - 1])) _cursor = PrevIndex(_cursor);
        while (_cursor > 0 && !char.IsWhiteSpace(_buffer[_cursor - 1])) _cursor = PrevIndex(_cursor);
    }

    private void MoveWordForward ()
    {
        while (_cursor < _buffer.Length && !char.IsWhiteSpace(_buffer[_cursor])) _cursor = NextIndex(_cursor);
        while (_cursor < _buffer.Length && char.IsWhiteSpace(_buffer[_cursor])) _cursor = NextIndex(_cursor);
    }

    private void HistoryUp ()
    {
        if (_historyIndex == 0 || history.Count == 0) return;
        if (_historyIndex == history.Count) _draft = _buffer.ToString();
        _historyIndex--;
        SetBuffer(history[_historyIndex]);
    }

    private void HistoryDown ()
    {
        if (_historyIndex >= history.Count) return;
        _historyIndex++;
        SetBuffer(_historyIndex == history.Count ? _draft : history[_historyIndex]);
    }

    // ---------------------------------------------------------------- menu

    private IReadOnlyList<SlashCommand> CurrentMenu ()
    {
        var text = _buffer.ToString();
        if (_menuSuppressed || text.Length == 0 || text[0] != '/' || text.Contains(' '))
        {
            _lastFilter = text;
            return [];
        }

        if (text != _lastFilter)
        {
            _selected = 0;
            _lastFilter = text;
        }

        var prefixMatches = commands
            .Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (prefixMatches.Count > 0) return prefixMatches;

        return commands
            .Where(c => c.Name.Contains(text[1..], StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ---------------------------------------------------------------- rendering

    private void Render ()
    {
        var view = BuildFrame();
        if (setFrame is not null)
        {
            setFrame(view);
            return;
        }

        var frame = new StringBuilder("\r\x1b[0J");
        frame.Append(string.Join('\n', view.Lines));
        if (view.CursorRowsBelow > 0) frame.Append("\x1b[").Append(view.CursorRowsBelow).Append('A');
        frame.Append('\r');
        if (view.CursorColumn > 0) frame.Append("\x1b[").Append(view.CursorColumn).Append('C');

        Console.Out.Write(frame.ToString());
        Console.Out.Flush();
    }

    private LineEditorFrame BuildFrame ()
    {
        var width = SafeWidth();
        var lines = new List<string>();

        // 1. Input lines (single-line horizontal scroll, multi-line soft wrap via explicit newlines).
        var (inputLines, cursorLine, cursorColumn) = RenderInputLines(width);
        lines.AddRange(inputLines);

        // 2. Status Bar
        var modeStr = getModeText?.Invoke() ?? "edit";
        var modeDisplay = AgentModeExtensions.Parse(modeStr).ToDisplayString();
        var statusLeft = $"  {modeDisplay} · /mode switch · / for commands".Style(Ansi.Gray);
        var statusRight = "❖ SeekClaw ".Style(Ansi.Gray);
        var statusPadding = Math.Max(1, width - TextWidth.Of(Ansi.StripStyles(statusLeft)) - TextWidth.Of(Ansi.StripStyles(statusRight)) - 1);
        var statusBar = statusLeft + new string(' ', statusPadding) + statusRight;
        lines.Add(Ansi.TruncateVisible(statusBar, width - 1));

        var menu = CurrentMenu();
        var menuRows = 0;
        if (menu.Count > 0)
        {
            _selected = Math.Clamp(_selected, 0, menu.Count - 1);
            var first = Math.Max(0, Math.Min(_selected - MaxMenuRows + 1, menu.Count - MaxMenuRows));
            var nameWidth = menu.Max(c => c.Name.Length + (c.ArgsHint.Length > 0 ? c.ArgsHint.Length + 1 : 0));

            foreach (var (command, index) in menu.Skip(first).Take(MaxMenuRows).Select((c, i) => (c, first + i)))
            {
                var left = command.Name + (command.ArgsHint.Length > 0 ? " " + command.ArgsHint : "");
                var line = index == _selected
                    ? ("❯ " + left.PadRight(nameWidth) + "  " + command.Description).Style(Ansi.Cyan + Ansi.Bold)
                    : "  " + left.PadRight(nameWidth).Style(Ansi.Bold) + "  " + command.Description.Style(Ansi.Gray);
                lines.Add(Ansi.TruncateVisible(line, width - 1));
                menuRows++;
            }

            lines.Add(Ansi.TruncateVisible(
                "  ↑/↓ select · tab complete · enter run · esc dismiss".Style(Ansi.Gray), width - 1));
            menuRows++;
        }

        var rowsBelowCursor = inputLines.Count - cursorLine + menuRows;
        return new LineEditorFrame(lines, rowsBelowCursor, cursorColumn);
    }

    private (List<string> Lines, int CursorLine, int CursorColumn) RenderInputLines (int width)
    {
        var raw = _buffer.ToString();
        if (raw.Length == 0)
        {
            var empty = new List<string> { Prompt.Style(Ansi.Cyan + Ansi.Bold) + Placeholder.Style(Ansi.Gray) };
            return (empty, 0, TextWidth.Of(Prompt));
        }

        var lines = new List<string>();
        var logicalLines = raw.Replace("\r\n", "\n").Split('\n');
        var cursorLine = 0;
        var cursorColumn = TextWidth.Of(Prompt);

        for (var index = 0; index < logicalLines.Length; index++)
        {
            var logical = logicalLines[index];
            var prefix = index == 0 ? Prompt : Continuation;
            var prefixWidth = TextWidth.Of(prefix);
            var available = Math.Max(10, width - prefixWidth - 1);
            var viewport = Viewport(logical, _cursorLineCursor(index, logicalLines), available);
            var visible = viewport.Visible;
            lines.Add((index == 0 ? prefix.Style(Ansi.Cyan + Ansi.Bold) : prefix) + visible);

            if (_cursor >= LineStart(index, logicalLines) && _cursor <= LineStart(index, logicalLines) + logical.Length)
            {
                cursorLine = index;
                cursorColumn = prefixWidth + viewport.CursorColumn;
            }
        }

        return (lines, cursorLine, cursorColumn);
    }

    private int _cursorLineCursor (int lineIndex, string[] logicalLines)
    {
        if (lineIndex < 0 || lineIndex >= logicalLines.Length) return 0;
        var start = LineStart(lineIndex, logicalLines);
        return Math.Clamp(_cursor - start, 0, logicalLines[lineIndex].Length);
    }

    private int LineStart (int lineIndex, string[] logicalLines)
    {
        var start = 0;
        for (var i = 0; i < lineIndex; i++)
            start += logicalLines[i].Length + 1;
        return start;
    }

    private void Finish ()
    {
        if (setFrame is not null)
        {
            submitLine?.Invoke(Prompt.Style(Ansi.Cyan + Ansi.Bold) + _buffer);
            setFrame(null);
            return;
        }

        // Clear status bar and menus, leave clean submitted input line
        var final = "\r\x1b[0J" + Prompt.Style(Ansi.Cyan + Ansi.Bold) + _buffer + "\n";
        Console.Out.Write(final);
        Console.Out.Flush();
    }

    /// <summary>Slice of one logical input line that fits one row, plus the cursor's display column.</summary>
    private (string Visible, int CursorColumn) Viewport (string text, int cursor, int available)
    {
        if (TextWidth.Of(text) <= available)
            return (text, TextWidth.Of(text[..cursor]));

        // Walk back from the cursor to find the window start.
        var start = cursor;
        var used = 0;
        while (start > 0)
        {
            var prev = PrevLogicalIndex(text, start);
            var w = TextWidth.Of(text[prev..start]);
            if (used + w > available - 8) break; // keep some right-hand context visible
            used += w;
            start = prev;
        }

        var end = start;
        var total = 0;
        while (end < text.Length)
        {
            var next = NextLogicalIndex(text, end);
            var w = TextWidth.Of(text[end..next]);
            if (total + w > available) break;
            total += w;
            end = next;
        }

        var visible = (start > 0 ? "…" : "") + text[start..Math.Max(start, end)];
        var column = (start > 0 ? 1 : 0) + TextWidth.Of(text[start..cursor]);
        return (visible, column);
    }

    private static int PrevLogicalIndex (string text, int index)
    {
        if (index >= 2 && char.IsSurrogatePair(text[index - 2], text[index - 1])) return index - 2;
        return index - 1;
    }

    private static int NextLogicalIndex (string text, int index)
    {
        if (index + 1 < text.Length && char.IsSurrogatePair(text[index], text[index + 1])) return index + 2;
        return index + 1;
    }

    private int PrevIndex (int index)
    {
        if (index >= 2 && char.IsSurrogatePair(_buffer[index - 2], _buffer[index - 1])) return index - 2;
        return index - 1;
    }

    private int NextIndex (int index)
    {
        if (index + 1 < _buffer.Length && char.IsSurrogatePair(_buffer[index], _buffer[index + 1])) return index + 2;
        return index + 1;
    }

    private static int SafeWidth ()
    {
        try { return Math.Max(30, Console.WindowWidth); }
        catch (IOException) { return 100; }
    }
}

/// <summary>Terminal display width (CJK and emoji count as two columns).</summary>
public static class TextWidth
{
    public static int Of (string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += RuneWidth(rune.Value);
        return width;
    }

    private static int RuneWidth (int cp) => cp switch
    {
        < 0x20 => 0,
        >= 0x1100 and <= 0x115F => 2,   // Hangul Jamo
        >= 0x2E80 and <= 0x303E => 2,   // CJK radicals, punctuation
        >= 0x3041 and <= 0x33FF => 2,   // Kana, CJK symbols
        >= 0x3400 and <= 0x4DBF => 2,   // CJK ext A
        >= 0x4E00 and <= 0x9FFF => 2,   // CJK unified
        >= 0xA000 and <= 0xA4CF => 2,   // Yi
        >= 0xAC00 and <= 0xD7A3 => 2,   // Hangul syllables
        >= 0xF900 and <= 0xFAFF => 2,   // CJK compat
        >= 0xFE30 and <= 0xFE4F => 2,   // CJK compat forms
        >= 0xFF00 and <= 0xFF60 => 2,   // fullwidth forms
        >= 0xFFE0 and <= 0xFFE6 => 2,
        >= 0x1F300 and <= 0x1FAFF => 2, // emoji
        >= 0x20000 and <= 0x3FFFD => 2, // CJK ext B+
        _ => 1,
    };
}
