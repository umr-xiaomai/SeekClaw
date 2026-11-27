using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Cli;

internal enum CliTheme
{
    Auto,
    Light,
    Dark,
}

/// <summary>Stores the terminal appearance as a per-user CLI preference.</summary>
internal sealed class CliThemeSettings(string path)
{
    internal CliThemeSettings() : this(Path.Combine(SeekClawPaths.Home, "cli-theme")) { }

    internal CliTheme? Load()
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal bool TrySave(CliTheme theme)
    {
        // Rename a complete file so two running CLI instances never read a partial choice.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, theme.ToString().ToLowerInvariant() + "\n");
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static CliTheme? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "auto" => CliTheme.Auto,
        "light" => CliTheme.Light,
        "dark" => CliTheme.Dark,
        _ => null,
    };
}
