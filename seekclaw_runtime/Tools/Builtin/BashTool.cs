using System.Text;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

/// <summary>Runs a shell command (bash when available, cmd.exe otherwise) inside the workspace.</summary>
public sealed class BashTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "bash";
    public override string StatusLabel => "Running command";
    public override bool Mutating => true; // a shell command may change anything

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("command", ToolSchema.String("Shell command to execute"), true),
        ("cwd", ToolSchema.String("Working directory (defaults to the workspace root)"), false),
        ("timeout_seconds", ToolSchema.Integer("Timeout in seconds (default from config)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command is required.");

        var cwd = context.ResolvePath(GetString(arguments, "cwd") ?? ".");
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Working directory not found: {cwd}");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            GetInt(arguments, "timeout_seconds") ?? context.Agent.BashTimeoutSeconds, 1, 3600));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var (shell, shellArgs) = ResolveShell(command);
        try
        {
            var result = await Cli.Wrap(shell)
                .WithArguments(shellArgs)
                .WithWorkingDirectory(cwd)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            var output = new StringBuilder();
            if (result.StandardOutput.Length > 0) output.Append(result.StandardOutput);
            if (result.StandardError.Length > 0)
            {
                if (output.Length > 0) output.AppendLine();
                output.Append(result.StandardError);
            }

            var text = output.Length == 0 ? "(no output)" : context.Truncate(output.ToString(), "command output");
            var summary = $"{Shorten(command)} → exit {result.ExitCode}";
            return result.ExitCode == 0
                ? ToolResult.Ok(text, summary)
                : new ToolResult { Success = false, Output = $"Exit code {result.ExitCode}\n{text}", Summary = summary };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Fail($"Command timed out after {timeout.TotalSeconds:0}s: {Shorten(command)}");
        }
    }

    private static (string Shell, string[] Args) ResolveShell(string command)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/bash", ["-c", command]);

        var bash = FindOnPath("bash.exe");
        if (bash is not null)
            return (bash, ["-c", command]);

        var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (pwsh is not null)
            return (pwsh, ["-NoProfile", "-NonInteractive", "-Command", command]);

        return ("cmd.exe", ["/d", "/s", "/c", command]);
    }

    private static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), fileName))
        .FirstOrDefault(File.Exists);

    private static string Shorten(string command) =>
        command.Length > 60 ? command[..60] + "…" : command;
}
