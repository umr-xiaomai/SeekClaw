namespace SeekClaw.Runtime.Configuration;

/// <summary>Well-known file system locations for global (per-user) state.</summary>
public static class SeekClawPaths
{
    /// <summary>The current user's profile directory (parent of the SeekClaw state folder).</summary>
    public static string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Home => Path.Combine(HomeDir, ".seekclaw");

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

    /// <summary>
    /// True when the path is the user's profile directory or SeekClaw's own global state
    /// directory (or something inside it) — neither is ever a valid project root.
    /// </summary>
    public static bool IsForbiddenProjectPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var home = Path.GetFullPath(HomeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var state = Path.GetFullPath(Home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (full.Equals(home, comparison) || full.Equals(state, comparison))
            return true;
        return full.StartsWith(state + Path.DirectorySeparatorChar, comparison);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SkillsDir);
    }
}
