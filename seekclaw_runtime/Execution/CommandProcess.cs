using System.Diagnostics;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Execution;

/// <summary>Transport boundary for ordinary pipes and platform pseudo terminals.</summary>
internal sealed class CommandProcess(Process process, Stream input, Stream output, Stream? error,
    bool pty, bool processGroupLeader = false, IDisposable? console = null) : IDisposable
{
    private IDisposable? _console = console;
    public Process Process { get; } = process;
    public Stream Input { get; } = input;
    public Stream Output { get; } = output;
    public Stream? Error { get; } = error;
    public bool Pty { get; } = pty;
    public bool IsProcessGroupLeader { get; } = processGroupLeader;

    public static CommandProcess Start(string command, string directory, bool pty)
    {
        if (pty && OperatingSystem.IsWindows()) return WindowsPseudoConsole.Start(command, directory);
        var (shell, args) = BashTool.ResolveShell(command);
        if (pty)
        {
            var script = new[] { "/usr/bin/script", "/bin/script" }.FirstOrDefault(File.Exists)
                ?? throw new PlatformNotSupportedException("PTY sessions require the platform 'script' utility.");
            var shellCommand = "/bin/sh -c " + "'" + command.Replace("'", "'\\''") + "'";
            args = OperatingSystem.IsMacOS() ? ["-q", "/dev/null", "/bin/sh", "-c", command]
                : ["-q", "-e", "-f", "-c", shellCommand, "/dev/null"];
            shell = script;
            // Establish a process group for reliable cancellation of all PTY descendants.
            if (File.Exists("/usr/bin/setsid")) { args = [script, .. args]; shell = "/usr/bin/setsid"; }
        }
        var start = new ProcessStartInfo(shell)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new IOException("Could not start command.");
        return new CommandProcess(process, process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            process.StandardError.BaseStream, pty, Path.GetFileName(shell) == "setsid");
    }

    public void ClosePseudoConsole() => Interlocked.Exchange(ref _console, null)?.Dispose();
    public void Dispose()
    {
        ClosePseudoConsole();
        Input.Dispose(); Output.Dispose(); Error?.Dispose(); Process.Dispose();
    }
}
