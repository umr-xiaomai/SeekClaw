using SeekClaw.Runtime.Agents;

namespace SeekClaw.Runtime.Tools;

/// <summary>Describes one tool invocation that needs an external approval decision.</summary>
public sealed record ToolApprovalRequest(
    string SessionId,
    string? TurnRequestId,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    string ArgumentSummary,
    AgentMode Mode,
    bool Mutating,
    bool RequiresNetwork,
    bool IsMcp);

public sealed record ToolApprovalDecision(bool Approved, string? Reason = null)
{
    public static ToolApprovalDecision Allow { get; } = new(true);
    public static ToolApprovalDecision Deny(string reason) => new(false, reason);
}

public interface IToolApprovalGate
{
    ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// Process-local approval dispatcher. Front ends install a handler for the duration of a
/// session/turn; when no handler is available the gate fails closed instead of executing.
/// </summary>
public sealed class ToolApprovalService : IToolApprovalGate
{
    private readonly Lock _gate = new();
    private Func<ToolApprovalRequest, CancellationToken, ValueTask<ToolApprovalDecision>>? _handler;

    public void SetHandler(Func<ToolApprovalRequest, CancellationToken, ValueTask<ToolApprovalDecision>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _handler = handler;
    }

    public void ClearHandler()
    {
        lock (_gate) _handler = null;
    }

    public ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        Func<ToolApprovalRequest, CancellationToken, ValueTask<ToolApprovalDecision>>? handler;
        lock (_gate) handler = _handler;
        if (handler is null)
            return ValueTask.FromResult(ToolApprovalDecision.Deny(
                "No interactive approval channel is available; the tool call was denied."));
        return handler(request, ct);
    }
}
