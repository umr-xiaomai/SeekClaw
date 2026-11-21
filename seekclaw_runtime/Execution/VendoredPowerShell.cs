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

    public static string ResolveExecutable() => ResolveExecutable(AppContext.BaseDirectory, File.Exists);

    internal static string ResolveExecutable(string baseDirectory, Func<string, bool> fileExists)
    {
        var appDirectory = new DirectoryInfo(baseDirectory);
        var packaged = Path.Combine(appDirectory.FullName, "powershell", "win-x64", "pwsh.exe");
        if (fileExists(packaged)) return packaged;

        // Dev builds run under the checkout. Do not load a shell from the task workspace or PATH.
        for (DirectoryInfo? ancestor = appDirectory; ancestor is not null; ancestor = ancestor.Parent)
        {
            var repoCopy = Path.Combine(ancestor.FullName, "seekclaw_third_party", "powershell", "win-x64", "pwsh.exe");
            if (fileExists(repoCopy)) return repoCopy;
        }

        throw new FileNotFoundException(
            $"Vendored PowerShell 7 (Windows x64) is missing. Reinstall the Windows x64 SeekClaw package " +
            $"to restore '{packaged}', or restore the complete 'seekclaw_third_party/powershell/win-x64/' " +
            $"payload (including pwsh.exe) in the source checkout above '{appDirectory.FullName}'.", packaged);
    }

    public static (string Shell, string[] Args) ResolveCommand(string command) =>
        (ResolveExecutable(), ["-NoProfile", "-NonInteractive", "-NoLogo", "-Command", WithUtf8Output(command)]);

    public static string WithUtf8Output(string command) =>
        $"try {{ [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 }} catch {{ }}; {command}";
}
