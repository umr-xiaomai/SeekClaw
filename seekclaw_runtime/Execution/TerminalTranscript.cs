using System.Text;

namespace SeekClaw.Runtime.Execution;

internal sealed class CommandOutputBuffer
{
    private const int OutputLimit = 128 * 1024;
    private readonly StringBuilder _text = new();

    public long Start { get; private set; }
    public long End { get; private set; }

    public void Append(ReadOnlySpan<char> value)
    {
        _text.Append(value);
        End += value.Length;
        if (_text.Length <= OutputLimit) return;

        var discarded = _text.Length - OutputLimit;
        if (char.IsLowSurrogate(_text[discarded])) discarded++;
        _text.Remove(0, discarded);
        Start += discarded;
    }

    public string Slice(long from, long until)
    {
        var offset = (int)(from - Start);
        var length = (int)(until - from);
        if (until < End && length > 0 && char.IsHighSurrogate(_text[offset + length - 1])) length--;
        return _text.ToString(offset, length);
    }
}

/// <summary>Projects ordinary PTY lines and prompts into a readable, append-only transcript.</summary>
/// <remarks>The raw stream remains available for a full terminal emulator.</remarks>
internal sealed class TerminalTranscript(CommandOutputBuffer output)
{
    private const int MaximumLineLength = 64 * 1024;
    private enum ParseState { Text, Escape, EscapeIntermediate, Csi, ControlString, StringEscape }

    private readonly StringBuilder _line = new();
    private ParseState _state;
    private int _row = 1;
    private int _column;
    private int _parameter;
    private int _secondParameter;
    private bool _hasSecondParameter;
    private bool _privateSequence;
    private bool _needsLineBreak;
    private string _published = "";

    public bool HasPromptUpdate => !_line.Equals(_published.AsSpan()) && _line.Length != 0;

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (var ch in text) Append(ch);
    }

    public void PublishPrompt()
    {
        if (!HasPromptUpdate) return;
        var line = _line.ToString();
        if (_needsLineBreak)
        {
            output.Append("\n");
            _needsLineBreak = false;
        }
        if (line.StartsWith(_published, StringComparison.Ordinal)) output.Append(line.AsSpan(_published.Length));
        else
        {
            if (_published.Length != 0) output.Append("\n");
            output.Append(line);
        }
        _published = line;
    }

    private void Append(char value)
    {
        switch (_state)
        {
            case ParseState.Text:
                AppendText(value);
                break;
            case ParseState.Escape:
                _state = value switch
                {
                    '[' => ParseState.Csi,
                    ']' or 'P' or '^' or '_' or 'X' => ParseState.ControlString,
                    >= ' ' and <= '/' => ParseState.EscapeIntermediate,
                    '\x1b' => ParseState.Escape,
                    _ => ParseState.Text,
                };
                _parameter = _secondParameter = 0;
                _hasSecondParameter = _privateSequence = false;
                break;
            case ParseState.EscapeIntermediate:
                if (value >= '0') _state = ParseState.Text;
                break;
            case ParseState.Csi:
                AppendCsi(value);
                break;
            case ParseState.ControlString:
                if (value is '\a' or '\x9c') _state = ParseState.Text;
                else if (value == '\x1b') _state = ParseState.StringEscape;
                break;
            case ParseState.StringEscape:
                _state = value == '\\' ? ParseState.Text : ParseState.ControlString;
                break;
        }
    }

    private void AppendText(char value)
    {
        switch (value)
        {
            case '\x1b': _state = ParseState.Escape; return;
            case '\r': _column = 0; return;
            case '\b': _column = Math.Max(0, _column - 1); return;
            case '\n':
                FinishLine();
                _row++;
                return;
        }
        if (value != '\t' && char.IsControl(value)) return;
        if (_column == MaximumLineLength)
        {
            PublishPrompt();
            ResetLine();
        }
        if (_column > _line.Length) _line.Append(' ', _column - _line.Length);
        if (_column == _line.Length) _line.Append(value);
        else _line[_column] = value;
        _column++;
    }

    private void AppendCsi(char value)
    {
        if (char.IsAsciiDigit(value))
        {
            ref var parameter = ref (_hasSecondParameter ? ref _secondParameter : ref _parameter);
            parameter = Math.Min(MaximumLineLength, 10 * parameter + value - '0');
        }
        else if (value == ';') _hasSecondParameter = true;
        else if (value is >= '@' and <= '~')
        {
            if (!_privateSequence) ApplyCsi(value);
            _state = ParseState.Text;
        }
        else if (value == '\x1b') _state = ParseState.Escape;
        else _privateSequence = true;
    }

    private void ApplyCsi(char operation)
    {
        var count = Math.Max(1, _parameter);
        switch (operation)
        {
            case 'C': _column = Math.Min(MaximumLineLength, _column + count); break;
            case 'D': _column = Math.Max(0, _column - count); break;
            case 'G': _column = count - 1; break;
            case 'A': MoveToRow(Math.Max(1, _row - count), _column); break;
            case 'B': MoveToRow(_row + count, _column); break;
            case 'E': MoveToRow(_row + count, 0); break;
            case 'F': MoveToRow(Math.Max(1, _row - count), 0); break;
            case 'd': MoveToRow(count, _column); break;
            case 'H' or 'f': MoveToRow(count, Math.Max(0, _secondParameter - 1)); break;
            case 'K' when _parameter == 0: _line.Length = Math.Min(_line.Length, _column); break;
            case 'K' when _parameter == 1:
                for (var i = 0; i <= _column && i < _line.Length; i++) _line[i] = ' ';
                break;
            case 'K' when _parameter == 2: _line.Clear(); break;
        }
    }

    private void MoveToRow(int row, int column)
    {
        // The current line belongs to the previous row. Never overwrite its text
        // with a repaint sent for a different row.
        if (row != _row && (_line.Length != 0 || _published.Length != 0))
        {
            PublishPrompt();
            ResetLine();
            _needsLineBreak = true;
        }
        _row = row;
        _column = column;
    }

    private void FinishLine()
    {
        PublishPrompt();
        output.Append("\n");
        _needsLineBreak = false;
        ResetLine();
    }

    private void ResetLine()
    {
        _line.Clear();
        _published = "";
        _column = 0;
    }
}
