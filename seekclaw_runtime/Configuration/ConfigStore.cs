using System.Text.Json;

namespace SeekClaw.Runtime.Configuration;

public interface IConfigStore
{
    SeekClawConfig Config { get; }
    RuntimeState State { get; }
    bool HasAnomaly { get; }
    string? AnomalyDetail { get; }
    string? BackupConfigFile { get; }

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
    public bool HasAnomaly { get; private set; }
    public string? AnomalyDetail { get; private set; }
    public string? BackupConfigFile { get; private set; }

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
            HasAnomaly = false;
            AnomalyDetail = null;
            BackupConfigFile = null;
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
            try
            {
                var text = File.ReadAllText(_configFile);
                if (string.IsNullOrWhiteSpace(text))
                {
                    HandleAnomaly("配置文件内容为空 (0 字节)");
                }
                else
                {
                    var loaded = JsonSerializer.Deserialize(text, SeekClawJsonContext.Default.SeekClawConfig);
                    if (loaded is not null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.Model))
                            MigrateLegacyProfiles(loaded, _configFile);
                        HasAnomaly = false;
                        AnomalyDetail = null;
                        return loaded;
                    }
                    HandleAnomaly("配置文件反序列化结果为空");
                }
            }
            catch (Exception ex)
            {
                HandleAnomaly($"配置文件损坏或格式错误：{ex.Message}");
            }

            // Anomaly occurred: fallback to in-memory defaults, DO NOT overwrite the corrupt file on disk.
            var fallback = DefaultSeekClawConfig.Build();
            return fallback;
        }

        // First run: file does not exist, seed and save cleanly
        HasAnomaly = false;
        AnomalyDetail = null;
        BackupConfigFile = null;
        var seeded = DefaultSeekClawConfig.Build();
        Config = seeded;
        Save();
        return seeded;
    }

    private void HandleAnomaly(string detail)
    {
        HasAnomaly = true;
        AnomalyDetail = detail;
        try
        {
            var dir = Path.GetDirectoryName(_configFile) ?? "";
            var fileName = Path.GetFileName(_configFile);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(dir, $"{fileName}.corrupt.{timestamp}.bak");
            if (File.Exists(_configFile) && !File.Exists(backupPath))
            {
                File.Copy(_configFile, backupPath, true);
                BackupConfigFile = backupPath;
            }
        }
        catch
        {
            // Best effort backup: ignore I/O errors during backup
        }
    }

    private static void MigrateLegacyProfiles(SeekClawConfig config, string configFile)
    {
        try
        {
            var json = File.ReadAllText(configFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                return;

            string? activeName = null;
            if (root.TryGetProperty("activeProfile", out var activeProp) && activeProp.ValueKind == JsonValueKind.String)
                activeName = activeProp.GetString();

            JsonElement targetProfile = default;
            if (activeName != null && profiles.TryGetProperty(activeName, out var p))
                targetProfile = p;
            else if (profiles.TryGetProperty("default", out var defP))
                targetProfile = defP;
            else
            {
                var first = profiles.EnumerateObject().FirstOrDefault();
                targetProfile = first.Value;
            }

            if (targetProfile.ValueKind == JsonValueKind.Object)
            {
                if (targetProfile.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                    config.Model = modelProp.GetString();
                if (targetProfile.TryGetProperty("provider", out var providerProp) && providerProp.ValueKind == JsonValueKind.String)
                    config.Provider = providerProp.GetString();
                if (targetProfile.TryGetProperty("temperature", out var tempProp) && tempProp.ValueKind == JsonValueKind.Number)
                    config.Temperature = tempProp.GetDouble();
            }
        }
        catch
        {
            // Best effort migration: ignore parse issues
        }
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
