using Microsoft.Data.Sqlite;
using SeekClaw.Runtime.Data;

namespace SeekClaw.Runtime.Projects;

public sealed class StoredProject
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IProjectStore
{
    IReadOnlyList<StoredProject> List();
    StoredProject? Get(string id);
    StoredProject Upsert(string? id, string path, string? name);
    void Remove(string id);
}

public sealed class ProjectStore(SeekClawDatabase database) : IProjectStore
{
    public IReadOnlyList<StoredProject> List()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, name, created_at, updated_at
            FROM projects
            ORDER BY updated_at DESC, name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var projects = new List<StoredProject>();
        while (reader.Read()) projects.Add(ReadProject(reader));
        return projects;
    }

    public StoredProject? Get(string id)
    {
        ValidateId(id);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, name, created_at, updated_at
            FROM projects WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    public StoredProject Upsert(string? id, string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Project path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var projectName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : name.Trim();
        if (string.IsNullOrWhiteSpace(projectName)) projectName = fullPath;
        var now = DateTimeOffset.UtcNow;

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id, created_at FROM projects WHERE path_key = $pathKey;";
            existing.Parameters.AddWithValue("$pathKey", SeekClawDatabase.PathKey(fullPath));
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var existingId = reader.GetString(0);
                var createdAt = reader.GetString(1);
                reader.Close();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE projects SET path = $path, name = $name, updated_at = $updatedAt
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$path", fullPath);
                update.Parameters.AddWithValue("$name", projectName);
                update.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
                update.Parameters.AddWithValue("$id", existingId);
                update.ExecuteNonQuery();
                transaction.Commit();
                return new StoredProject
                {
                    Id = existingId,
                    Path = fullPath,
                    Name = projectName,
                    CreatedAt = DateTimeOffset.Parse(createdAt),
                    UpdatedAt = now,
                };
            }
        }

        var projectId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id.Trim();
        ValidateId(projectId);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO projects(id, path, path_key, name, created_at, updated_at)
                VALUES($id, $path, $pathKey, $name, $createdAt, $updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    path = excluded.path,
                    path_key = excluded.path_key,
                    name = excluded.name,
                    updated_at = excluded.updated_at;
                """;
            insert.Parameters.AddWithValue("$id", projectId);
            insert.Parameters.AddWithValue("$path", fullPath);
            insert.Parameters.AddWithValue("$pathKey", SeekClawDatabase.PathKey(fullPath));
            insert.Parameters.AddWithValue("$name", projectName);
            insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            insert.ExecuteNonQuery();
        }
        var insertedCreatedAt = now;
        using (var readCreated = connection.CreateCommand())
        {
            readCreated.Transaction = transaction;
            readCreated.CommandText = "SELECT created_at FROM projects WHERE id = $id;";
            readCreated.Parameters.AddWithValue("$id", projectId);
            if (readCreated.ExecuteScalar() is string value)
                insertedCreatedAt = DateTimeOffset.Parse(value);
        }
        transaction.Commit();
        return new StoredProject
        {
            Id = projectId,
            Path = fullPath,
            Name = projectName,
            CreatedAt = insertedCreatedAt,
            UpdatedAt = now,
        };
    }

    public void Remove(string id)
    {
        ValidateId(id);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException($"Project not found: {id}");
    }

    private static StoredProject ReadProject(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Path = reader.GetString(1),
        Name = reader.GetString(2),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(4)),
    };

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("Invalid project id.", nameof(id));
    }
}
