using System.Collections.Concurrent;
using System.Text.Json;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Sessions;

/// <summary>A live conversation bound to a JSONL file under the workspace .session/ directory.</summary>
public sealed class AgentSession
{
    public required SessionHeader Header { get; init; }
    public List<ChatMessage> Messages { get; } = [];
    public required string FilePath { get; init; }
}

public interface ISessionStore
{
    AgentSession Create(WorkspaceInfo workspace);
    AgentSession? Load(WorkspaceInfo workspace, string sessionId);
    AgentSession? LoadLatest(WorkspaceInfo workspace);
    IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace, bool includeArchived = false);
    void Append(AgentSession session, ChatMessage message);
    SessionHeader UpdateMetadata(WorkspaceInfo workspace, string sessionId, string? title = null, bool? archived = null);
    void Delete(WorkspaceInfo workspace, string sessionId);
}

public sealed class SessionStore : ISessionStore
{
    // Isolated daemon turns create separate SessionStore instances. Keep file locks
    // process-wide so concurrent turns cannot interleave writes to one session JSONL.
    private static readonly ConcurrentDictionary<string, Lock> FileGates = new(StringComparer.OrdinalIgnoreCase);

    public AgentSession Create(WorkspaceInfo workspace)
    {
        Directory.CreateDirectory(workspace.SessionsDir);
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var header = new SessionHeader { Id = id, Workspace = workspace.IsGlobal ? null : workspace.Root };
        var file = Path.Combine(workspace.SessionsDir, id + ".jsonl");
        File.WriteAllText(file, JsonSerializer.Serialize(header, SeekClawJsonContext.Compact.SessionHeader) + Environment.NewLine);
        return new AgentSession { Header = header, FilePath = file };
    }

    public AgentSession? Load(WorkspaceInfo workspace, string sessionId)
    {
        var file = SessionFile(workspace, sessionId);
        return File.Exists(file) ? LoadFile(file) : null;
    }

    public AgentSession? LoadLatest(WorkspaceInfo workspace)
    {
        if (!Directory.Exists(workspace.SessionsDir)) return null;
        var latest = List(workspace).FirstOrDefault();
        return latest is null ? null : Load(workspace, latest.Id);
    }

    public IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace, bool includeArchived = false)
    {
        if (!Directory.Exists(workspace.SessionsDir)) return [];
        var headers = new List<SessionHeader>();
        foreach (var file in Directory.EnumerateFiles(workspace.SessionsDir, "*.jsonl"))
        {
            using var lines = File.ReadLines(file).GetEnumerator();
            if (!lines.MoveNext()) continue;
            try
            {
                var header = JsonSerializer.Deserialize(lines.Current, SeekClawJsonContext.Compact.SessionHeader);
                if (header is null) continue;
                if (header.Archived && !includeArchived) continue;
                header.UpdatedAt = File.GetLastWriteTimeUtc(file);
                if (string.IsNullOrWhiteSpace(header.Title) && lines.MoveNext())
                {
                    var firstMessage = JsonSerializer.Deserialize(lines.Current, SeekClawJsonContext.Compact.SessionMessage);
                    if (firstMessage?.Role == "user" && !string.IsNullOrWhiteSpace(firstMessage.Text))
                        header.Title = firstMessage.Text.Length > 42
                            ? firstMessage.Text[..42] + "…"
                            : firstMessage.Text;
                }
                headers.Add(header);
            }
            catch (JsonException) { }
        }
        return headers.OrderByDescending(h => h.UpdatedAt).ToList();
    }

    public void Append(AgentSession session, ChatMessage message)
    {
        var record = ToRecord(message);
        lock (GateFor(session.FilePath))
        {
            session.Messages.Add(message);
            File.AppendAllText(session.FilePath,
                JsonSerializer.Serialize(record, SeekClawJsonContext.Compact.SessionMessage) + Environment.NewLine);
        }
    }

    public SessionHeader UpdateMetadata(
        WorkspaceInfo workspace,
        string sessionId,
        string? title = null,
        bool? archived = null)
    {
        var file = SessionFile(workspace, sessionId);
        lock (GateFor(file))
        {
            if (!File.Exists(file))
                throw new FileNotFoundException($"Session not found: {sessionId}", file);

            var lines = File.ReadAllLines(file);
            if (lines.Length == 0)
                throw new InvalidDataException($"Session header is missing: {sessionId}");

            var header = JsonSerializer.Deserialize(lines[0], SeekClawJsonContext.Compact.SessionHeader)
                         ?? throw new InvalidDataException($"Session header is invalid: {sessionId}");
            if (title is not null) header.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            if (archived is not null) header.Archived = archived.Value;
            header.UpdatedAt = DateTimeOffset.UtcNow;
            lines[0] = JsonSerializer.Serialize(header, SeekClawJsonContext.Compact.SessionHeader);
            File.WriteAllLines(file, lines);
            return header;
        }
    }

    public void Delete(WorkspaceInfo workspace, string sessionId)
    {
        var file = SessionFile(workspace, sessionId);
        lock (GateFor(file))
        {
            if (!File.Exists(file))
                throw new FileNotFoundException($"Session not found: {sessionId}", file);
            File.Delete(file);
        }
    }

    private AgentSession? LoadFile(string file)
    {
        SessionHeader? header = null;
        var messages = new List<ChatMessage>();

        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (header is null)
                {
                    header = JsonSerializer.Deserialize(line, SeekClawJsonContext.Compact.SessionHeader);
                    continue;
                }
                var record = JsonSerializer.Deserialize(line, SeekClawJsonContext.Compact.SessionMessage);
                if (record is not null) messages.Add(ToMessage(record));
            }
            catch (JsonException) { }
        }

        if (header is null) return null;
        var session = new AgentSession { Header = header, FilePath = file };
        session.Messages.AddRange(messages);
        return session;
    }

    private static SessionMessage ToRecord(ChatMessage message) => new()
    {
        Role = message.Role.ToString().ToLowerInvariant(),
        Text = message.Text.Length > 0 ? message.Text : null,
        Thinking = message.Thinking,
        ToolCalls = message.ToolCalls?.Select(c => new SessionToolCall
        {
            Id = c.Id, Name = c.Name, ArgumentsJson = c.ArgumentsJson,
        }).ToList(),
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        ToolSuccess = message.Role == ChatRole.Tool ? message.ToolSuccess : null,
        ToolDiff = message.ToolDiff,
        ToolFilePath = message.ToolFilePath,
    };

    private static ChatMessage ToMessage(SessionMessage record)
    {
        var role = record.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        };
        return new ChatMessage
        {
            Role = role,
            Text = record.Text ?? "",
            Thinking = record.Thinking,
            ToolCalls = record.ToolCalls?.Select(c => new ToolCallRequest(c.Id, c.Name, c.ArgumentsJson)).ToList(),
            ToolCallId = record.ToolCallId,
            ToolName = record.ToolName,
            ToolSuccess = record.ToolSuccess ?? true,
            ToolDiff = record.ToolDiff,
            ToolFilePath = record.ToolFilePath,
        };
    }

    private static string SessionFile(WorkspaceInfo workspace, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !string.Equals(Path.GetFileName(sessionId), sessionId, StringComparison.Ordinal))
            throw new ArgumentException("Invalid session id.", nameof(sessionId));
        return Path.Combine(workspace.SessionsDir, sessionId + ".jsonl");
    }

    private static Lock GateFor(string file) =>
        FileGates.GetOrAdd(Path.GetFullPath(file), static _ => new Lock());
}
