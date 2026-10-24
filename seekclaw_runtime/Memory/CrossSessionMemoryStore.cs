using System.Security.Cryptography;
using System.Text;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Memory;

public interface ICrossSessionMemoryStore
{
    IReadOnlyList<CrossSessionMemory> ListRecent(int limit = 100);
    void Save(string content, WorkspaceInfo sourceWorkspace, string? sourceSessionId = null);
    bool Delete(string id);
    int Clear();
}

/// <summary>
/// SQLite-backed user memory shared by CLI and Desktop runtimes. Memories deliberately
/// have no session foreign key: deleting a conversation must not silently delete facts
/// the user chose to retain, while factory reset still clears the whole table.
/// </summary>
public sealed class CrossSessionMemoryStore(SeekClawDatabase database) : ICrossSessionMemoryStore
{
    public IReadOnlyList<CrossSessionMemory> ListRecent(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, content, source_workspace, source_session_id, created_at
            FROM cross_session_memories
            ORDER BY created_at DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var memories = new List<CrossSessionMemory>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            memories.Add(new CrossSessionMemory
            {
                Id = reader.GetString(0),
                Content = reader.GetString(1),
                SourceWorkspace = reader.IsDBNull(2) ? null : reader.GetString(2),
                SourceSessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            });
        }
        return memories;
    }

    public void Save(string content, WorkspaceInfo sourceWorkspace, string? sourceSessionId = null)
    {
        content = content.Trim();
        if (content.Length == 0) throw new ArgumentException("Memory content cannot be empty.", nameof(content));
        if (content.Length > 500) throw new ArgumentException("Memory content cannot exceed 500 characters.", nameof(content));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cross_session_memories(
                id, content, content_key, source_workspace, source_session_id, created_at)
            VALUES($id, $content, $contentKey, $workspace, $sessionId, $createdAt)
            ON CONFLICT(content_key) DO UPDATE SET
                source_workspace = excluded.source_workspace,
                source_session_id = excluded.source_session_id,
                created_at = excluded.created_at;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$contentKey", ContentKey(content));
        command.Parameters.AddWithValue("$workspace",
            sourceWorkspace.IsGlobal ? DBNull.Value : Path.GetFullPath(sourceWorkspace.Root));
        command.Parameters.AddWithValue("$sessionId",
            string.IsNullOrWhiteSpace(sourceSessionId) ? DBNull.Value : sourceSessionId);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Memory id is required.", nameof(id));
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cross_session_memories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public int Clear()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cross_session_memories;";
        return command.ExecuteNonQuery();
    }

    private static string ContentKey(string content)
    {
        var normalized = string.Join(' ', content.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
