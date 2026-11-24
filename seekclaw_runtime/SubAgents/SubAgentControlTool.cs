using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.SubAgents;

public sealed class SubAgentControlTool(IPromptProvider prompts, SubAgentRunner runner, SubAgentTaskManager tasks) : BuiltinTool(prompts)
{
    public override string Name => "subagent";
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("spawn | list | status | wait | send | followup | cancel"), true),
        ("id", ToolSchema.String("Persistent sub-agent task ID"), false),
        ("role", ToolSchema.String("researcher | coder | tester | general"), false),
        ("name", ToolSchema.String("Optional short display name for the sub-agent (e.g. 'Demo explainer')"), false),
        ("task", ToolSchema.String("Self-contained task or follow-up instruction"), false),
        ("model", ToolSchema.String("Optional provider/model override"), false),
        ("worktree", ToolSchema.Boolean("Run in a dedicated Git worktree"), false),
        ("inherit_context", ToolSchema.Boolean("Include selected parent requirements and relevant results; defaults to true on spawn"), false),
        ("context", ToolSchema.String("Optional concise background to include with the delegated task"), false),
        ("wait_ms", ToolSchema.Integer("Bounded wait, maximum 60000 ms"), false));
    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        if (context.SubAgentDepth > 0) return ToolResult.Fail("Sub-agents cannot control other sub-agents.");
        var parent = SubAgentRunner.Parent(context);
        var id = GetString(args, "id") ?? "";
        var name = GetString(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = null;
        JsonNode result = GetString(args, "action") switch
        {
            "spawn" => await runner.StartAsync(new SubAgentTaskRequest(GetString(args, "role") ?? "general",
                GetString(args, "task") ?? "", GetString(args, "model"), GetBool(args, "worktree"), name,
                args["inherit_context"]?.GetValue<bool>() ?? true, GetString(args, "context")), context, ct).ConfigureAwait(false),
            "send" or "followup" => await runner.SendAsync(id, GetString(args, "task") ?? "", context, ct).ConfigureAwait(false),
            "list" => tasks.List(parent),
            "status" => tasks.Get(parent, id),
            "wait" => await tasks.WaitAsync(parent, id, GetInt(args, "wait_ms") ?? 1000, ct).ConfigureAwait(false),
            "cancel" => await tasks.CancelAsync(parent, id, ct).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown sub-agent action."),
        };
        return ToolResult.Ok(result.ToJsonString(), mutated: false);
    }
}
