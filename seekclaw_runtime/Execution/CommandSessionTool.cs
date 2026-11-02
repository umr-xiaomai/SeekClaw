using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Execution;

public sealed class CommandSessionTool(IPromptProvider prompts, CommandSessionManager sessions) : BuiltinTool(prompts)
{
    public override string Name => "command_session";
    public override bool Mutating => true;
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("start | read | write | terminate | close | list"), true),
        ("session_id", ToolSchema.String("Returned command session ID"), false),
        ("command", ToolSchema.String("Command to start"), false),
        ("cwd", ToolSchema.String("Directory relative to workspace"), false),
        ("pty", ToolSchema.Boolean("Use a real pseudo terminal (default false)"), false),
        ("text", ToolSchema.String("Exact stdin text, including any required newline"), false),
        ("eof", ToolSchema.Boolean("Close stdin after writing"), false),
        ("cursor", ToolSchema.Integer("Output cursor returned by the previous read"), false),
        ("wait_ms", ToolSchema.Integer("Wait up to 60000 ms for completion"), false),
        ("timeout_seconds", ToolSchema.Integer("Maximum lifetime, default 1800 seconds"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var owner = context.SessionId;
        var id = GetString(args, "session_id") ?? "";
        var action = GetString(args, "action");
        JsonNode result = action switch
        {
            "start" => await sessions.StartAsync(owner, GetString(args, "command") ?? "",
                context.ResolvePath(GetString(args, "cwd") ?? "."), GetInt(args, "timeout_seconds") ?? 1800,
                GetBool(args, "pty"), ct).ConfigureAwait(false),
            "read" => await sessions.ReadAsync(owner, id, args["cursor"]?.GetValue<long>(), GetInt(args, "wait_ms") ?? 0, ct).ConfigureAwait(false),
            "write" => await sessions.WriteAsync(owner, id, GetString(args, "text") ?? "", GetBool(args, "eof"), ct).ConfigureAwait(false),
            "terminate" or "close" => await sessions.StopAsync(owner, id, action == "close", ct).ConfigureAwait(false),
            "list" => sessions.List(owner),
            _ => throw new ArgumentException("Unknown command session action."),
        };
        // Shorten output inside the envelope so JSON and the continuation cursor
        // remain valid even for small model context budgets or escape-heavy output.
        if (result is JsonObject item && item["output"]?.GetValue<string>() is { } output)
        {
            var from = item["cursor"]!.GetValue<long>() - output.Length;
            while (output.Length > 0 && item.ToJsonString().Length > context.MaxOutputChars)
            {
                output = output[..(output.Length / 2)];
                item["output"] = output;
                item["cursor"] = from + output.Length;
                item["hasMore"] = true;
            }
        }
        return ToolResult.Ok(result.ToJsonString(), mutated: action is "start" or "write");
    }
}
