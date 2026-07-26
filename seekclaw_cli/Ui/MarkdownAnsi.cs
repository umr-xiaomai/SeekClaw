using System.Text;
using Markdig;
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

    private static void RenderCode(LeafBlock code, List<string> lines, string indent)
    {
        var language = (code as FencedCodeBlock)?.Info;
        lines.Add(indent + ("╭─ " + (string.IsNullOrWhiteSpace(language) ? "code" : language)).Style(Ansi.Gray));
        foreach (var line in code.Lines.Lines[..code.Lines.Count])
            lines.Add(indent + "│ ".Style(Ansi.Gray) + line.Slice.ToString().Style(Ansi.Yellow));
        lines.Add(indent + "╰─".Style(Ansi.Gray));
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
