using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.SubAgents;

public sealed class SubAgentControlTool(IPromptProvider prompts, SubAgentRunner runner, SubAgentTaskManager tasks) : BuiltinTool(prompts)
{
    public override string Name => "subagent";
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("spawn | list | status | wait | send | cancel"), true),
        ("id", ToolSchema.String("Persistent sub-agent task ID"), false),
        ("role", ToolSchema.String("researcher | coder | tester | general"), false),
        ("task", ToolSchema.String("Self-contained task or follow-up instruction"), false),
        ("model", ToolSchema.String("Optional provider/model override"), false),
        ("worktree", ToolSchema.Boolean("Run in a dedicated Git worktree"), false),
        ("wait_ms", ToolSchema.Integer("Bounded wait, maximum 60000 ms"), false));
    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        if (context.SubAgentDepth > 0) return ToolResult.Fail("Sub-agents cannot control other sub-agents.");
        var parent = SubAgentRunner.Parent(context);
        var id = GetString(args, "id") ?? "";
        JsonNode result = GetString(args, "action") switch
        {
            "spawn" => await runner.StartAsync(new SubAgentTaskRequest(GetString(args, "role") ?? "general",
                GetString(args, "task") ?? "", GetString(args, "model"), GetBool(args, "worktree")), context, ct).ConfigureAwait(false),
            "send" => await runner.SendAsync(id, GetString(args, "task") ?? "", context, ct).ConfigureAwait(false),
            "list" => tasks.List(parent),
            "status" => tasks.Get(parent, id),
            "wait" => await tasks.WaitAsync(parent, id, GetInt(args, "wait_ms") ?? 1000, ct).ConfigureAwait(false),
            "cancel" => await tasks.CancelAsync(parent, id, ct).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown sub-agent action."),
        };
        return ToolResult.Ok(result.ToJsonString(), mutated: false);
    }
}
