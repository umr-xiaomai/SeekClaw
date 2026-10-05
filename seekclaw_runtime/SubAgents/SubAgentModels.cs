namespace SeekClaw.Runtime.SubAgents;

/// <summary>Sub-agent scope identifier attached to DI during turn execution.</summary>
public sealed record SubAgentScope(int Depth = 0, string? SubAgentId = null, string? Role = null);

/// <summary>Role configuration defining the sub-agent's permissions, prompts, and tool access.</summary>
public sealed record SubAgentDefinition(
    string Role,
    string Description,
    string PromptKey,
    IReadOnlySet<string>? AllowedTools,
    bool AllowMutating,
    int MaxSteps = 10,
    string? DefaultModelOverride = null);

/// <summary>Instruction payload passed to the sub-agent execution engine.</summary>
public sealed record SubAgentTaskRequest(
    string Role,
    string Task,
    string? ModelOverride = null);

/// <summary>Execution outcome returned to the orchestrator agent.</summary>
public sealed record SubAgentTaskResult(
    bool Success,
    string Output,
    string? Error = null,
    TimeSpan Elapsed = default);
