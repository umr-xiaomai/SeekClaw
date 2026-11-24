using System.Text.Json.Nodes;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Goals;

public sealed class GoalTool(IPromptProvider prompts, SessionGoalStore goals) : BuiltinTool(prompts)
{
    public const string ToolName = "session_goal";
    public override string Name => ToolName;
    public override bool RequiresWorkspace => false;
    public override string StatusLabel => "Updating goal";

    public override JsonObject ParameterSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("get", "create", "progress", "pause", "complete"),
            },
            ["objective"] = Field("The user-authorized outcome. Required for create."),
            ["criteria"] = Field("Concrete, verifiable requirements for completion. Required for create."),
            ["progress"] = Field("A concise checkpoint with finished work, relevant paths and remaining steps. Required for progress."),
            ["reason"] = Field("What prevents further progress and what is needed to resume. Required for pause."),
            ["evidence"] = Field("Verification that all completion criteria were met. Required for complete."),
        },
        ["required"] = new JsonArray("action"),
    };

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.SubAgentDepth > 0 || context.GoalExecution is not { } execution || string.IsNullOrEmpty(context.SessionId))
            return Task.FromResult(ToolResult.Fail("Goal management is available on foreground conversation turns."));

        var workspace = context.SessionWorkspace ?? context.Workspace;
        var goal = goals.Get(workspace, context.SessionId);
        var action = GetString(arguments, "action");
        if (action == "get") return Task.FromResult(ToolResult.Ok(goal?.ToJson().ToJsonString() ?? "No goal set."));

        SessionGoal? updated;
        try
        {
            if (action == "create")
            {
                updated = goals.Create(workspace, context.SessionId,
                    GetString(arguments, "objective"), GetString(arguments, "criteria"));
            }
            else if (goal is { Status: SessionGoal.Active })
            {
                if (action == "complete")
                {
                    execution.RequestCompletion(goal.Id, GetString(arguments, "evidence"));
                    return Task.FromResult(ToolResult.Ok(
                        "Completion requested. Summarize the outcome; the runtime will save completion after its checks.",
                        "Goal completion requested"));
                }
                updated = action switch
                {
                    "progress" => goals.RecordProgress(workspace, context.SessionId, goal.Id, GetString(arguments, "progress")),
                    "pause" => goals.Pause(workspace, context.SessionId, goal.Id, GetString(arguments, "reason")),
                    _ => throw new ArgumentException("Unknown goal action."),
                };
            }
            else return Task.FromResult(ToolResult.Fail("No active goal. A paused goal can be resumed by the user."));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }

        if (updated is null) return Task.FromResult(ToolResult.Fail("The goal state changed. Read it before updating."));
        context.Events.Publish(new SessionGoalChangedEvent(updated));
        return Task.FromResult(ToolResult.Ok(updated.ToJson().ToJsonString(), $"Goal {updated.Status}"));
    }

    private static JsonObject Field(string description) => new() { ["type"] = "string", ["description"] = description };
}
