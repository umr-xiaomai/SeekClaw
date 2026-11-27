using System.Runtime.InteropServices;

namespace SeekClaw.Cli.Ui;

/// <summary>Checks for a live console and enables ANSI processing on Windows.</summary>
public static class VirtualTerminal
{
    private const int StdOutputHandle = -11;
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    public static bool TryEnable()
    {
        if (Console.IsOutputRedirected) return false;
        if (!OperatingSystem.IsWindows())
            return !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

        // A Windows pseudoconsole can support VT even when the inherited TERM is "dumb".
        var handle = GetStdHandle(StdOutputHandle);
        return GetConsoleMode(handle, out var mode) &&
            SetConsoleMode(handle, mode | EnableProcessedOutput | EnableVirtualTerminalProcessing);
    }
}
