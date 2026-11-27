using System.Globalization;

namespace SeekClaw.Cli.Ui;

/// <summary>Identifies the CLI and current workspace at the top of scrollback.</summary>
public static class Banner
{
    public const string Version = "2.2.36";

    public sealed record Info(string Workspace, string Session, bool Resumed);

    public static IReadOnlyList<string> Build(Info info) => Build(info, TerminalSize.Read().Width);

    internal static IReadOnlyList<string> Build(Info info, int terminalWidth)
    {
        var width = Math.Max(1, terminalWidth - 1);
        var title = "  " + ">_ ".Style(Ansi.Cyan) + "SeekClaw".Style(Ansi.Bold) + $" (v{Version})".Style(Ansi.Dim);
        const string indent = "     ";
        var directory = InlineText(info.Workspace);
        var rows = new List<string> { "", Ansi.TruncateVisible(title, width) };
        if (width > indent.Length)
            rows.Add(indent + FitPath(directory, width - indent.Length).Style(Ansi.Dim));
        else
            rows.Add(FitPath(directory, width).Style(Ansi.Dim));

        if (info.Resumed)
            rows.Add(Ansi.TruncateVisible("  " + $"Resumed {InlineText(info.Session)}".Style(Ansi.Dim), width));
        return rows;
    }

    private static string InlineText(string text) => Ansi.StripControls(text).Replace('\n', ' ').Replace('\t', ' ');

    private static string FitPath(string path, int width)
    {
        if (TextWidth.Of(path) <= width) return path;

        var boundaries = StringInfo.ParseCombiningCharacters(path);
        var first = path.Length;
        var used = 1;
        for (var i = boundaries.Length - 1; i >= 0; i--)
        {
            var next = boundaries[i];
            var columns = TextWidth.OfElement(path.AsSpan(next, first - next));
            if (used + columns > width) break;
            used += columns;
            first = next;
        }
        return "…" + path[first..];
    }
}
