namespace SeekClaw.Runtime.Scheduling;

/// <summary>Outcome of the latest run of a scheduled task.</summary>
public static class ScheduleRunStatus
{
    public const string Success = "success";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
}

/// <summary>
/// A recurring task persisted in the central SeekClaw database. The daemon's scheduler
/// fires it on the 5-field cron expression (minute hour day-of-month month day-of-week),
/// runs one agent turn and records the outcome.
/// </summary>
public sealed class ScheduledTask
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Absolute workspace path; null/empty runs in the global (non-workspace) scope.</summary>
    public string? Workspace { get; set; }
    public string Prompt { get; set; } = "";
    public string Cron { get; set; } = "0 9 * * *";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public string? LastOutput { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名任务" : Name;
}
