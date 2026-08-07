using Microsoft.Data.Sqlite;
using SeekClaw.Runtime.Data;

namespace SeekClaw.Runtime.Scheduling;

public interface IScheduleStore
{
    IReadOnlyList<ScheduledTask> List();
    ScheduledTask? Get(string id);
    ScheduledTask Upsert(string? id, string name, string? workspace, string prompt, string cron, bool enabled);
    ScheduledTask SetEnabled(string id, bool enabled);
    ScheduledTask RecordRun(string id, string status, string? error = null, string? output = null);
    void Remove(string id);
}

/// <summary>Persists recurring tasks in the central SQLite database.</summary>
public sealed class ScheduleStore(SeekClawDatabase database) : IScheduleStore
{
    private const string SelectColumns =
        "id, name, workspace, prompt, cron, enabled, " +
        "last_run_at, next_run_at, last_status, last_error, last_output, " +
        "created_at, updated_at";

    public IReadOnlyList<ScheduledTask> List()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM scheduled_tasks ORDER BY created_at DESC;";
        using var reader = command.ExecuteReader();
        var tasks = new List<ScheduledTask>();
        while (reader.Read()) tasks.Add(ReadTask(reader));
        return tasks;
    }

    public ScheduledTask? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM scheduled_tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    public ScheduledTask Upsert(string? id, string name, string? workspace, string prompt, string cron, bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var taskId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id.Trim();
        ScheduleCron.Parse(cron); // validates; throws CronFormatException
        var nextRunAt = enabled ? ScheduleCron.NextOccurrence(cron, now) : null;

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM scheduled_tasks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", taskId);
            var exists = Convert.ToInt64(command.ExecuteScalar()) > 0;
            command.Parameters.Clear();

            if (exists)
            {
                command.CommandText = """
                    UPDATE scheduled_tasks
                    SET name = $name, workspace = $workspace, prompt = $prompt, cron = $cron,
                        enabled = $enabled, next_run_at = $nextRunAt, updated_at = $updatedAt
                    WHERE id = $id;
                    """;
            }
            else
            {
                command.CommandText = """
                    INSERT INTO scheduled_tasks
                        (id, name, workspace, prompt, cron, enabled, next_run_at, created_at, updated_at)
                    VALUES
                        ($id, $name, $workspace, $prompt, $cron, $enabled, $nextRunAt, $createdAt, $updatedAt);
                    """;
                command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            }
            command.Parameters.AddWithValue("$id", taskId);
            command.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? "未命名任务" : name.Trim());
            command.Parameters.AddWithValue("$workspace", Nullable(string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace)));
            command.Parameters.AddWithValue("$prompt", prompt.Trim());
            command.Parameters.AddWithValue("$cron", cron.Trim());
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$nextRunAt", Nullable(nextRunAt?.ToString("O")));
            command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Get(taskId)!;
    }

    public ScheduledTask SetEnabled(string id, bool enabled)
    {
        var task = Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        return Upsert(task.Id, task.Name, task.Workspace, task.Prompt, task.Cron, enabled);
    }

    public ScheduledTask RecordRun(string id, string status, string? error = null, string? output = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Task id is required.", nameof(id));
        var task = Get(id) ?? throw new InvalidOperationException($"Scheduled task not found: {id}");
        var now = DateTimeOffset.UtcNow;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scheduled_tasks
            SET last_run_at = $lastRunAt, last_status = $status, last_error = $error,
                last_output = $output, next_run_at = $nextRunAt, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.Trim());
        command.Parameters.AddWithValue("$lastRunAt", now.ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", Nullable(string.IsNullOrWhiteSpace(error) ? null : error));
        command.Parameters.AddWithValue("$output", Nullable(string.IsNullOrWhiteSpace(output) ? null : Truncate(output)));
        command.Parameters.AddWithValue("$nextRunAt", Nullable(task.Enabled ? ScheduleCron.NextOccurrence(task.Cron, now)?.ToString("O") : null));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.ExecuteNonQuery();
        return Get(id)!;
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scheduled_tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.Trim());
        command.ExecuteNonQuery();
    }

    private static ScheduledTask ReadTask(SqliteDataReader reader)
    {
        static DateTimeOffset? ParseDate(object? value) =>
            value is string text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
        static string? Text(object? value) => value is string text ? text : null;

        return new ScheduledTask
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Workspace = Text(reader.GetValue(2)),
            Prompt = reader.GetString(3),
            Cron = reader.GetString(4),
            Enabled = reader.GetInt64(5) != 0,
            LastRunAt = ParseDate(reader.GetValue(6)),
            NextRunAt = ParseDate(reader.GetValue(7)),
            LastStatus = Text(reader.GetValue(8)),
            LastError = Text(reader.GetValue(9)),
            LastOutput = Text(reader.GetValue(10)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12)),
        };
    }

    private static string Truncate(string text) => text.Length > 4_000 ? text[..4_000] : text;

    private static object Nullable(string? value) => (object?)value ?? DBNull.Value;
}
