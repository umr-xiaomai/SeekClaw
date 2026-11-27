using System.Globalization;
using System.Text;

namespace SeekClaw.Cli.Ui;

internal static class MarkdownLayout
{
    internal readonly record struct Run(string Text, string Style = "", string? Destination = null,
        bool BreakAtSeparators = false);
    private readonly record struct Glyph(string Text, string Style, string? Destination, int Width, bool Space, bool BreakAfter);

    public static List<string> Wrap(IEnumerable<Run> runs, int width, bool literal = false)
    {
        var lines = new List<string>();
        var glyphs = new List<Glyph>();
        var combined = new StringBuilder();
        var ranges = new List<(int End, Run Run)>();
        foreach (var run in runs)
        {
            var text = Ansi.StripControls(run.Text).Replace("\t", "    ");
            if (text.Length == 0) continue;
            combined.Append(text);
            ranges.Add((combined.Length, run));
        }

        width = Math.Max(width, 1);
        var range = 0;
        // A color or link boundary can fall inside an emoji or combining sequence.
        // Measure once across those boundaries and assign the whole glyph its initial style.
        var elements = StringInfo.GetTextElementEnumerator(combined.ToString());
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element == "\n")
            {
                WrapPhysicalLine(glyphs, lines, width, literal);
                glyphs.Clear();
                continue;
            }

            while (elements.ElementIndex >= ranges[range].End) range++;
            var run = ranges[range].Run;
            var breakAfter = run.BreakAtSeparators && element is "/" or "\\" or "-";
            glyphs.Add(new(element, run.Style, run.Destination, TextWidth.OfElement(element),
                char.IsWhiteSpace(element[0]), breakAfter));
        }
        WrapPhysicalLine(glyphs, lines, width, literal);
        return lines;
    }

    private static void WrapPhysicalLine(List<Glyph> glyphs, List<string> lines, int width, bool literal)
    {
        if (glyphs.Count == 0) { lines.Add(""); return; }
        for (var start = 0; start < glyphs.Count;)
        {
            var end = start;
            var breakAt = -1;
            var resumeAt = -1;
            var columns = 0;
            while (end < glyphs.Count)
            {
                var glyph = glyphs[end];
                if (!literal && glyph.Space && end > start)
                {
                    breakAt = end;
                    resumeAt = end + 1;
                }
                if (end > start && columns + glyph.Width > width) break;
                columns += glyph.Width;
                end++;
                if (!literal && glyph.BreakAfter)
                    breakAt = resumeAt = end;
            }
            var next = end;
            if (!literal && end < glyphs.Count && breakAt > start)
            {
                end = breakAt;
                while (end > start && glyphs[end - 1].Space) end--;
                next = resumeAt;
                while (next < glyphs.Count && glyphs[next].Space) next++;
            }
            lines.Add(Render(glyphs, start, end));
            start = next;
        }
    }

    private static string Render(List<Glyph> glyphs, int start, int end)
    {
        var line = new StringBuilder();
        var activeStyle = "";
        string? activeDestination = null;
        for (var i = start; i < end; i++)
        {
            var glyph = glyphs[i];
            if (glyph.Destination != activeDestination)
            {
                if (activeDestination is not null) line.Append(MarkdownLinks.Close);
                activeDestination = glyph.Destination;
                if (activeDestination is not null)
                    line.Append(MarkdownLinks.OpenPrefix).Append(activeDestination).Append(MarkdownLinks.Terminator);
            }
            if (glyph.Style != activeStyle)
            {
                if (activeStyle.Length != 0) line.Append(Ansi.Reset);
                activeStyle = glyph.Style;
                line.Append(activeStyle);
            }
            line.Append(glyph.Text);
        }
        if (activeDestination is not null) line.Append(MarkdownLinks.Close);
        if (activeStyle.Length != 0) line.Append(Ansi.Reset);
        return line.ToString();
    }
}
