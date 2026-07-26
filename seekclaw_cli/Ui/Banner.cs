using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>
/// Startup splash: gradient ASCII-art logo + rounded info panel, Claude-Code style.
/// Falls back to a compact wordmark on narrow terminals.
/// </summary>
public static class Banner
{
    public const string Version = "0.1.0";

    // Gradient endpoints: cyan → violet, applied diagonally across the logo.
    private static readonly (int R, int G, int B) From = (34, 211, 238);
    private static readonly (int R, int G, int B) To = (167, 139, 250);

    private static readonly string[] Logo =
    [
        "███████╗███████╗███████╗██╗  ██╗ ██████╗██╗      █████╗ ██╗    ██╗",
        "██╔════╝██╔════╝██╔════╝██║ ██╔╝██╔════╝██║     ██╔══██╗██║    ██║",
        "███████╗█████╗  █████╗  █████╔╝ ██║     ██║     ███████║██║ █╗ ██║",
        "╚════██║██╔══╝  ██╔══╝  ██╔═██╗ ██║     ██║     ██╔══██║██║███╗██║",
        "███████║███████╗███████╗██║  ██╗╚██████╗███████╗██║  ██║╚███╔███╔╝",
        "╚══════╝╚══════╝╚══════╝╚═╝  ╚═╝ ╚═════╝╚══════╝╚═╝  ╚═╝ ╚══╝╚══╝ ",
    ];

    public sealed record Info(string Model, string Workspace, string ProjectKinds, string Session, bool Resumed);

    public static IReadOnlyList<string> Build(Info info)
    {
        var width = SafeWidth();
        var lines = new List<string> { "" };

        if (width >= Logo[0].Length + 2)
            lines.AddRange(Logo.Select((row, i) => " " + Gradient(row, i, Logo.Length)));
        else
            lines.Add(" " + Gradient("✻ S E E K C L A W ✻", 0, 1));

        lines.Add("");
        lines.AddRange(BuildPanel(info, width));
        return lines;
    }

    // ---------------------------------------------------------------- info panel

    private static IEnumerable<string> BuildPanel(Info info, int width)
    {
        var rows = new List<string>
        {
            "✻ ".Style(Ansi.Rgb(To.R, To.G, To.B)) + "Welcome to SeekClaw".Style(Ansi.Bold) + $"  v{Version}".Style(Ansi.Gray),
            "",
            Row("model", info.Model.Style(Ansi.Cyan)),
            Row("workspace", info.Workspace + (info.ProjectKinds.Length > 0 ? $"  [{info.ProjectKinds}]".Style(Ansi.Gray) : "")),
            Row("session", info.Session + (info.Resumed ? " (resumed)".Style(Ansi.Yellow) : " (new)".Style(Ansi.Gray))),
            "",
            "/ commands · ↑/↓ history · tab complete · ctrl+c cancel/exit".Style(Ansi.Gray),
        };

        var inner = Math.Min(Math.Max(rows.Max(VisibleWidth), 40) + 2, width - 4);
        var border = Ansi.Gray;

        yield return (" ╭" + new string('─', inner + 2) + "╮").Style(border);
        foreach (var row in rows)
        {
            var clipped = ClipToWidth(row, inner);
            var padding = inner - VisibleWidth(clipped);
            yield return " │ ".Style(border) + clipped + new string(' ', Math.Max(0, padding)) + " │".Style(border);
        }
        yield return (" ╰" + new string('─', inner + 2) + "╯").Style(border);

        static string Row(string label, string value) =>
            label.PadRight(11).Style(Ansi.Dim) + value;
    }

    // ---------------------------------------------------------------- helpers

    private static string Gradient(string text, int row, int totalRows)
    {
        var sb = new StringBuilder(text.Length * 12);
        var columns = Math.Max(1, text.Length - 1);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                sb.Append(' ');
                continue;
            }
            // Diagonal blend: mostly horizontal, a touch of vertical.
            var t = 0.8 * i / columns + 0.2 * (totalRows <= 1 ? 0 : (double)row / (totalRows - 1));
            sb.Append(Ansi.Rgb(Lerp(From.R, To.R, t), Lerp(From.G, To.G, t), Lerp(From.B, To.B, t)))
              .Append(text[i]);
        }
        sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    private static int Lerp(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);

    private static int VisibleWidth(string text) => TextWidth.Of(Ansi.StripStyles(text));

    /// <summary>Trims styled text to a display width, keeping escape sequences intact.</summary>
    private static string ClipToWidth(string text, int maxWidth)
    {
        if (VisibleWidth(text) <= maxWidth) return text;

        var sb = new StringBuilder();
        var used = 0;
        var i = 0;
        while (i < text.Length && used < maxWidth - 1)
        {
            if (text[i] == '\x1b')
            {
                var end = text.IndexOf('m', i);
                if (end < 0) break;
                sb.Append(text, i, end - i + 1);
                i = end + 1;
                continue;
            }

            var next = i + (char.IsSurrogatePair(text, Math.Min(i, text.Length - 2)) && i + 1 < text.Length ? 2 : 1);
            var w = TextWidth.Of(text[i..next]);
            if (used + w > maxWidth - 1) break;
            sb.Append(text, i, next - i);
            used += w;
            i = next;
        }
        return sb.Append(Ansi.Reset).Append('…').ToString();
    }

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch (IOException) { return 100; }
    }
}
