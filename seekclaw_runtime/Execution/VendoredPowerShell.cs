using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeekClaw.Runtime.Execution;

internal static class VendoredPowerShell
{
    public static IReadOnlyDictionary<string, string?> ChildEnvironment { get; } = new Dictionary<string, string?>
    {
        ["PSModuleAnalysisCachePath"] = "NUL",
        ["POWERSHELL_UPDATECHECK"] = "Off",
        ["POWERSHELL_TELEMETRY_OPTOUT"] = "1",
    };

    public static bool IsRequired => OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public static string ResolveExecutable() => ResolveExecutable(
        AppContext.BaseDirectory, CanRun, Environment.GetEnvironmentVariable("PATH"));

    internal static string ResolveExecutable(string baseDirectory, Func<string, bool> canRun, string? searchPath = null)
    {
        var appDirectory = new DirectoryInfo(baseDirectory);
        var packaged = Path.Combine(appDirectory.FullName, "powershell", "win-x64", "pwsh.exe");
        if (canRun(packaged)) return packaged;

        for (DirectoryInfo? ancestor = appDirectory; ancestor is not null; ancestor = ancestor.Parent)
        {
            var repoCopy = Path.Combine(ancestor.FullName, "seekclaw_third_party", "powershell", "win-x64", "pwsh.exe");
            if (canRun(repoCopy)) return repoCopy;
        }

        foreach (var entry in (searchPath ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var systemCopy = Path.Combine(directory, "pwsh.exe");
            if (canRun(systemCopy)) return systemCopy;
        }

        throw new FileNotFoundException(
            $"No usable PowerShell 7 executable was found. Reinstall the Windows x64 SeekClaw package " +
            $"to restore '{packaged}', restore 'seekclaw_third_party/powershell/win-x64/' in the source checkout, " +
            "or install PowerShell 7 and make pwsh.exe available on PATH.", packaged);
    }

    private static bool CanRun(string executable)
    {
        if (!File.Exists(executable)) return false;

        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var (name, value) in ChildEnvironment) start.Environment[name] = value;
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-Command", "if ($PSVersionTable.PSVersion.Major -ge 7) { exit 0 }; exit 1" })
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) return false;
            process.StandardInput.Close();
            if (process.WaitForExit(5000)) return process.ExitCode == 0;
            process.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    public static (string Shell, string[] Args) ResolveCommand(string command) =>
        (ResolveExecutable(), ["-NoProfile", "-NonInteractive", "-NoLogo", "-Command", WithUtf8Output(command)]);

    public static string WithUtf8Output(string command) =>
        $"try {{ [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 }} catch {{ }}; {command}";
}
