using System.Globalization;
using System.Text.RegularExpressions;

namespace SeekClaw.Cli.Ui;

/// <summary>Colors for CLI content, resolved separately from the user's terminal settings.</summary>
public sealed partial class CliPalette
{
    public static CliPalette Auto { get; } = new();
    public static CliPalette Dark { get; } = new(light: false, useTerminalBackground: false);
    public static CliPalette Light { get; } = new(light: true, useTerminalBackground: false);
    private static CliPalette TerminalDark { get; } = new(light: false, useTerminalBackground: true);
    private static CliPalette TerminalLight { get; } = new(light: true, useTerminalBackground: true);

    public bool IsLight { get; }
    public string Text { get; } = "";
    public string Muted { get; } = "";
    public string Accent { get; } = Ansi.Cyan;
    public string Success { get; } = Ansi.Green;
    public string Warning { get; } = Ansi.Yellow;
    public string Error { get; } = Ansi.Red;
    internal string Heading { get; } = Ansi.Bold;
    internal string Emphasis => Heading;
    internal string Strong => Heading;
    internal string Link { get; } = Ansi.Underline;
    internal string Quote { get; } = Ansi.Italic;
    internal string OrderedMarker { get; } = "";
    internal string Bullet { get; } = "";
    internal string TableHeader { get; } = Ansi.Bold;
    internal string CodeText => Text;
    internal string CodePunctuation => Muted;
    internal string CodeFunction => Link;
    internal string CodeString { get; } = "";
    internal string CodeKeyword { get; } = Ansi.Bold;
    internal string CodeNumber { get; } = "";
    internal string CodeComment => Muted;
    internal string CodeVariable => Bullet;
    internal string EditorBackground { get; } = "";
    internal string HistoryBackground { get; } = "";
    private string Surface { get; } = "";

    private CliPalette() { }

    private CliPalette(bool light, bool useTerminalBackground)
    {
        IsLight = light;
        Text = light ? Ansi.Rgb(47, 51, 67) : Ansi.Rgb(222, 228, 247);
        Muted = light ? Ansi.Rgb(80, 85, 104) : Ansi.Rgb(168, 178, 207);
        Accent = light ? Ansi.Rgb(16, 89, 131) : Ansi.Rgb(137, 220, 235);
        Success = light ? Ansi.Rgb(38, 105, 51) : Ansi.Rgb(166, 227, 161);
        Warning = light ? Ansi.Rgb(131, 80, 19) : Ansi.Rgb(249, 226, 175);
        Error = light ? Ansi.Rgb(163, 23, 55) : Ansi.Rgb(243, 139, 168);
        Heading = Error;
        Link = light ? Ansi.Rgb(27, 80, 167) : Ansi.Rgb(137, 180, 250);
        Quote = light ? Ansi.Rgb(115, 50, 112) : Ansi.Rgb(245, 194, 231);
        OrderedMarker = Link;
        Bullet = light ? Ansi.Rgb(19, 98, 105) : Ansi.Rgb(148, 226, 213);
        TableHeader = Warning;
        CodeString = Success;
        CodeKeyword = light ? Ansi.Rgb(98, 44, 160) : Ansi.Rgb(203, 166, 247);
        CodeNumber = light ? Ansi.Rgb(145, 61, 18) : Ansi.Rgb(250, 179, 135);
        EditorBackground = light ? Ansi.BackgroundRgb(227, 232, 243) : Ansi.BackgroundRgb(36, 43, 59);
        HistoryBackground = light ? Ansi.BackgroundRgb(236, 239, 247) : Ansi.BackgroundRgb(34, 40, 54);
        if (!useTerminalBackground)
            Surface = Text + (light ? Ansi.BackgroundRgb(248, 249, 252) : Ansi.BackgroundRgb(23, 28, 40));
    }

    internal static CliPalette Default => ForTerminalBackground(Environment.GetEnvironmentVariable("COLORFGBG"));

    public static CliPalette ForTerminalBackground(string? colorFgBg)
    {
        var background = colorFgBg.AsSpan();
        var separator = background.LastIndexOf(';');
        if (separator < 0 || !int.TryParse(background[(separator + 1)..].Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var index)) return Auto;

        if (index is >= 0 and <= 15)
        {
            // Use conventional ANSI RGB values when only the index is available.
            ReadOnlySpan<int> standard = [0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xc0c0c0,
                0x808080, 0xff0000, 0x00ff00, 0xffff00, 0x0000ff, 0xff00ff, 0x00ffff, 0xffffff];
            var color = standard[index];
            return ForTerminalBackground(color >> 16, color >> 8 & 0xff, color & 0xff);
        }
        if (index is >= 16 and <= 231)
        {
            ReadOnlySpan<int> levels = [0, 95, 135, 175, 215, 255];
            var color = index - 16;
            return ForTerminalBackground(levels[color / 36], levels[color / 6 % 6], levels[color % 6]);
        }
        if (index is < 232 or > 255) return Auto;
        var gray = 8 + (index - 232) * 10;
        return ForTerminalBackground(gray, gray, gray);
    }

    public static CliPalette ForTerminalBackground(int red, int green, int blue)
    {
        // Relative luminance with the crossover where dark and light text have
        // comparable contrast. Inputs are 8-bit channels from the terminal.
        var luminance = .2126 * Linear(red) + .7152 * Linear(green) + .0722 * Linear(blue);
        return ForLuminance(luminance > .18);

        static double Linear(int channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
    }

    private static CliPalette ForLuminance(bool light) => light ? TerminalLight : TerminalDark;

    /// <summary>Applies colors to an application-owned row and restores terminal defaults afterwards.</summary>
    public string StyleRow(string row) => StyleRow(row, fill: false);

    internal string PaintRow(string row) => StyleRow(row, fill: true);

    private string StyleRow(string row, bool fill)
    {
        if (row.Length == 0 && !fill) return row;
        var styled = LegacyStyles().Replace(row, match => match.Value switch
        {
            Ansi.Reset => Surface.Length == 0 ? Ansi.Reset : Ansi.Reset + Surface,
            Ansi.Dim or Ansi.Gray => Muted,
            Ansi.Red => Error,
            Ansi.Green => Success,
            Ansi.Yellow => Warning,
            Ansi.Blue => Link,
            Ansi.Magenta => CodeKeyword,
            Ansi.Cyan => Accent,
            _ => match.Value,
        });
        if (Surface.Length == 0) return styled;

        // Painting the remaining cells via EL does not append padding to scrollback
        // or consume columns that would wrap at the right edge of the terminal.
        var end = fill ? Surface + "\x1b[K" : "";
        return Surface + styled + end + Ansi.Reset;
    }

    [GeneratedRegex(@"\x1b\[(?:0|2|3[1-6]|90)m")]
    private static partial Regex LegacyStyles();
}
