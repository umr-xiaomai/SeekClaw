namespace SeekClaw.Cli.Ui;

/// <summary>Chooses a grid while the cells remain readable, or records when width is scarce.</summary>
internal static class MarkdownTableLayout
{
    private const int MinimumColumnWidth = 3;
    private const int ReadableExpansiveWidth = 12;

    private enum ColumnKind { TokenHeavy, Narrative, Compact }
    private sealed record Cell(string Text, int Width, int[] Words)
    {
        public int LongestWord => Words.Length == 0 ? 0 : Words.Max();
    }
    private sealed record Column(Cell[] Cells, int Width, int PreferredWidth, ColumnKind Kind);
    internal sealed record Plan(int[]? ColumnWidths, int MinimumAlignedValueWidth);

    internal static Plan PlanGrid(string[][] rows, int columnCount, int availableWidth)
    {
        var columns = Enumerable.Range(0, columnCount).Select(index => MeasureColumn(rows, index)).ToArray();
        var minimumValueWidth = columns.Any(static column => column.Kind != ColumnKind.Compact) ? 24 : 12;
        if (availableWidth < columnCount * MinimumColumnWidth) return new(null, minimumValueWidth);

        var widths = columns.Select(static column => column.Width).ToArray();
        if (widths.Sum() <= availableWidth) return new(widths, minimumValueWidth);

        var preferred = columns.Select(static column => column.PreferredWidth).ToArray();
        if (preferred.Sum() > availableWidth)
            Shrink(preferred, Enumerable.Repeat(MinimumColumnWidth, columnCount).ToArray(), columns, availableWidth);
        Shrink(widths, preferred, columns, availableWidth);
        return new(ShouldUseRecords(columns, widths, rows.Length) ? null : widths, minimumValueWidth);
    }

    private static Column MeasureColumn(string[][] rows, int index)
    {
        var cells = rows.Select(row => Measure(index < row.Length ? row[index] : "")).ToArray();
        var width = Math.Max(MinimumColumnWidth, cells.Max(static cell => cell.Width));
        var populatedRows = cells.Skip(1).Where(static cell => cell.Words.Length > 0).ToArray();
        var wordCount = populatedRows.Sum(static cell => cell.Words.Length);
        var longWordCount = populatedRows.Sum(static cell => cell.Words.Count(static word => word >= 20));
        var averageWords = populatedRows.Length == 0 ? cells[0].Words.Length : (double)wordCount / populatedRows.Length;
        var averageWidth = populatedRows.Length == 0 ? cells[0].Width : populatedRows.Average(static cell => cell.Width);
        var kind = longWordCount > 0 && longWordCount >= wordCount - longWordCount ? ColumnKind.TokenHeavy
            : averageWords >= 4 || averageWidth >= 28 ? ColumnKind.Narrative : ColumnKind.Compact;
        var widestBodyWord = populatedRows.Select(static cell => cell.LongestWord).DefaultIfEmpty().Max();
        var preferred = kind == ColumnKind.Compact ? Math.Max(cells[0].LongestWord, Math.Min(16, widestBodyWord)) : 16;
        return new(cells, width, Math.Clamp(preferred, MinimumColumnWidth, width), kind);
    }

    private static Cell Measure(string text) => new(text, TextWidth.Of(text),
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(TextWidth.Of).ToArray());

    private static void Shrink(int[] widths, int[] floors, Column[] columns, int available)
    {
        var excess = widths.Sum() - available;
        foreach (var kind in new[] { ColumnKind.TokenHeavy, ColumnKind.Narrative, ColumnKind.Compact })
        {
            if (excess <= 0) return;
            var indices = Enumerable.Range(0, columns.Length)
                .Where(index => columns[index].Kind == kind && widths[index] > floors[index]).ToArray();
            if (indices.Length == 0) continue;

            int Slack(int index) => widths[index] - floors[index];
            int CappedTotal(int cap) => indices.Sum(index => Math.Min(cap, Slack(index)));
            var total = indices.Sum(Slack);
            var removed = Math.Min(excess, total);
            var budget = total - removed;

            // Balance the slack of equal-priority columns without scanning once per character.
            var low = 0;
            var high = indices.Max(Slack);
            while (low < high)
            {
                var cap = low + (high - low + 1) / 2;
                if (CappedTotal(cap) <= budget) low = cap;
                else high = cap - 1;
            }

            var left = budget - CappedTotal(low);
            foreach (var index in indices)
            {
                var slack = Math.Min(low, Slack(index));
                if (left > 0 && Slack(index) > low)
                {
                    slack++;
                    left--;
                }
                widths[index] = floors[index] + slack;
            }
            excess -= removed;
        }
    }

    private static bool ShouldUseRecords(Column[] columns, int[] widths, int rowCount)
    {
        var bodyRows = rowCount - 1;
        var threshold = bodyRows == 1 ? 1 : Math.Max(2, (bodyRows + 2) / 3);
        var affected = 0;
        for (var row = 1; row < rowCount; row++)
        {
            if (RowIsCramped(columns, widths, row) && ++affected >= threshold) return true;
        }
        return false;
    }

    private static bool RowIsCramped(Column[] columns, int[] widths, int row)
    {
        var expansiveCells = 0;
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index];
            var cell = column.Cells[row];
            var width = widths[index];
            if (column.Kind == ColumnKind.Compact)
            {
                if (cell.LongestWord > width) return true;
                continue;
            }
            if (column.Kind == ColumnKind.TokenHeavy && width < ReadableExpansiveWidth && cell.LongestWord > width)
                return true;
            if (cell.Width <= width) continue;

            var height = MarkdownLayout.Wrap([new(cell.Text)], width).Count;
            if (column.Kind == ColumnKind.Narrative && width < ReadableExpansiveWidth && height >= 7) return true;
            if (height >= 4 && ++expansiveCells >= 2) return true;
        }
        return false;
    }
}
