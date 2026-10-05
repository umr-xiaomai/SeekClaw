using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.SubAgents;

public sealed class InvokeSubAgentTool(
    IPromptProvider prompts,
    SubAgentRunner runner) : BuiltinTool(prompts)
{
    public override string Name => "invoke_subagent";
    public override string StatusLabel => "Deploying sub-agent";
    public override bool Mutating => false;
    public override bool RequiresWorkspace => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("role", ToolSchema.String("Specialist role of the sub-agent: 'researcher', 'coder', 'tester', or 'general'"), true),
        ("task", ToolSchema.String("Clear, self-contained task description and instructions for the sub-agent"), true),
        ("model", ToolSchema.String("Optional model override in 'provider/model' format (e.g. 'openai/gpt-4o-mini'). Omit to use the active or default model."), false)
    );

    public override async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        if (context.SubAgentDepth >= 1)
        {
            return ToolResult.Fail("Sub-agents cannot invoke further sub-agents. Maximum delegation depth reached.");
        }

        var role = GetString(arguments, "role")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(role))
            return ToolResult.Fail("The 'role' argument is required (e.g. 'researcher', 'coder', 'tester', 'general').");

        var task = GetString(arguments, "task")?.Trim();
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("The 'task' argument is required with concrete instructions.");

        var model = GetString(arguments, "model")?.Trim();

        var request = new SubAgentTaskRequest(role, task, model);
        var result = await runner.ExecuteSubTaskAsync(request, context, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return ToolResult.Fail(result.Error ?? "Sub-agent failed to complete the task.");
        }

        var summary = $"SubAgent [{role}] completed ({result.Elapsed.TotalSeconds:0.1}s)";
        return ToolResult.Ok(result.Output, summary: summary);
    }
}
