using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Execution;

internal sealed class WriteStdinTool(IPromptProvider prompts, CommandSessionManager sessions) : BuiltinTool(prompts)
{
    public override string Name => "write_stdin";
    public override string StatusLabel => "Interacting with terminal";
    public override bool Mutating => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("session_id", ToolSchema.String("sessionId returned by exec_command or command_session"), true),
        ("chars", ToolSchema.String("Text to type, including a newline to submit a line. Omit or send an empty string to poll without input."), false),
        ("yield_time_ms", ToolSchema.Integer("Wait this long for new output (default 250, maximum 60000)"), false),
        ("cursor", ToolSchema.Integer("Output cursor returned by the previous call"), false));

    internal static bool IsPoll(JsonObject args) => string.IsNullOrEmpty(GetString(args, "chars"));

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var id = GetString(args, "session_id");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Fail("session_id is required.");
        var chars = GetString(args, "chars") ?? "";
        if (!CommandSessionTool.TryReadCursor(args, out var cursor)) return ToolResult.Fail("cursor must be an integer.");
        var wait = GetInt(args, "yield_time_ms") ?? 250;
        try
        {
            var result = chars.Length == 0
                ? await sessions.ReadAsync(context.SessionId, id, cursor, wait, ct, context.MaxOutputChars).ConfigureAwait(false)
                : await sessions.WriteAsync(context.SessionId, id, chars, closeInput: false, ct, cursor, wait, context.MaxOutputChars).ConfigureAwait(false);
            return CommandSessionTool.FormatResult(result, mutated: chars.Length > 0);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or ArgumentOutOfRangeException)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
