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
    IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace);
    void Append(AgentSession session, ChatMessage message);
}

public sealed class SessionStore : ISessionStore
{
    private readonly object _gate = new();

    public AgentSession Create(WorkspaceInfo workspace)
    {
        Directory.CreateDirectory(workspace.SessionsDir);
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var header = new SessionHeader { Id = id, Workspace = workspace.Root };
        var file = Path.Combine(workspace.SessionsDir, id + ".jsonl");
        File.WriteAllText(file, JsonSerializer.Serialize(header, SeekClawJsonContext.Compact.SessionHeader) + Environment.NewLine);
        return new AgentSession { Header = header, FilePath = file };
    }

    public AgentSession? Load(WorkspaceInfo workspace, string sessionId)
    {
        var file = Path.Combine(workspace.SessionsDir, sessionId + ".jsonl");
        return File.Exists(file) ? LoadFile(file) : null;
    }

    public AgentSession? LoadLatest(WorkspaceInfo workspace)
    {
        if (!Directory.Exists(workspace.SessionsDir)) return null;
        var latest = Directory.EnumerateFiles(workspace.SessionsDir, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return latest is null ? null : LoadFile(latest.FullName);
    }

    public IReadOnlyList<SessionHeader> List(WorkspaceInfo workspace)
    {
        if (!Directory.Exists(workspace.SessionsDir)) return [];
        var headers = new List<SessionHeader>();
        foreach (var file in Directory.EnumerateFiles(workspace.SessionsDir, "*.jsonl"))
        {
            var firstLine = File.ReadLines(file).FirstOrDefault();
            if (firstLine is null) continue;
            try
            {
                var header = JsonSerializer.Deserialize(firstLine, SeekClawJsonContext.Compact.SessionHeader);
                if (header is not null) headers.Add(header);
            }
            catch (JsonException) { }
        }
        return headers.OrderByDescending(h => h.UpdatedAt).ToList();
    }

    public void Append(AgentSession session, ChatMessage message)
    {
        session.Messages.Add(message);
        var record = ToRecord(message);
        lock (_gate)
            File.AppendAllText(session.FilePath,
                JsonSerializer.Serialize(record, SeekClawJsonContext.Compact.SessionMessage) + Environment.NewLine);
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
        };
    }
}
