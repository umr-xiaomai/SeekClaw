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
        bool networkEnabled = true,
        string? parentSessionId = null,
        string? modelRef = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var header = new SessionHeader
        {
            Id = id,
            Workspace = workspace.Root,
            ParentSessionId = string.IsNullOrWhiteSpace(parentSessionId) ? null : parentSessionId,
            ModelRef = modelRef,
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
        lock (session)
        {
            session.Messages.Add(message);
            session.Header.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public bool TrySaveContextCheckpoint(AgentSession session, int coveredMessageCount, ChatMessage coveredThrough, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        lock (session)
        {
            if (coveredMessageCount <= (session.ContextCheckpoint?.CoveredMessageCount ?? 0)
                || coveredMessageCount > session.Messages.Count
                || !ReferenceEquals(session.Messages[coveredMessageCount - 1], coveredThrough)) return false;
            session.ContextCheckpoint = new(coveredMessageCount, summary);
            return true;
        }
    }

    public void ReplaceHistory(AgentSession session, IReadOnlyList<ChatMessage> messages)
    {
        lock (session)
        {
            var snapshot = messages.ToArray();
            session.Messages.Clear();
            session.Messages.AddRange(snapshot);
            session.ContextCheckpoint = null;
            session.Header.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public AgentSession Fork(WorkspaceInfo workspace, string sourceSessionId, int keepMessageCount = 0, string? title = null)
    {
        var source = Load(workspace, sourceSessionId)
            ?? throw new KeyNotFoundException($"Session {sourceSessionId} not found.");
        var forked = Create(workspace, source.Header.ReasoningLevel, source.Header.NetworkEnabled,
            modelRef: source.Header.ModelRef);
        forked.Header.Title = title ?? source.Header.Title;
        forked.Header.TitleSource = "manual";
        lock (source)
        {
            var count = keepMessageCount > 0 ? Math.Min(keepMessageCount, source.Messages.Count) : source.Messages.Count;
            forked.Messages.AddRange(source.Messages.Take(count));
            if (source.ContextCheckpoint is { } checkpoint && checkpoint.CoveredMessageCount <= count)
                forked.ContextCheckpoint = checkpoint;
        }
        return forked;
    }

    public AgentSession? Load(WorkspaceInfo workspace, string sessionId)
        => _sessions.GetValueOrDefault(sessionId);

    public AgentSession? LoadLatest(WorkspaceInfo workspace)
        => _sessions.Values.Where(s => s.Header.ParentSessionId is null).MaxBy(s => s.Header.UpdatedAt);

    public IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace, bool includeArchived = false)
        => _sessions.Values
            .Where(s => s.Header.ParentSessionId is null && (includeArchived || !s.Header.Archived))
            .Select(s => s.Header)
            .ToList();

    public void Truncate(WorkspaceInfo workspace, string sessionId, int keepMessageCount)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        lock (session)
        {
            var count = Math.Max(0, keepMessageCount);
            if (count >= session.Messages.Count) return;
            session.Messages.RemoveRange(count, session.Messages.Count - count);
            if (session.ContextCheckpoint is { } checkpoint && checkpoint.CoveredMessageCount > count)
                session.ContextCheckpoint = null;
            session.Header.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public SessionHeader UpdateMetadata(
        WorkspaceInfo workspace,
        string sessionId,
        string? title = null,
        bool? archived = null,
        ReasoningLevel? reasoningLevel = null,
        bool? networkEnabled = null,
        string? modelRef = null,
        bool clearModelRef = false,
        bool setModelRefIfMissing = false)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} not found.");
        if (title is not null)
        {
            session.Header.Title = title;
            session.Header.TitleSource = "manual";
        }
        if (archived.HasValue) session.Header.Archived = archived.Value;
        if (reasoningLevel.HasValue) session.Header.ReasoningLevel = reasoningLevel.Value;
        if (networkEnabled.HasValue) session.Header.NetworkEnabled = networkEnabled.Value;
        lock (session)
        {
            if ((modelRef is not null || clearModelRef) && (!setModelRefIfMissing || session.Header.ModelRef is null))
                session.Header.ModelRef = modelRef;
        }
        return session.Header;
    }

    public SessionHeader? TrySetGeneratedTitle(WorkspaceInfo workspace, string sessionId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!_sessions.TryGetValue(sessionId, out var session)) return null;
        lock (session)
        {
            if (session.Header.TitleSource is not ("pending" or "input")) return null;
            session.Header.Title = title.Trim();
            session.Header.TitleSource = "generated";
            return session.Header;
        }
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
