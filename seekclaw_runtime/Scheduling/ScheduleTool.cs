using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Presentation;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Scheduling;

public sealed class ScheduleTool(IPromptProvider prompts, IScheduleStore schedules) : BuiltinTool(prompts)
{
    public override string Name => "schedule";
    public override bool Mutating => true;
    public override bool RequiresWorkspace => false;
    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("action", ToolSchema.String("upsert | list | pause | resume | complete | delete"), true),
        ("id", ToolSchema.String("Existing schedule ID; omit when creating"), false),
        ("name", ToolSchema.String("Task name"), false),
        ("prompt", ToolSchema.String("Durable instructions, what counts as a meaningful change, and when to stop"), false),
        ("cron", ToolSchema.String("Five-field cron expression in the host's local time zone"), false),
        ("continue_context", ToolSchema.Boolean("Continue this conversation (default true); false creates a new conversation per run"), false),
        ("worktree", ToolSchema.Boolean("Use a dedicated worktree"), false),
        ("notification", ToolSchema.String("changes (default) | failures | always"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = GetString(args, "id");
        var workspace = context.SessionWorkspace ?? context.Workspace;
        var source = workspace.IsGlobal ? null : workspace.Root;
        var existing = id is null ? null : schedules.Get(id) ?? throw new KeyNotFoundException("Schedule not found.");
        if (existing is not null && (existing.Workspace != source || existing.SessionId is not null && existing.SessionId != context.SessionId))
            return Task.FromResult(ToolResult.Fail("This schedule belongs to another workspace or conversation; manage it from the scheduled tasks interface."));
        var action = GetString(args, "action");
        string output;
        ScheduledTask? presented = null;
        switch (action)
        {
            case "upsert":
                var continuation = args["continue_context"]?.GetValue<bool>() ?? (existing is null || existing.SessionId is not null);
                if (continuation && string.IsNullOrWhiteSpace(context.SessionId)) throw new InvalidOperationException("A conversation is required for context continuation.");
                var task = schedules.Upsert(id, GetString(args, "name") ?? existing?.Name ?? "Follow-up",
                    source,
                    GetString(args, "prompt") ?? existing?.Prompt ?? "", GetString(args, "cron") ?? existing?.Cron ?? "*/10 * * * *", true,
                    continuation ? context.SessionId : null, args["worktree"]?.GetValue<bool>() ?? existing?.RunInWorktree ?? false,
                    GetString(args, "notification") ?? existing?.NotificationPolicy ?? "changes");
                presented = task;
                output = JsonSerializer.Serialize(task, SeekClawJsonContext.Default.ScheduledTask); break;
            case "list":
                output = JsonSerializer.Serialize(schedules.List().Where(x => x.Workspace == source && (x.SessionId is null || x.SessionId == context.SessionId)).ToList(),
                    SeekClawJsonContext.Default.ListScheduledTask); break;
            case "pause": case "complete": case "resume":
                if (id is null) throw new ArgumentException("Schedule ID is required.");
                presented = schedules.SetEnabled(id, action == "resume");
                output = JsonSerializer.Serialize(presented, SeekClawJsonContext.Default.ScheduledTask); break;
            case "delete":
                if (id is null) throw new ArgumentException("Schedule ID is required.");
                schedules.Remove(id); presented = existing; output = "Schedule deleted."; break;
            default: throw new ArgumentException("Unknown schedule action.");
        }
        if (action != "list") context.Events.Publish(new ScheduledTaskUpdatedEvent());
        return Task.FromResult(ToolResult.Ok(output, presented is null ? null : $"{action}: {presented.Name}", mutated: false) with
        {
            Widgets = presented is null ? null : [new ScheduleWidget("schedule:" + presented.Id, presented.Name, presented, action == "delete")],
        });
    }
}
