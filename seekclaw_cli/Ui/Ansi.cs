using System.Globalization;
using System.Text.RegularExpressions;

namespace SeekClaw.Cli.Ui;

/// <summary>Minimal ANSI styling helpers for the live renderer (Spectre is used only for static command output).</summary>
public static partial class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Italic = "\x1b[3m";
    public const string Underline = "\x1b[4m";

    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";
    public const string Gray = "\x1b[90m";

    public static string Style(this string text, string style) => style.Length == 0 ? text : style + text + Reset;

    [GeneratedRegex(@"\x1b(?:\[[0-9;]*m|\]8;;[^\x00-\x1f\x7f-\x9f]*\x1b\\)")]
    private static partial Regex DecorationSequences();

    public static string StripStyles(string text) =>
        text.Contains('\x1b') ? DecorationSequences().Replace(text, "") : text;

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex GraphicRendition();

    internal static string StripSgr(string text) =>
        text.Contains('\x1b') ? GraphicRendition().Replace(text, "") : text;

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|(?:\x1b\[|\x9b)[0-?]*[ -/]*[@-~]|\x1b[ -/]*[0-~]|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]")]
    private static partial Regex ControlSequences();

    public static string StripControls(string text) =>
        ControlSequences().Replace(text, "").Replace("\r\n", "\n").Replace('\r', '\n');

    internal static string KeepStyles(string text, bool allowHyperlinks = true) => ControlSequences().Replace(text, match =>
    {
        var sequence = match.ValueSpan;
        var isSgr = sequence.Length >= 3 && sequence.StartsWith("\x1b[", StringComparison.Ordinal) &&
            sequence[^1] == 'm' && sequence[2..^1].IndexOfAnyExcept("0123456789;") < 0;
        return isSgr || allowHyperlinks && sequence.StartsWith(MarkdownLinks.OpenPrefix, StringComparison.Ordinal) &&
            MarkdownLinks.IsSafeSequence(match.Value) ? match.Value : "";
    });

    public static int VisibleLength(string text) => TextWidth.Of(StripStyles(text));

    /// <summary>24-bit foreground color.</summary>
    public static string Rgb(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";

    internal static string BackgroundRgb(int r, int g, int b) => $"\x1b[48;2;{r};{g};{b}m";

    /// <summary>Truncates a styled line to a visible width, closing styles and links before the ellipsis.</summary>
    public static string TruncateVisible(string text, int maxWidth)
    {
        if (maxWidth <= 0) return "";

        var plain = StripStyles(text);
        var columns = 0;
        var cutoff = 0;
        for (var i = 0; i < plain.Length;)
        {
            var next = StringInfo.GetNextTextElementLength(plain.AsSpan(i));
            columns += TextWidth.OfElement(plain.AsSpan(i, next));
            if (columns > maxWidth) break;
            i += next;
            if (columns < maxWidth) cutoff = i; // leave a cell for the ellipsis
        }
        if (columns <= maxWidth) return text;

        // Measure complete graphemes even when a style change falls inside one, then
        // map the plain text cutoff back to the original decorated line.
        var linkOpen = false;
        if (text.Contains('\x1b'))
        {
            foreach (var match in DecorationSequences().EnumerateMatches(text))
            {
                if (match.Index >= cutoff) break;
                var sequence = text.AsSpan(match.Index, match.Length);
                if (sequence.StartsWith(MarkdownLinks.OpenPrefix, StringComparison.Ordinal))
                    linkOpen = !sequence.SequenceEqual(MarkdownLinks.Close);
                cutoff += match.Length;
            }
        }
        return text[..cutoff] + (linkOpen ? MarkdownLinks.Close : "") + Reset + "…";
    }
}
