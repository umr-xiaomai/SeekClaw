using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Execution;

internal sealed class ExecCommandTool(IPromptProvider prompts, CommandSessionManager sessions) : BuiltinTool(prompts)
{
    public override string Name => "exec_command";
    public override string StatusLabel => "Running command";
    public override bool Mutating => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("cmd", ToolSchema.String(VendoredPowerShell.IsRequired ? "PowerShell 7 command" : "Shell command"), true),
        ("workdir", ToolSchema.String("Working directory inside the workspace (defaults to the current project directory)"), false),
        ("tty", ToolSchema.Boolean("Allocate a terminal and keep stdin open for prompts such as SSH (default false)"), false),
        ("yield_time_ms", ToolSchema.Integer("Wait up to this many milliseconds before returning a live session ID (default 10000, maximum 30000)"), false),
        ("timeout_seconds", ToolSchema.Integer("Maximum process lifetime, independent of yield (default 1800, maximum 86400)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var command = GetString(args, "cmd");
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Fail("cmd is required.");

        try
        {
            if (context.Workspace.SshTarget is { } sshTarget)
            {
                return await SeekClaw.Runtime.Ssh.SshRemoteToolExecutor.ExecShellAsync(
                    sshTarget, command, GetString(args, "workdir"), GetInt(args, "timeout_seconds") ?? 1_800, context, ct)
                    .ConfigureAwait(false);
            }

            var directory = context.ResolvePath(GetString(args, "workdir") ?? ".");
            if (!Directory.Exists(directory)) return ToolResult.Fail($"Working directory not found: {directory}");

            var tty = GetBool(args, "tty");
            var result = await sessions.ExecAsync(context.SessionId, command, directory,
                GetInt(args, "timeout_seconds") ?? 1_800, tty, GetInt(args, "yield_time_ms") ?? 10_000, ct, context.MaxOutputChars)
                .ConfigureAwait(false);
            return CommandSessionTool.FormatResult(result,
                mutated: tty || !BashTool.IsReadOnlyCommand(command));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
