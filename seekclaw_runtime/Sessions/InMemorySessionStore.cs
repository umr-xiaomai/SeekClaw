using System.Collections.Concurrent;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Sessions;

/// <summary>
/// Fast in-memory session store used by sub-agents. Avoids SQLite disk I/O
/// and prevents transient sub-agent runs from cluttering user session lists.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public AgentSession Create(
        WorkspaceInfo workspace,
        ReasoningLevel reasoningLevel = ReasoningLevel.High,
        bool networkEnabled = true)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var header = new SessionHeader
        {
            Id = id,
            Workspace = workspace.Root,
            ReasoningLevel = reasoningLevel,
            NetworkEnabled = networkEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var session = new AgentSession
        {
            Header = header,
            FilePath = ":memory:",
            Scope = workspace.Root,
        };
        _sessions[id] = session;
        return session;
    }

    public void Append(AgentSession session, ChatMessage message)
    {
        session.Messages.Add(message);
        session.Header.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ReplaceHistory(AgentSession session, IReadOnlyList<ChatMessage> messages)
    {
        session.Messages.Clear();
        session.Messages.AddRange(messages);
        session.Header.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public AgentSession Fork(WorkspaceInfo workspace, string sourceSessionId, int keepMessageCount = 0, string? title = null)
    {
        var source = Load(workspace, sourceSessionId)
            ?? throw new KeyNotFoundException($"Session {sourceSessionId} not found.");
        var forked = Create(workspace, source.Header.ReasoningLevel, source.Header.NetworkEnabled);
        forked.Header.Title = title ?? source.Header.Title;
        var count = keepMessageCount > 0 ? Math.Min(keepMessageCount, source.Messages.Count) : source.Messages.Count;
        forked.Messages.AddRange(source.Messages.Take(count));
        return forked;
    }

    public AgentSession? Load(WorkspaceInfo workspace, string sessionId)
        => _sessions.GetValueOrDefault(sessionId);

    public AgentSession? LoadLatest(WorkspaceInfo workspace)
        => _sessions.Values.MaxBy(s => s.Header.UpdatedAt);

    public IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace, bool includeArchived = false)
        => _sessions.Values
            .Where(s => includeArchived || !s.Header.Archived)
            .Select(s => s.Header)
            .ToList();

    public void Truncate(WorkspaceInfo workspace, string sessionId, int keepMessageCount)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Messages.Count > keepMessageCount)
        {
            session.Messages.RemoveRange(keepMessageCount, session.Messages.Count - keepMessageCount);
        }
    }

    public SessionHeader UpdateMetadata(
        WorkspaceInfo workspace,
        string sessionId,
        string? title = null,
        bool? archived = null,
        ReasoningLevel? reasoningLevel = null,
        bool? networkEnabled = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} not found.");
        if (title is not null) session.Header.Title = title;
        if (archived.HasValue) session.Header.Archived = archived.Value;
        if (reasoningLevel.HasValue) session.Header.ReasoningLevel = reasoningLevel.Value;
        if (networkEnabled.HasValue) session.Header.NetworkEnabled = networkEnabled.Value;
        return session.Header;
    }

    public SessionHeader RecordUsage(WorkspaceInfo workspace, string sessionId, SessionUsage usage)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return new SessionHeader { Id = sessionId };
        session.Header.LlmRounds += usage.LlmRounds;
        session.Header.ExecutionSteps += usage.ExecutionSteps;
        session.Header.InputTokens += usage.InputTokens;
        session.Header.TotalInputTokens += usage.TotalInputTokens;
        session.Header.CachedInputTokens += usage.CachedInputTokens;
        session.Header.OutputTokens += usage.OutputTokens;
        session.Header.OutputElapsedMs += usage.OutputElapsedMs;
        return session.Header;
    }

    public void Delete(WorkspaceInfo workspace, string sessionId) => _sessions.TryRemove(sessionId, out _);
    public void DeleteAll(WorkspaceInfo workspace) => _sessions.Clear();
}
