using System.Text.Json;

namespace SeekClaw.Runtime.Configuration;

public interface IConfigStore
{
    SeekClawConfig Config { get; }
    RuntimeState State { get; }

    void Save();
    void SaveState();
    void Reload();
    /// <summary>Restores the in-memory objects and on-disk files to factory defaults.</summary>
    void Reset();
}

/// <summary>
/// Loads / persists ~/.seekclaw/config.json. On first run the store seeds the file by
/// serializing <see cref="DefaultSeekClawConfig"/> to JSON; provider and model data
/// stay fully data-driven after that point.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly Lock _gate = new();
    private readonly string _configFile;
    private readonly string _stateFile;

    public SeekClawConfig Config { get; private set; }
    public RuntimeState State { get; private set; }

    public ConfigStore(string? configFile = null, string? stateFile = null)
    {
        _configFile = configFile ?? SeekClawPaths.ConfigFile;
        _stateFile = stateFile ?? SeekClawPaths.StateFile;
        Config = LoadConfig();
        State = LoadState();
    }

    public void Reload()
    {
        lock (_gate)
        {
            Config = LoadConfig();
            State = LoadState();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!);
            var json = JsonSerializer.Serialize(Config, SeekClawJsonContext.Default.SeekClawConfig);
            File.WriteAllText(_configFile, json);
        }
    }

    public void SaveState()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var json = JsonSerializer.Serialize(State, SeekClawJsonContext.Default.RuntimeState);
            File.WriteAllText(_stateFile, json);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Config = DefaultSeekClawConfig.Build();
            State = new RuntimeState();
            DeleteIfExists(_configFile);
            DeleteIfExists(_stateFile);
        }

        Save();
        SaveState();
    }

    private SeekClawConfig LoadConfig()
    {
        if (File.Exists(_configFile))
        {
            var loaded = TryDeserialize(_configFile, SeekClawJsonContext.Default.SeekClawConfig);
            if (loaded is not null) return loaded;
        }

        var seeded = DefaultSeekClawConfig.Build();
        Config = seeded;
        Save();
        return seeded;
    }

    private RuntimeState LoadState() =>
        (File.Exists(_stateFile) ? TryDeserialize(_stateFile, SeekClawJsonContext.Default.RuntimeState) : null)
        ?? new RuntimeState();

    private static T? TryDeserialize<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), typeInfo);
        }
        catch (JsonException)
        {
            // Corrupt file: keep it on disk for the user to inspect, fall back to defaults.
            return null;
        }
    }

    private static void DeleteIfExists(string file)
    {
        if (!File.Exists(file)) return;
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller rebuilds the files below; keep reset best-effort.
        }
    }
}
