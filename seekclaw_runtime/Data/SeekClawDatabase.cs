using Microsoft.Data.Sqlite;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Data;

/// <summary>Shared SQLite storage for durable user data. Configuration remains file based.</summary>
public sealed class SeekClawDatabase
{
    private static readonly object InitializationGate = new();
    private static readonly HashSet<string> InitializedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connectionString;

    public SeekClawDatabase(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? SeekClawPaths.DatabaseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
        EnsureInitialized();
    }

    public string FilePath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public static string ScopeKey(WorkspaceInfo workspace) =>
        (workspace.IsGlobal ? "global|" : "workspace|") + PathKey(workspace.Root);

    public static string PathKey(string path)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private void EnsureInitialized()
    {
        lock (InitializationGate)
        {
            if (InitializedFiles.Contains(FilePath) && File.Exists(FilePath)) return;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout=10000;

                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT NOT NULL PRIMARY KEY,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS sessions (
                    scope TEXT NOT NULL,
                    id TEXT NOT NULL,
                    workspace TEXT NULL,
                    title TEXT NULL,
                    archived INTEGER NOT NULL DEFAULT 0,
                    reasoning_level INTEGER NOT NULL,
                    network_enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (scope, id)
                );

                CREATE INDEX IF NOT EXISTS ix_sessions_scope_updated
                    ON sessions(scope, archived, updated_at DESC);

                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    scope TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    FOREIGN KEY (scope, session_id)
                        REFERENCES sessions(scope, id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_messages_session
                    ON messages(scope, session_id, id);

                CREATE TABLE IF NOT EXISTS scheduled_tasks (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    workspace TEXT NULL,
                    prompt TEXT NOT NULL,
                    cron TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    last_run_at TEXT NULL,
                    next_run_at TEXT NULL,
                    last_status TEXT NULL,
                    last_error TEXT NULL,
                    last_output TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_scheduled_tasks_enabled
                    ON scheduled_tasks(enabled, next_run_at);

                CREATE TABLE IF NOT EXISTS migrations (
                    scope TEXT NOT NULL PRIMARY KEY,
                    source_dir TEXT NOT NULL,
                    imported_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();

            // Migration: databases created before the per-session network toggle
            // existed get the column (existing sessions default to enabled).
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'network_enabled';";
                if (Convert.ToInt64(check.ExecuteScalar()) == 0)
                {
                    using var alter = connection.CreateCommand();
                    alter.CommandText = "ALTER TABLE sessions ADD COLUMN network_enabled INTEGER NOT NULL DEFAULT 1;";
                    alter.ExecuteNonQuery();
                }
            }

            InitializedFiles.Add(FilePath);
        }
    }
}
