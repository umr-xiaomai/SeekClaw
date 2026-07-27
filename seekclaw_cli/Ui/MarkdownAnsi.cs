using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SeekClaw.Cli.Ui;

/// <summary>Renders assistant markdown into ANSI-styled terminal lines.</summary>
public static class MarkdownAnsi
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    public static List<string> Render(string markdown, int width = 100)
    {
        var lines = new List<string>();
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(markdown, Pipeline);
        }
        catch (Exception)
        {
            lines.AddRange(markdown.Split('\n'));
            return lines;
        }

        RenderBlocks(document, lines, "", width);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static void RenderBlocks(ContainerBlock container, List<string> lines, string indent, int width)
    {
        var first = true;
        foreach (var block in container)
        {
            if (!first && block is not ListItemBlock) lines.Add(indent.TrimEnd());
            first = false;

            switch (block)
            {
                case HeadingBlock heading:
                    var text = RenderInlines(heading.Inline);
                    lines.Add(indent + (heading.Level <= 2
                        ? text.Style(Ansi.Bold + Ansi.Cyan)
                        : text.Style(Ansi.Bold)));
                    break;

                case FencedCodeBlock code:
                    RenderCode(code, lines, indent);
                    break;

                case CodeBlock code:
                    RenderCode(code, lines, indent);
                    break;

                case QuoteBlock quote:
                {
                    var inner = new List<string>();
                    RenderBlocks(quote, inner, "", width - 2);
                    lines.AddRange(inner.Select(l => indent + "│ ".Style(Ansi.Gray) + l.Style(Ansi.Dim)));
                    break;
                }

                case ListBlock list:
                {
                    var index = 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var bullet = list.IsOrdered ? $"{index++}. " : "• ";
                        var inner = new List<string>();
                        RenderBlocks(item, inner, "", width - indent.Length - bullet.Length);
                        for (var i = 0; i < inner.Count; i++)
                            lines.Add(indent + (i == 0 ? bullet.Style(Ansi.Cyan) : new string(' ', bullet.Length)) + inner[i]);
                    }
                    break;
                }

                case ThematicBreakBlock:
                    lines.Add(indent + new string('─', Math.Max(10, width - indent.Length - 2)).Style(Ansi.Gray));
                    break;

                case Table table:
                    RenderTable(table, lines, indent, width);
                    break;

                case ParagraphBlock paragraph:
                    lines.AddRange(Wrap(RenderInlines(paragraph.Inline), width - indent.Length)
                        .Select(l => indent + l));
                    break;

                case ContainerBlock nested:
                    RenderBlocks(nested, lines, indent, width);
                    break;

                case LeafBlock leaf:
                    lines.AddRange(Wrap(RenderInlines(leaf.Inline), width - indent.Length).Select(l => indent + l));
                    break;
            }
        }
    }

    private static void RenderTable(Table table, List<string> lines, string indent, int width)
    {
        // Collect all cell contents
        var rows = new List<List<string>>();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.OfType<TableCell>())
            {
                var cellLines = new List<string>();
                RenderBlocks(cell, cellLines, "", width);
                cells.Add(string.Join(" ", cellLines.Select(l => l.Trim())));
            }
            rows.Add(cells);
        }

        if (rows.Count == 0) return;

        // Calculate column widths
        var colCount = rows.Max(r => r.Count);
        var colWidths = new int[colCount];
        foreach (var row in rows)
            for (var i = 0; i < row.Count; i++)
                colWidths[i] = Math.Max(colWidths[i], Ansi.VisibleLength(row[i]));

        // Clamp column widths
        var maxColWidth = Math.Max(20, (width - colCount * 3 - 1) / colCount);
        for (var i = 0; i < colCount; i++)
            colWidths[i] = Math.Min(colWidths[i], maxColWidth);

        // Render table
        var isHeader = true;
        foreach (var row in rows)
        {
            var sb = new StringBuilder(indent + "│");
            for (var i = 0; i < colCount; i++)
            {
                var cellContent = i < row.Count ? row[i] : "";
                var visibleLen = Ansi.VisibleLength(cellContent);
                var padding = Math.Max(0, colWidths[i] - visibleLen);
                sb.Append(' ');
                sb.Append(isHeader ? cellContent.Style(Ansi.Bold) : cellContent);
                sb.Append(new string(' ', padding + 1));
                sb.Append('│');
            }
            lines.Add(sb.ToString());

            // Add separator after header
            if (isHeader)
            {
                var sep = new StringBuilder(indent + "├");
                for (var i = 0; i < colCount; i++)
                {
                    sep.Append(new string('─', colWidths[i] + 2));
                    sep.Append(i < colCount - 1 ? '┼' : '┤');
                }
                lines.Add(sep.ToString());
                isHeader = false;
            }
        }
    }

    private static void RenderCode(LeafBlock code, List<string> lines, string indent)
    {
        var language = (code as FencedCodeBlock)?.Info;
        var lineNumColor = Ansi.Rgb(100, 100, 100); // Dim gray for line numbers

        // Top border with language label
        var langLabel = string.IsNullOrWhiteSpace(language) ? "" : $" {language}";
        lines.Add((indent + "┌" + new string('─', Math.Max(20, Math.Min(40, langLabel.Length + 2)))).Style(Ansi.Gray));
        if (!string.IsNullOrWhiteSpace(langLabel))
            lines.Add((indent + "│" + langLabel).Style(Ansi.Gray));

        // Code lines with line numbers
        var codeLines = code.Lines.Lines[..code.Lines.Count];
        for (var i = 0; i < codeLines.Length; i++)
        {
            var lineNum = (i + 1).ToString().PadLeft(3);
            var content = codeLines[i].Slice.ToString();
            lines.Add((indent + "│ " + lineNum.Style(lineNumColor) + "  " + content).Style(Ansi.Cyan));
        }

        // Bottom border
        lines.Add((indent + "└" + new string('─', Math.Max(20, Math.Min(40, (language?.Length ?? 0) + 4)))).Style(Ansi.Gray));
    }

    private static string RenderInlines(ContainerInline? container)
    {
        if (container is null) return "";
        var sb = new StringBuilder();
        foreach (var inline in container)
            sb.Append(RenderInline(inline));
        return sb.ToString();
    }

    private static string RenderInline(Inline inline) => inline switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content.Style(Ansi.Yellow),
        EmphasisInline { DelimiterCount: >= 2 } strong => RenderInlines(strong).Style(Ansi.Bold),
        EmphasisInline emphasis => RenderInlines(emphasis).Style(Ansi.Italic),
        LinkInline link => RenderInlines(link) + (string.IsNullOrEmpty(link.Url) ? "" : $" ({link.Url})".Style(Ansi.Gray)),
        LineBreakInline { IsHard: true } => "\n",
        LineBreakInline => " ",
        AutolinkInline autolink => autolink.Url.Style(Ansi.Cyan),
        HtmlInline html => html.Tag,
        ContainerInline container => RenderInlines(container),
        _ => "",
    };

    private static IEnumerable<string> Wrap(string text, int width)
    {
        width = Math.Max(20, width);
        foreach (var rawLine in text.Split('\n'))
        {
            if (Ansi.VisibleLength(rawLine) <= width)
            {
                yield return rawLine;
                continue;
            }

            var current = new StringBuilder();
            var visible = 0;
            foreach (var word in rawLine.Split(' '))
            {
                var wordLength = Ansi.VisibleLength(word);
                if (visible > 0 && visible + wordLength + 1 > width)
                {
                    yield return current.ToString();
                    current.Clear();
                    visible = 0;
                }
                if (visible > 0) { current.Append(' '); visible++; }
                current.Append(word);
                visible += wordLength;
            }
            if (current.Length > 0) yield return current.ToString();
        }
    }
}
