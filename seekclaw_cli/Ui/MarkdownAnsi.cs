using System.Globalization;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using static SeekClaw.Cli.Ui.MarkdownLayout;

namespace SeekClaw.Cli.Ui;

/// <summary>Markdown layout for the inline terminal transcript.</summary>
/// <remarks>Uses the display conventions of the Apache-licensed Codex CLI markdown renderer.</remarks>
public sealed class MarkdownAnsi
{
    private const string Underline = "\x1b[4m";
    private const string Strike = "\x1b[9m";
    private readonly CliPalette _palette;

    private MarkdownAnsi(CliPalette palette) => _palette = palette;
    private const int TableGap = 2;
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
        builder.BlockParsers.InsertBefore<ParagraphBlockParser>(new MathBlockParser());
        builder.InlineParsers.Add(new FormulaInlineParser());
        return builder.Build();
    }

    private sealed class FormulaInlineParser : MathInlineParser
    {
        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice;
            var previous = processor.Inline;
            if (!base.Match(processor, ref slice)) return false;

            if (processor.Inline is MathInline math)
            {
                var source = math.Content.AsSpan();
                // Dollar signs in currency or shell commands must not consume a
                // later code span, link, or formula as part of a single math node.
                if (!source.ContainsAny('`', '\n', '\r') &&
                    (source.StartsWith("\\", StringComparison.Ordinal) || MarkdownMath.TryRenderInline(source.ToString(), out _)))
                    return true;
            }

            slice = start;
            processor.Inline = previous;
            return false;
        }
    }

    public static List<string> Render(string markdown, int width = 100) => Render(markdown, width, workingDirectory: null);

    public static List<string> Render(string markdown, int width, string? workingDirectory) =>
        Render(markdown, width, workingDirectory, palette: null);

    internal static List<string> Render(string markdown, int width, string? workingDirectory, CliPalette? palette) =>
        Render(Parse(markdown), width, workingDirectory, palette);

    internal static MarkdownDocument Parse(string markdown) => ParseNormalized(Ansi.StripControls(markdown));

    internal static MarkdownDocument ParseNormalized(string markdown) => Markdown.Parse(markdown, Pipeline);

    internal static List<string> Render(IEnumerable<Block> blocks, int width, string? workingDirectory = null, CliPalette? palette = null)
    {
        var lines = new List<string>();
        AppendDocument(lines, blocks, width, workingDirectory, palette);
        return lines;
    }

    internal static void AppendDocument(List<string> lines, IEnumerable<Block> blocks, int width, string? workingDirectory = null,
        CliPalette? palette = null) =>
        new MarkdownAnsi(palette ?? CliPalette.Default).RenderBlocks(blocks, lines, Math.Max(1, width), listDepth: 0, listColumn: 0, workingDirectory);

    private void RenderBlocks(IEnumerable<Block> blocks, List<string> lines, int width, int listDepth, int listColumn, string? workingDirectory)
    {
        foreach (var block in blocks)
        {
            var start = lines.Count;
            // Nested lists continue the item without introducing a blank paragraph.
            if (start > 0 && lines[^1].Length != 0 && !(listDepth > 0 && block is ListBlock))
                lines.Add("");
            var contentStart = lines.Count;
            switch (block)
            {
                case HeadingBlock heading:
                    var style = _palette.Heading + (heading.Level switch
                    {
                        1 => Ansi.Bold + Underline,
                        2 => Ansi.Bold,
                        3 => Ansi.Bold + Ansi.Italic,
                        _ => Ansi.Italic,
                    });
                    var inlines = new List<Run> { new(new string('#', heading.Level) + " ", style) };
                    inlines.AddRange(Inlines(heading.Inline, style, workingDirectory));
                    lines.AddRange(Wrap(inlines, width));
                    break;

                case MathBlock math:
                    RenderMathBlock(math, lines, width);
                    break;

                case CodeBlock code:
                    if (TryReadMarkdownTable(code) is { } tableContent)
                        RenderBlocks(tableContent, lines, width, listDepth, listColumn, workingDirectory);
                    else
                        RenderCode(code, lines, width);
                    break;

                case QuoteBlock quote:
                    var quoted = new List<string>();
                    RenderBlocks(quote, quoted, Math.Max(1, width - 2), listDepth, listColumn, workingDirectory);
                    if (quoted.Count == 0) quoted.Add("");
                    lines.AddRange(quoted.Select(line => ("> " + line.Replace(Ansi.Reset, Ansi.Reset + _palette.Quote)).Style(_palette.Quote)));
                    break;

                case ListBlock list:
                    RenderList(list, lines, width, listDepth, listColumn, workingDirectory);
                    break;

                case Table table:
                    RenderTable(table, lines, width, workingDirectory);
                    break;

                case ThematicBreakBlock:
                    lines.Add(new string('—', Math.Min(3, width)));
                    break;

                case HtmlBlock html:
                    foreach (var line in html.Lines.Lines.AsSpan(0, html.Lines.Count))
                        lines.AddRange(Wrap([new(line.Slice.ToString())], width));
                    break;

                case LeafBlock { Inline: not null } leaf:
                    lines.AddRange(Wrap(Inlines(leaf.Inline, "", workingDirectory), width));
                    break;
            }
            if (lines.Count == contentStart && contentStart > start) lines.RemoveAt(start);
        }
    }

    private void RenderList(ListBlock list, List<string> lines, int width, int depth, int listColumn, string? workingDirectory)
    {
        var items = list.OfType<ListItemBlock>();
        var index = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? (long)start : 1L;
        var indent = new string(' ', Math.Max(0, depth * 4 - listColumn));
        var previousWasMultiline = false;
        foreach (var item in items)
        {
            if (previousWasMultiline) lines.Add("");
            var numeric = list.IsOrdered ? (index++).ToString(CultureInfo.InvariantCulture) + ". " : "";
            var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList;
            var bullet = task is not null ? (task.Checked ? "☑ " : "☐ ") : list.IsOrdered ? "" : "• ";
            var prefix = indent
                + (numeric.Length == 0 ? "" : numeric.Style(_palette.OrderedMarker))
                + (bullet.Length == 0 ? "" : bullet.Style(_palette.Bullet));
            var prefixWidth = Ansi.VisibleLength(prefix);
            var rest = new string(' ', prefixWidth);
            var content = new List<string>();
            RenderBlocks(item, content, Math.Max(1, width - prefixWidth), depth + 1, listColumn + prefixWidth, workingDirectory);
            previousWasMultiline = content.Count > 1;
            if (content.Count == 0) lines.Add(prefix);
            for (var row = 0; row < content.Count; row++)
            {
                if (row == 0) lines.Add(prefix + content[row]);
                else lines.Add(content[row].Length == 0 ? "" : rest + content[row]);
            }
        }
    }

    private void RenderMathBlock(MathBlock block, List<string> lines, int width)
    {
        var closed = block.ClosingFencedCharCount >= block.OpeningFencedCharCount;
        if (closed && MarkdownMath.TryRenderBlock(block.Lines.ToString(), width, out var rendered))
        {
            lines.AddRange(rendered);
            return;
        }

        lines.AddRange(Wrap([new(new string('$', block.OpeningFencedCharCount))], width, literal: true));
        RenderCode(block, lines, width);
        if (closed) lines.AddRange(Wrap([new(new string('$', block.ClosingFencedCharCount))], width, literal: true));
    }

    private static MarkdownDocument? TryReadMarkdownTable(CodeBlock code)
    {
        if (code is not FencedCodeBlock fence || fence.ClosingFencedCharCount < fence.OpeningFencedCharCount
            || code.Lines.Count < 2) return null;
        var info = fence.Info;
        if (!string.Equals(info, "md", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(info, "markdown", StringComparison.OrdinalIgnoreCase)) return null;

        var source = code.Lines.ToString();
        if (!source.Contains('|')) return null;
        var document = Markdown.Parse(source, Pipeline);
        // Unwrap only real pipe tables. Other snippets keep their original Markdown text.
        return document.Any(static block => block is Table) ? document : null;
    }

    private void RenderCode(CodeBlock code, List<string> lines, int width)
    {
        var fence = code as FencedCodeBlock;
        var language = CodeLanguage(fence);
        if (fence is not null && fence.ClosingFencedCharCount >= fence.OpeningFencedCharCount
            && language.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
        {
            var status = MarkdownMermaid.Render(code.Lines.ToString(), width, out var diagram, _palette);
            if (status == MarkdownMermaid.Status.Success)
            {
                foreach (var row in diagram) lines.AddRange(Wrap(row, width, literal: true));
                return;
            }
            lines.AddRange(Wrap([new(MarkdownMermaid.Notice(status), _palette.Muted)], width));
        }
        var indent = code is FencedCodeBlock ? "" : new string(' ', Math.Min(4, width - 1));
        var highlighter = new MarkdownCode(language, _palette);
        foreach (var line in code.Lines.Lines.AsSpan(0, code.Lines.Count))
            lines.AddRange(Wrap(highlighter.HighlightLine(line.Slice.ToString()), width - indent.Length, literal: true)
                .Select(row => indent + row));
    }

    internal static string CodeLanguage(FencedCodeBlock? fence) => fence?.Info?.Split([' ', ',', '\t'], 2)[0] ?? "";

    private IEnumerable<Run> Inlines(ContainerInline? container, string style, string? workingDirectory)
    {
        if (container is null) yield break;
        var afterTask = false;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case TaskList:
                    afterTask = true;
                    break;
                case LiteralInline literal:
                    var text = literal.Content.ToString();
                    yield return new(afterTask ? text.TrimStart() : text, style);
                    afterTask = false;
                    break;
                case MathInline math:
                    var formula = math.Content.ToString();
                    var delimiter = new string(math.Delimiter, math.DelimiterCount);
                    yield return new(MarkdownMath.TryRenderInline(formula, out var rendered) ? rendered : delimiter + formula + delimiter, style);
                    break;
                case CodeInline code:
                    yield return new(code.Content, style + _palette.CodeString);
                    break;
                case EmphasisInline emphasis:
                    var format = emphasis.DelimiterChar == '~' ? Strike
                        : emphasis.DelimiterCount >= 2 ? _palette.Strong + Ansi.Bold
                        : _palette.Emphasis + Ansi.Italic;
                    foreach (var part in Inlines(emphasis, style + format, workingDirectory)) yield return part;
                    break;
                case LinkInline link:
                    foreach (var part in RenderLink(link, style, workingDirectory)) yield return part;
                    break;
                case AutolinkInline autolink:
                    var destination = MarkdownLinks.WebDestination(autolink.Url);
                    yield return new(autolink.Url, destination is null ? style : style + _palette.Link + Underline,
                        MarkdownLinks.CanOpenWebLinks ? destination : null, BreakAtSeparators: destination is not null);
                    break;
                case LineBreakInline:
                    yield return new("\n", style);
                    break;
                case HtmlEntityInline entity:
                    yield return new(entity.Transcoded.ToString(), style);
                    break;
                case HtmlInline html:
                    yield return new(html.Tag, style);
                    break;
                case ContainerInline nested:
                    foreach (var part in Inlines(nested, style, workingDirectory)) yield return part;
                    break;
            }
        }
    }

    private IEnumerable<Run> RenderLink(LinkInline link, string style, string? workingDirectory)
    {
        if (link.IsImage)
        {
            foreach (var part in Inlines(link, style, workingDirectory)) yield return part;
            yield break;
        }

        var url = link.Url ?? "";
        var web = MarkdownLinks.WebDestination(url);
        var clickable = MarkdownLinks.CanOpenWebLinks ? web : null;
        var local = web is null ? MarkdownLinks.LocalTarget(url, workingDirectory) : null;
        var targetStyle = local is not null ? style + _palette.CodeString
            : web is not null ? style + _palette.Link + Underline : style;
        var target = new Run(local ?? url, targetStyle, clickable, BreakAtSeparators: local is not null || web is not null);
        var labelStyle = clickable is not null || link.IsAutoLink && web is not null ? targetStyle : style;
        var label = Inlines(link, labelStyle, workingDirectory).ToArray();
        var title = string.Concat(label.Select(static part => part.Text));

        if (string.IsNullOrWhiteSpace(title))
        {
            if (target.Text.Length > 0) yield return target;
            yield break;
        }

        foreach (var part in label)
            yield return part with { Destination = clickable, BreakAtSeparators = link.IsAutoLink && web is not null };
        if (target.Text.Length == 0 || clickable is not null || link.IsAutoLink || local is null && title == url)
            yield break;
        yield return new(" (", style);
        yield return target;
        yield return new(")", style);
    }

    private void RenderTable(Table table, List<string> lines, int width, string? workingDirectory)
    {
        var rows = table.OfType<TableRow>().Select(row => row.OfType<TableCell>().Select(cell => TableCellRuns(cell, workingDirectory)).ToArray()).ToArray();
        if (rows.Length == 0) return;
        var columnCount = rows.Max(static row => row.Length);
        if (columnCount == 0) return;
        var plainRows = rows.Select(row => row.Select(PlainCellText).ToArray()).ToArray();
        // Each column has one space on both sides and two spaces between columns.
        var available = width - 2 * columnCount - TableGap * (columnCount - 1);
        var layout = MarkdownTableLayout.PlanGrid(plainRows, columnCount, available);
        if (layout.ColumnWidths is not { } widths)
        {
            if (rows.Length == 1)
                RenderTableAsPipeHeader(rows[0], table, lines, width);
            else
                RenderTableAsRecords(rows, plainRows[0], lines, width, layout.MinimumAlignedValueWidth);
            return;
        }
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            var cells = widths.Select((cells, column) => Wrap(
                column < row.Length ? CellStyle(row[column], rowIndex == 0 ? _palette.TableHeader + Ansi.Bold : "") : [], cells)).ToArray();
            for (var lineIndex = 0; lineIndex < cells.Max(static cell => cell.Count); lineIndex++)
            {
                var fields = cells.Select((cell, column) => Pad(
                    lineIndex < cell.Count ? cell[lineIndex] : "", widths[column], table.ColumnDefinitions.ElementAtOrDefault(column)?.Alignment));
                lines.Add((" " + string.Join(new string(' ', TableGap + 2), fields)).TrimEnd());
            }
            if (rowIndex < rows.Length - 1 || rowIndex == 0)
            {
                var rule = rowIndex == 0 ? '━' : '─';
                var segments = widths.Select(cells => new string(rule, cells + 2));
                lines.Add(string.Join(new string(' ', TableGap), segments).Style(_palette.Muted));
            }
        }
    }

    private static IEnumerable<Run> CellStyle(IEnumerable<Run> runs, string style) =>
        runs.Select(part => part with { Text = part.Text.Replace('\n', ' '), Style = style + part.Style });

    private Run[] TableCellRuns(TableCell cell, string? workingDirectory) => cell.OfType<LeafBlock>()
        .SelectMany(block => Inlines(block.Inline, "", workingDirectory)).ToArray();

    private static string Pad(string value, int width, TableColumnAlign? alignment)
    {
        var space = Math.Max(0, width - Ansi.VisibleLength(value));
        var before = alignment switch { TableColumnAlign.Right => space, TableColumnAlign.Center => space / 2, _ => 0 };
        return new string(' ', before) + value + new string(' ', space - before);
    }

    private static string PlainCellText(IEnumerable<Run> cell) =>
        string.Concat(cell.Select(static part => part.Text.Replace('\n', ' ').Replace("\t", "    ")));

    private void RenderTableAsPipeHeader(Run[][] headers, Table table, List<string> lines, int width)
    {
        var heading = new List<Run> { new("| ") };
        for (var column = 0; column < headers.Length; column++)
        {
            heading.AddRange(CellStyle(headers[column], _palette.TableHeader + Ansi.Bold)
                .Select(static part => part with { Text = part.Text.Replace("|", "\\|", StringComparison.Ordinal) }));
            heading.Add(new(column + 1 == headers.Length ? " |" : " | "));
        }
        lines.AddRange(Wrap(heading, width));

        var separators = Enumerable.Range(0, headers.Length).Select(column =>
            table.ColumnDefinitions.ElementAtOrDefault(column)?.Alignment switch
            {
                TableColumnAlign.Left => ":---", TableColumnAlign.Right => "---:",
                TableColumnAlign.Center => ":---:", _ => "---",
            });
        lines.AddRange(Wrap([new("| " + string.Join(" | ", separators) + " |", Ansi.Dim)], width));
    }

    private void RenderTableAsRecords(Run[][][] rows, string[] headers, List<string> lines, int width,
        int minimumValueWidth)
    {
        var headingWidth = headers.Select(TextWidth.Of).DefaultIfEmpty().Max();
        var valueColumn = headingWidth + 1 + TableGap;
        var aligned = valueColumn + minimumValueWidth <= width;
        var labelIndent = Math.Min(1, width - 1);
        var valueIndent = aligned ? valueColumn : Math.Min(2, width - 1);
        var labelPrefix = new string(' ', labelIndent);
        var valuePrefix = new string(' ', valueIndent);
        for (var row = 1; row < rows.Length; row++)
        {
            if (row > 1) lines.Add(new string('─', width).Style(_palette.Muted));
            for (var column = 0; column < rows[row].Length; column++)
            {
                var label = column < rows[0].Length
                    ? Wrap(CellStyle(rows[0][column], _palette.TableHeader + Ansi.Bold), width - labelIndent) : [""];
                var values = Wrap(CellStyle(rows[row][column], ""), width - valueIndent);
                if (aligned)
                {
                    var padded = Pad(label[0], headingWidth + TableGap, TableColumnAlign.Left);
                    lines.Add((labelPrefix + padded + values[0]).TrimEnd());
                    lines.AddRange(values.Skip(1).Select(line => valuePrefix + line));
                }
                else
                {
                    lines.AddRange(label.Select(line => labelPrefix + line));
                    lines.AddRange(values.Select(line => valuePrefix + line));
                }
            }
        }
    }
}
