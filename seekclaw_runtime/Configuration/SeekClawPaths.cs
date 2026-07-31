namespace SeekClaw.Runtime.Configuration;

/// <summary>Well-known file system locations for global (per-user) state.</summary>
public static class SeekClawPaths
{
    public static string Home { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seekclaw");

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string StateFile => Path.Combine(Home, "state.json");
    /// <summary>SQLite database for sessions and Desktop project metadata.</summary>
    public static string DatabaseFile => Path.Combine(Home, "seekclaw.db");
    public static string UsageFile => Path.Combine(Home, "usage.jsonl");
    public static string LogsDir => Path.Combine(Home, "logs");
    public static string SkillsDir => Path.Combine(Home, "skills");
    public static string PromptsDir => Path.Combine(Home, "prompts");
    public static string SessionsDir => Path.Combine(Home, "sessions");

    /// <summary>Directory of the running application (default prompts / seed config ship here).</summary>
    public static string AppDir => AppContext.BaseDirectory;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SkillsDir);
    }
}
