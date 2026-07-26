using System.Text.RegularExpressions;

namespace SeekClaw.Cli.Ui;

/// <summary>Minimal ANSI styling helpers for the live renderer (Spectre is used only for static command output).</summary>
public static partial class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";

    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";
    public const string Gray = "\x1b[90m";

    public static string Style(this string text, string style) => style + text + Reset;

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex EscapeSequences();

    public static string StripStyles(string text) =>
        text.Contains('\x1b') ? EscapeSequences().Replace(text, "") : text;

    public static int VisibleLength(string text) => StripStyles(text).Length;

    /// <summary>24-bit foreground color.</summary>
    public static string Rgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

    /// <summary>Truncates to a visible width, preserving escape sequences and closing styles.</summary>
    public static string TruncateVisible(string text, int maxWidth)
    {
        if (VisibleLength(text) <= maxWidth) return text;

        var visible = 0;
        var i = 0;
        while (i < text.Length && visible < maxWidth - 1)
        {
            if (text[i] == '\x1b')
            {
                var end = text.IndexOf('m', i);
                if (end < 0) break;
                i = end + 1;
                continue;
            }
            visible++;
            i++;
        }
        return text[..i] + Reset + "…";
    }
}
