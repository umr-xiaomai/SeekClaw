using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Goals;

public sealed record SessionGoal(string Id, string Objective, string Criteria, string Status,
    string? Progress, string? PauseReason, string UpdatedAt)
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Complete = "complete";

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["objective"] = Objective,
        ["criteria"] = Criteria,
        ["status"] = Status,
        ["progress"] = Progress,
        ["pauseReason"] = PauseReason,
        ["updatedAt"] = UpdatedAt,
    };
}

/// <summary>One durable goal per conversation. Updates compare the goal identity and state.</summary>
public sealed class SessionGoalStore(SeekClawDatabase database)
{
    private const string Columns = "id, objective, criteria, status, progress, pause_reason, updated_at";
    private const string Key = "scope=$scope AND session_id=$session";

    public SessionGoal? Get(WorkspaceInfo workspace, string sessionId) =>
        Read(workspace, sessionId, $"SELECT {Columns} FROM session_goals WHERE {Key};");

    public SessionGoal? Create(WorkspaceInfo workspace, string sessionId, string? objective, string? criteria) =>
        Read(workspace, sessionId, $"""
            INSERT INTO session_goals(scope, session_id, id, objective, criteria, status, updated_at)
            VALUES($scope, $session, $id, $objective, $criteria, 'active', $updated)
            ON CONFLICT(scope, session_id) DO UPDATE SET
                id=excluded.id, objective=excluded.objective, criteria=excluded.criteria,
                status='active', progress=NULL, pause_reason=NULL, updated_at=excluded.updated_at
            WHERE session_goals.status='complete'
            RETURNING {Columns};
            """, ("$id", Guid.NewGuid().ToString("N")), ("$objective", RequireText(objective, "objective")),
            ("$criteria", RequireText(criteria, "criteria")), ("$updated", Now()));

    public SessionGoal? RecordProgress(WorkspaceInfo workspace, string sessionId, string goalId, string? progress) =>
        ChangeFromActive(workspace, sessionId, goalId, SessionGoal.Active, RequireText(progress, "progress"));

    public SessionGoal? MarkComplete(WorkspaceInfo workspace, string sessionId, string goalId, string? evidence) =>
        ChangeFromActive(workspace, sessionId, goalId, SessionGoal.Complete, RequireText(evidence, "evidence"));

    public SessionGoal? Pause(WorkspaceInfo workspace, string sessionId, string goalId, string? reason) =>
        ChangeFromActive(workspace, sessionId, goalId, SessionGoal.Paused, reason: RequireText(reason, "reason"));

    public SessionGoal? Resume(WorkspaceInfo workspace, string sessionId, string goalId) =>
        Read(workspace, sessionId, $"""
            UPDATE session_goals SET status='active', pause_reason=NULL, updated_at=$updated
            WHERE {Key} AND id=$id AND status='paused' RETURNING {Columns};
            """, ("$id", goalId), ("$updated", Now()));

    private SessionGoal? ChangeFromActive(WorkspaceInfo workspace, string sessionId, string goalId,
        string status, string? progress = null, string? reason = null) =>
        Read(workspace, sessionId, $"""
            UPDATE session_goals SET status=$status, progress=COALESCE($progress, progress),
                pause_reason=$reason, updated_at=$updated
            WHERE {Key} AND id=$id AND status='active' RETURNING {Columns};
            """, ("$id", goalId), ("$status", status), ("$progress", progress), ("$reason", reason), ("$updated", Now()));

    private SessionGoal? Read(WorkspaceInfo workspace, string sessionId, string sql,
        params (string Name, string? Value)[] parameters)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$scope", SeekClawDatabase.ScopeKey(workspace));
        command.Parameters.AddWithValue("$session", sessionId);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SessionGoal(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                OptionalString(reader, 4), OptionalString(reader, 5), reader.GetString(6))
            : null;
    }

    private static string? OptionalString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    internal static string RequireText(string? text, string name)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 4000)
            throw new ArgumentException($"{name} must contain 1 to 4000 characters.");
        return value;
    }
}
