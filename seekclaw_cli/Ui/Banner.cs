using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>
/// Startup splash: gradient ASCII-art logo + rounded info panel, Claude-Code style.
/// Falls back to a compact wordmark on narrow terminals.
/// </summary>
public static class Banner
{
    public const string Version = "0.2.0";

    // SeekClaw Cyber Cyan to Violet gradient colors
    private static readonly (int R, int G, int B) Cyan = (34, 211, 238);
    private static readonly (int R, int G, int B) Violet = (167, 139, 250);

    private static readonly string[] Logo =
    [
        "███████╗███████╗███████╗██╗  ██╗ ██████╗██╗      █████╗ ██╗    ██╗",
        "██╔════╝██╔════╝██╔════╝██║ ██╔╝██╔════╝██║     ██╔══██╗██║    ██║",
        "███████╗█████╗  █████╗  █████╔╝ ██║     ██║     ███████║██║ █╗ ██║",
        "╚════██║██╔══╝  ██╔══╝  ██╔═██╗ ██║     ██║     ██╔══██║██║███╗██║",
        "███████║███████╗███████╗██║  ██╗╚██████╗███████╗██║  ██║╚███╔███╔╝",
        "╚══════╝╚══════╝╚══════╝╚═╝  ╚═╝ ╚═════╝╚══════╝╚═╝  ╚═╝ ╚══╝╚══╝ ",
    ];

    public sealed record Info (string Model, string Workspace, string ProjectKinds, string Session, bool Resumed);

    public static IReadOnlyList<string> Build (Info info)
    {
        var width = SafeWidth();
        var lines = new List<string> { "" };
        lines.AddRange(BuildPanel(info, width));
        return lines;
    }

    private static IEnumerable<string> BuildPanel (Info info, int width)
    {
        var logoWidth = Logo[0].Length;
        var totalWidth = Math.Clamp(Math.Max(width - 2, logoWidth + 6), 76, 110);
        var borderStyle = Ansi.Rgb(Cyan.R, Cyan.G, Cyan.B);

        // Header: ╭── SEEKCLAW v0.1.0 ──────────────────────────────────────────╮
        var headerTitle = " SEEKCLAW ".Style(Ansi.Rgb(Cyan.R, Cyan.G, Cyan.B) + Ansi.Bold) + $"v{Version} ".Style(Ansi.Gray);
        var headerWidth = VisibleWidth(headerTitle);
        var remainingDashes = Math.Max(0, totalWidth - headerWidth - 4);
        var topBorder = "╭──".Style(borderStyle) + headerTitle + new string('─', remainingDashes).Style(borderStyle) + "╮".Style(borderStyle);

        var innerWidth = totalWidth - 4;
        var output = new List<string> { topBorder };

        // 1. Render Gradient Logo in Header Panel (if terminal width is sufficient)
        if (innerWidth >= logoWidth)
        {
            var logoPadding = (innerWidth - logoWidth) / 2;
            var padStr = new string(' ', logoPadding);

            for (var i = 0; i < Logo.Length; i++)
            {
                var rowGrad = GradientRow(Logo[i], i, Logo.Length);
                var rowClipped = ClipToWidth(padStr + rowGrad, innerWidth);
                var rightPad = Math.Max(0, innerWidth - VisibleWidth(rowClipped));
                output.Add("│  ".Style(borderStyle) + rowClipped + new string(' ', rightPad) + "  │".Style(borderStyle));
            }

            // Divider line
            var dividerText = " SYSTEM ENVIRONMENT & QUICK COMMANDS ";
            var remDiv = Math.Max(0, totalWidth - VisibleWidth(dividerText) - 4);
            var divider = "├──".Style(borderStyle) + dividerText.Style(Ansi.Gray + Ansi.Bold) + new string('─', remDiv).Style(borderStyle) + "┤".Style(borderStyle);
            output.Add(divider);
        }

        // 2. Body Split: Left Column (Session & Model), Right Column (Quick Commands)
        var leftWidth = innerWidth / 2 - 1;
        var rightWidth = innerWidth - leftWidth - 1;

        var leftLines = new List<string>
        {
            Row("Model", info.Model.Style(Ansi.Cyan)),
            Row("Workspace", ShortenPath(info.Workspace, leftWidth - 12).Style(Ansi.Gray)),
            Row("Session", info.Session.Style(Ansi.Gray)),
            Row("Project", (info.ProjectKinds.Length > 0 ? info.ProjectKinds : "general").Style(Ansi.Rgb(Violet.R, Violet.G, Violet.B))),
        };

        var rightLines = new List<string>
        {
            CmdRow("/mode", "switch plan/readonly/edit/auto"),
            CmdRow("/mcp", "manage tool integrations"),
            CmdRow("/doctor", "run health diagnostics"),
            CmdRow("/help", "view available commands"),
        };

        var maxRows = Math.Max(leftLines.Count, rightLines.Count);

        for (var i = 0; i < maxRows; i++)
        {
            var leftText = i < leftLines.Count ? leftLines[i] : "";
            var rightText = i < rightLines.Count ? rightLines[i] : "";

            var leftClipped = ClipToWidth(leftText, leftWidth);
            var leftPad = Math.Max(0, leftWidth - VisibleWidth(leftClipped));

            var rightClipped = ClipToWidth(rightText, rightWidth);
            var rightPad = Math.Max(0, rightWidth - VisibleWidth(rightClipped));

            var row = "│  ".Style(borderStyle) +
                      leftClipped + new string(' ', leftPad) +
                      "│ ".Style(borderStyle) +
                      rightClipped + new string(' ', rightPad) +
                      "│".Style(borderStyle);

            output.Add(row);
        }

        // Bottom border: ╰────────────────────────────────────────────────────────────╯
        var bottomBorder = "╰".Style(borderStyle) + new string('─', totalWidth - 2).Style(borderStyle) + "╯".Style(borderStyle);
        output.Add(bottomBorder);

        return output;

        static string Row (string label, string value) =>
            label.PadRight(10).Style(Ansi.Gray) + " " + value;

        static string CmdRow (string cmd, string desc) =>
            cmd.PadRight(8).Style(Ansi.Rgb(Cyan.R, Cyan.G, Cyan.B) + Ansi.Bold) + " " + desc.Style(Ansi.Gray);
    }

    private static string GradientRow (string text, int row, int totalRows)
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
            var t = 0.8 * i / columns + 0.2 * (totalRows <= 1 ? 0 : (double)row / (totalRows - 1));
            sb.Append(Ansi.Rgb(Lerp(Cyan.R, Violet.R, t), Lerp(Cyan.G, Violet.G, t), Lerp(Cyan.B, Violet.B, t)))
              .Append(text[i]);
        }
        sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    private static int Lerp (int from, int to, double t) => (int)Math.Round(from + (to - from) * t);

    private static string CenterText (string text, int targetWidth)
    {
        var vis = VisibleWidth(text);
        if (vis >= targetWidth) return text;
        var left = (targetWidth - vis) / 2;
        return new string(' ', left) + text;
    }

    private static string ShortenPath (string path, int maxLen)
    {
        if (path.Length <= maxLen) return path;
        return "…" + path[^(maxLen - 1)..];
    }

    private static int VisibleWidth (string text) => TextWidth.Of(Ansi.StripStyles(text));

    private static string ClipToWidth (string text, int maxWidth)
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

    private static int SafeWidth ()
    {
        try { return Math.Max(70, Console.WindowWidth); }
        catch (IOException) { return 90; }
    }
}
