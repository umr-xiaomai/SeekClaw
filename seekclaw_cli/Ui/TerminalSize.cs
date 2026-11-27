namespace SeekClaw.Cli.Ui;

internal readonly record struct TerminalSize(int Width, int Height)
{
    public static TerminalSize Read()
    {
        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            return new(width > 0 ? width : 80, height > 0 ? height : 25);
        }
        catch (IOException)
        {
            return new(80, 25);
        }
    }
}
