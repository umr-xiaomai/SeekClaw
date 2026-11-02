using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.SubAgents;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Execution;

/// <summary>Process-owned task infrastructure shared with short-lived agent runtimes.</summary>
public sealed class RuntimeTaskServices(SeekClawDatabase database, ExecutionRecordStore records,
    CommandSessionManager commands, WorktreeManager worktrees, SessionTurnCoordinator turns,
    SubAgentTaskManager subAgents, McpAuthentication authentication)
{
    public SeekClawDatabase Database { get; } = database;
    public ExecutionRecordStore Records { get; } = records;
    public CommandSessionManager Commands { get; } = commands;
    public WorktreeManager Worktrees { get; } = worktrees;
    public SessionTurnCoordinator Turns { get; } = turns;
    public SubAgentTaskManager SubAgents { get; } = subAgents;
    public McpAuthentication Authentication { get; } = authentication;

    internal void ShareWith(IServiceCollection services)
    {
        // Instance registrations are externally owned; disposing a turn cannot stop sibling tasks.
        services.AddSingleton(this); services.AddSingleton(Database); services.AddSingleton(Records);
        services.AddSingleton(Commands); services.AddSingleton(Worktrees); services.AddSingleton(Turns);
        services.AddSingleton(SubAgents); services.AddSingleton(Authentication);
    }
}
