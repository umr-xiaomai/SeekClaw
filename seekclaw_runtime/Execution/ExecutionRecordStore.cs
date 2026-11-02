using System.Text.Json.Nodes;
using SeekClaw.Runtime.Data;

namespace SeekClaw.Runtime.Execution;

/// <summary>Versionable task metadata. Source-generated session messages remain in the session store.</summary>
public sealed class ExecutionRecordStore(SeekClawDatabase database)
{
    internal string LockDirectory => Path.Combine(Path.GetDirectoryName(database.FilePath)!, "task-locks");
    public JsonObject? Get(string kind, string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM execution_records WHERE kind=$kind AND id=$id;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonNode.Parse(json) as JsonObject : null;
    }

    public IReadOnlyList<JsonObject> List(string kind, string? owner = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM execution_records WHERE kind=$kind"
            + (owner is null ? "" : " AND owner=$owner") + " ORDER BY updated_at DESC;";
        command.Parameters.AddWithValue("$kind", kind);
        if (owner is not null) command.Parameters.AddWithValue("$owner", owner);
        using var reader = command.ExecuteReader();
        var results = new List<JsonObject>();
        while (reader.Read()) if (JsonNode.Parse(reader.GetString(0)) is JsonObject record) results.Add(record);
        return results;
    }

    public void Put(string kind, string id, string owner, JsonObject payload)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO execution_records(kind,id,owner,payload_json,updated_at) VALUES($kind,$id,$owner,$json,$updated)
            ON CONFLICT(kind,id) DO UPDATE SET owner=excluded.owner,payload_json=excluded.payload_json,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$json", payload.ToJsonString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
