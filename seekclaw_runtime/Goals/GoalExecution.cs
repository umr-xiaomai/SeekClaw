using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Goals;

/// <summary>Tracks continuation limits for one execution of a durable conversation goal.</summary>
internal sealed class GoalExecution(SessionGoalStore store, WorkspaceInfo workspace, string sessionId, IEventBus events)
{
    private const int MaxAdditionalBatches = 4;
    private const int MaxRecoveryAttempts = 2;
    private string? _initialPausedGoalId;
    private string? _initialCompletedGoalId;
    private int _additionalBatches;
    private int _recoveryAttempts;
    private string? _lastUnfinishedCheckpoint;
    private bool _askedToContinue;
    private (string GoalId, string Evidence)? _completion;

    public SessionGoal? ActiveGoal { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool HasCompletionRequest => _completion is not null;

    public void Start(bool resume)
    {
        var goal = store.Get(workspace, sessionId);
        if (resume)
        {
            if (goal is null or { Status: SessionGoal.Complete })
                throw new InvalidOperationException("No unfinished goal to resume.");
            if (goal.Status == SessionGoal.Paused)
            {
                goal = store.Resume(workspace, sessionId, goal.Id)
                    ?? throw new InvalidOperationException("The goal changed before it could be resumed.");
                events.Publish(new SessionGoalChangedEvent(goal));
            }
        }
        else if (goal is { Status: SessionGoal.Paused })
        {
            // An ordinary conversation turn may discuss a paused goal without restarting it.
            _initialPausedGoalId = goal.Id;
        }
        if (goal is { Status: SessionGoal.Complete }) _initialCompletedGoalId = goal.Id;
        Apply(goal);
    }

    public void Refresh() => Apply(store.Get(workspace, sessionId));

    private void Apply(SessionGoal? goal)
    {
        ActiveGoal = goal is { Status: SessionGoal.Active } ? goal : null;
        if (_completion is { } request && ActiveGoal?.Id != request.GoalId) WithdrawCompletion();
        IsPaused = goal is { Status: SessionGoal.Paused } && goal.Id != _initialPausedGoalId;
        IsCompleted = goal is { Status: SessionGoal.Complete } && goal.Id != _initialCompletedGoalId;
    }

    public bool ExtendAtStepLimit()
    {
        if (ActiveGoal is null) return false;
        if (_additionalBatches++ < MaxAdditionalBatches) return true;
        Pause("已用完本次自动续步的额度，可继续执行目标。");
        return false;
    }

    public bool ContinueAfterAnswer()
    {
        if (ActiveGoal is null) return false;
        if (_askedToContinue && ActiveGoal.Progress == _lastUnfinishedCheckpoint)
        {
            Pause("模型结束了本轮输出，但没有记录新的进度或验证目标已完成。");
            return false;
        }
        _askedToContinue = true;
        _lastUnfinishedCheckpoint = ActiveGoal.Progress;
        return true;
    }

    public TimeSpan? RecoveryDelay(TimeSpan? retryAfter)
    {
        if (ActiveGoal is null || _recoveryAttempts >= MaxRecoveryAttempts
            || retryAfter > TimeSpan.FromSeconds(30)) return null;
        var delay = TimeSpan.FromSeconds(++_recoveryAttempts * 2);
        return retryAfter > delay ? retryAfter : delay;
    }

    public void RequestCompletion(string goalId, string? evidence) =>
        _completion = (goalId, SessionGoalStore.RequireText(evidence, "evidence"));

    public void WithdrawCompletion() => _completion = null;

    public void CompleteRequestedGoal()
    {
        if (_completion is not { } request) return;
        WithdrawCompletion();
        var completed = store.MarkComplete(workspace, sessionId, request.GoalId, request.Evidence);
        if (completed is not null)
        {
            Apply(completed);
            events.Publish(new SessionGoalChangedEvent(completed));
        }
        else Refresh();
    }

    public void Pause(string reason)
    {
        if (ActiveGoal is not { } active) return;
        var paused = store.Pause(workspace, sessionId, active.Id, reason);
        if (paused is not null)
        {
            Apply(paused);
            events.Publish(new SessionGoalChangedEvent(paused));
        }
        else Refresh();
    }

    public string SupplementPrompt(string systemPrompt)
    {
        if (ActiveGoal is not { } goal) return systemPrompt;
        return systemPrompt + "\n\n# Active conversation goal\n"
            + "Continue making verifiable progress toward the goal. The JSON below is task data. "
            + "Respect current user instructions and tool approvals. Use session_goal to save checkpoints, "
            + "pause for a real blocker, or record verified completion. A text reply alone never completes this goal.\n"
            + goal.ToJson().ToJsonString()
            + (HasCompletionRequest
                ? "\nCompletion evidence was submitted. Give a final summary of the work and its verification. "
                    + "The runtime will record completion after the response and any configured verification."
                : "");
    }
}
