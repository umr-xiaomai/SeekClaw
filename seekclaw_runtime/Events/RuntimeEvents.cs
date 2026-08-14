namespace SeekClaw.Runtime.Events;

/// <summary>
/// Base type for every event flowing through the runtime event bus.
/// The renderer (CLI/GUI/Web) consumes these; the runtime never touches the console.
/// </summary>
public abstract record RuntimeEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

// ---------------------------------------------------------------- turn lifecycle

public sealed record TurnStartedEvent(string SessionId, string UserInput) : RuntimeEvent;

public sealed record UserSteerEvent(string Instruction) : RuntimeEvent;

public sealed record TurnCompletedEvent(string SessionId, bool Cancelled, string? Error) : RuntimeEvent;

/// <summary>High-level agent status: Thinking, Reading files, Searching, Editing, Building, Verifying…</summary>
public sealed record StatusEvent(string Status, string? Detail = null) : RuntimeEvent;

// ---------------------------------------------------------------- streaming

public sealed record AssistantTextDeltaEvent(string Delta) : RuntimeEvent;

public sealed record AssistantMessageCompletedEvent(string Text) : RuntimeEvent;

public sealed record ThinkingDeltaEvent(string Delta) : RuntimeEvent;

public sealed record ThinkingCompletedEvent : RuntimeEvent;

/// <summary>Emitted immediately before an uploaded image enters the model request.</summary>
public sealed record ImageViewedEvent(string ImageId, string Name, string MediaType) : RuntimeEvent;

// ---------------------------------------------------------------- tools

public sealed record ToolCallStartedEvent(string CallId, string ToolName, string ArgumentSummary) : RuntimeEvent;

public sealed record ToolCallProgressEvent(string CallId, string Message) : RuntimeEvent;

public sealed record ToolCallCompletedEvent(
    string CallId,
    string ToolName,
    bool Success,
    string ResultSummary,
    TimeSpan Duration) : RuntimeEvent;

/// <summary>A unified diff produced by a file-mutating tool, for rich rendering.</summary>
public sealed record FileDiffEvent(string CallId, string FilePath, string UnifiedDiff) : RuntimeEvent;

// ---------------------------------------------------------------- provider layer

public sealed record ModelInvocationStartedEvent(string ProviderId, string ModelId, int Step) : RuntimeEvent;

public sealed record ProviderRetryEvent(string ModelRef, int Attempt, string Reason, TimeSpan Delay) : RuntimeEvent;

public sealed record ProviderSwitchedEvent(string FromRef, string ToRef, string Reason) : RuntimeEvent;

public sealed record UsageRecordedEvent(
    string ProviderId,
    string ModelId,
    long InputTokens,
    long OutputTokens,
    decimal Cost,
    TimeSpan Elapsed,
    long TotalInputTokens,
    long CachedInputTokens) : RuntimeEvent;

// ---------------------------------------------------------------- workflow

/// <summary>A node in the agent's live execution flowchart (think / tool / verify …).</summary>
public sealed record WorkflowEvent(int Step, string Kind, string Label, string? Detail = null) : RuntimeEvent;

// ---------------------------------------------------------------- verification

public sealed record VerificationStartedEvent(string Command, int Attempt) : RuntimeEvent;

public sealed record VerificationCompletedEvent(bool Success, string Summary, int Attempt) : RuntimeEvent;

// ---------------------------------------------------------------- scheduling

/// <summary>Emitted when a scheduled task enters the one-minute pre-run window.</summary>
public sealed record ScheduledTaskUpcomingEvent(
    string TaskId,
    string Name,
    DateTimeOffset RunAt) : RuntimeEvent;

/// <summary>Emitted after a scheduled task run records its outcome.</summary>
public sealed record ScheduledTaskCompletedEvent(
    string TaskId,
    string Name,
    string? SessionId,
    string Status,
    string? Error = null,
    string? Output = null) : RuntimeEvent;

// ---------------------------------------------------------------- diagnostics

public sealed record WarningEvent(string Message) : RuntimeEvent;

public sealed record ErrorEvent(string Message, string? Detail = null) : RuntimeEvent;
