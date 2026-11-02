using System.Text.Json.Nodes;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Skills;

public sealed class LoadSkillTool(IPromptProvider prompts, SkillManager skills) : BuiltinTool(prompts)
{
    public override string Name => "load_skill";
    public override bool RequiresWorkspace => false;
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("name", ToolSchema.String("Name from the available skills catalog"), true),
        ("file", ToolSchema.String("Optional supporting file relative to the skill directory"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return Task.FromResult(ToolResult.Ok(skills.Load(context.Workspace,
            GetString(arguments, "name") ?? "", GetString(arguments, "file")))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}
