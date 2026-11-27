namespace SeekClaw.Cli.Ui;

/// <summary>Shows the theme choices in the same terminal region as the chat input.</summary>
internal static class ThemePicker
{
    private static readonly (CliTheme Theme, string Description)[] Choices =
    [
        (CliTheme.Auto, "Follow the terminal"),
        (CliTheme.Light, "Light background"),
        (CliTheme.Dark, "Dark background"),
    ];

    internal static CliTheme? Read(TerminalRenderer renderer, CliTheme current, bool firstRun = false,
        CancellationToken cancellationToken = default)
    {
        var selected = Math.Max(0, Array.FindIndex(Choices, choice => choice.Theme == current));
        var width = 0;
        var drawnSelection = -1;
        var previousTreatCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var terminalWidth = TerminalSize.Read().Width;
                if (width != terminalWidth || drawnSelection != selected)
                {
                    width = terminalWidth;
                    drawnSelection = selected;
                    renderer.SetInputFrame(BuildFrame(width, selected, firstRun));
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(40);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return Choices[selected].Theme;
                if (key.Key == ConsoleKey.Escape) return firstRun ? CliTheme.Auto : null;
                if (key.KeyChar is '\x03' or '\x04') return null;

                selected = key.Key switch
                {
                    ConsoleKey.UpArrow => (selected + Choices.Length - 1) % Choices.Length,
                    ConsoleKey.DownArrow or ConsoleKey.Tab => (selected + 1) % Choices.Length,
                    ConsoleKey.D1 or ConsoleKey.NumPad1 => 0,
                    ConsoleKey.D2 or ConsoleKey.NumPad2 => 1,
                    ConsoleKey.D3 or ConsoleKey.NumPad3 => 2,
                    _ => selected,
                };
            }
            return null;
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatCtrlC;
            renderer.SetInputFrame(null);
        }
    }

    private static LineEditorFrame BuildFrame(int width, int selected, bool firstRun)
    {
        var lines = new List<string> { "  Choose a theme".Style(Ansi.Bold), "" };
        var firstOption = lines.Count;
        for (var i = 0; i < Choices.Length; i++)
        {
            var (theme, description) = Choices[i];
            var marker = i == selected ? "› ".Style(Ansi.Cyan) : "  ";
            var label = $"{i + 1}. {theme}";
            lines.Add("  " + marker + (i == selected ? label.Style(Ansi.Bold) : label)
                + new string(' ', 10 - label.Length) + description.Style(Ansi.Dim));
        }
        lines.Add("");
        lines.Add(LineEditor.FormatFooter(width, firstRun
            ? "↑/↓ select · Enter confirm · Esc use Auto"
            : "↑/↓ select · Enter confirm · Esc cancel"));
        return new LineEditorFrame(lines, lines.Count - firstOption - selected - 1, 2);
    }
}
