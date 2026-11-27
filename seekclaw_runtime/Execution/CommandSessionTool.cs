using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Execution;

public sealed class CommandSessionTool(IPromptProvider prompts, CommandSessionManager sessions) : BuiltinTool(prompts)
{
    internal const string ToolName = "command_session";
    public override string Name => ToolName;
    public override bool Mutating => true;
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("start | read | write | terminate | close | list"), true),
        ("session_id", ToolSchema.String("Returned command session ID"), false),
        ("command", ToolSchema.String(VendoredPowerShell.IsRequired
            ? "PowerShell 7 command to start (with or without a pseudo terminal)" : "Command to start"), false),
        ("cwd", ToolSchema.String("Directory relative to workspace"), false),
        ("pty", ToolSchema.Boolean("Use a real pseudo terminal (default false)"), false),
        ("text", ToolSchema.String("Exact stdin text, including any required newline"), false),
        ("eof", ToolSchema.Boolean("Close stdin after writing"), false),
        ("cursor", ToolSchema.Integer("Output cursor returned by the previous read"), false),
        ("wait_ms", ToolSchema.Integer("Wait up to 60000 ms for new output or exit when reading or writing"), false),
        ("timeout_seconds", ToolSchema.Integer("Maximum lifetime, default 1800 seconds"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var owner = context.SessionId;
        var id = GetString(args, "session_id") ?? "";
        var action = GetString(args, "action");
        if (!TryReadCursor(args, out var cursor)) return ToolResult.Fail("cursor must be an integer.");
        var wait = GetInt(args, "wait_ms") ?? 0;
        var budget = context.MaxOutputChars;
        JsonNode result;
        try
        {
            result = action switch
            {
                "start" => await sessions.StartAsync(owner, GetString(args, "command") ?? "",
                    context.ResolvePath(GetString(args, "cwd") ?? "."), GetInt(args, "timeout_seconds") ?? 1800,
                    GetBool(args, "pty"), ct, budget).ConfigureAwait(false),
                "read" => await sessions.ReadAsync(owner, id, cursor, wait, ct, budget).ConfigureAwait(false),
                "write" => await sessions.WriteAsync(owner, id, GetString(args, "text") ?? "", GetBool(args, "eof"),
                    ct, cursor, wait, budget).ConfigureAwait(false),
                "terminate" or "close" => await sessions.StopAsync(owner, id, action == "close", ct, budget).ConfigureAwait(false),
                "list" => sessions.List(owner, budget),
                _ => throw new ArgumentException("Unknown command session action."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or KeyNotFoundException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ToolResult.Fail(ex.Message);
        }
        return FormatResult(result, mutated: action is "start" or "write",
            stopping: action is "terminate" or "close");
    }

    internal static bool TryReadCursor(JsonObject args, out long? cursor)
    {
        cursor = null;
        if (args["cursor"] is not { } value) return true;
        if (value is not JsonValue number) return false;
        if (number.TryGetValue<long>(out var position)) cursor = position;
        else if (number.TryGetValue<int>(out var integer)) cursor = integer;
        return cursor.HasValue;
    }

    internal static ToolResult FormatResult(JsonNode result, bool mutated, bool stopping = false)
    {
        if (result is JsonArray sessions)
            return ToolResult.Ok(sessions.ToJsonString(), $"Command sessions: {sessions.Count}", mutated: mutated);

        var status = result["status"]?.GetValue<string>();
        var exitCode = result["exitCode"]?.GetValue<int>();
        var (success, summary) = status switch
        {
            "running" when string.IsNullOrWhiteSpace(result["output"]?.GetValue<string>())
                => (true, "Command still running (no new output)"),
            "running" => (true, "Command still running"),
            "exited" when exitCode is int code => (code == 0, $"Command exited with code {code}"),
            "cancelled" when stopping => (true, "Command stopped"),
            "cancelled" => (false, "Command was cancelled"),
            "timed_out" => (false, "Command timed out"),
            "error" => (false, "Command session failed"),
            _ => (false, "Command state could not be determined"),
        };
        return new ToolResult { Success = success, Output = result.ToJsonString(), Summary = summary, Mutated = mutated };
    }
}
