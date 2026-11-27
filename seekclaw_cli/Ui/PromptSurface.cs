namespace SeekClaw.Cli.Ui;

/// <summary>Draws the editable and submitted prompt with the same gutter and padding.</summary>
internal static class PromptSurface
{
    internal const string Marker = "› ";
    private const string Indent = "  ";
    private const string ResetIntensity = "\x1b[22m";

    internal static string EditorRow(string text, int terminalWidth, bool first, CliPalette palette, bool placeholder = false)
    {
        var width = Math.Max(1, terminalWidth - Marker.Length - 1);
        var body = Ansi.StripStyles(Ansi.TruncateVisible(text, width));
        return Row(body, width, palette, palette.EditorBackground, first, placeholder);
    }

    internal static string EditorPadding(int terminalWidth, CliPalette palette) =>
        Padding(Math.Max(1, terminalWidth - Marker.Length - 1), palette.EditorBackground);

    internal static List<string> HistoryRows(string source, int contentWidth, CliPalette palette)
    {
        var body = MarkdownLayout.Wrap([new(source)], contentWidth, literal: true);
        var background = palette.HistoryBackground;
        var rows = new List<string>(body.Count + 2) { Padding(contentWidth, background) };
        for (var i = 0; i < body.Count; i++)
            rows.Add(Row(body[i], contentWidth, palette, background, first: i == 0));
        rows.Add(Padding(contentWidth, background));
        return rows;
    }

    private static string Row(string body, int contentWidth, CliPalette palette, string background, bool first, bool placeholder = false)
    {
        var prefix = first ? palette.Muted + Ansi.Bold + Marker + ResetIntensity + palette.Text : Indent;
        var text = placeholder ? palette.Muted + body + palette.Text : body;
        if (background.Length == 0) return prefix + text;
        var padding = new string(' ', Math.Max(0, contentWidth - TextWidth.Of(body)));
        return background + palette.Text + prefix + text + padding + Ansi.Reset;
    }

    private static string Padding(int contentWidth, string background) => background.Length == 0
        ? ""
        : new string(' ', contentWidth + Indent.Length).Style(background);
}
