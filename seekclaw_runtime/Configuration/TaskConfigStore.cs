using System.Text.Json;

namespace SeekClaw.Runtime.Configuration;

/// <summary>A task inherits the launching runtime's configuration without persisting its local overrides.</summary>
internal sealed class TaskConfigStore : IConfigStore
{
    private readonly IConfigStore _source;
    /// <summary>Live authority for revocable service configuration; task behavior still uses its snapshot.</summary>
    internal IConfigStore Source => _source is TaskConfigStore task ? task.Source : _source;

    public TaskConfigStore(IConfigStore source)
    {
        _source = source;
        if (source is ConfigStore cs)
        {
            (Config, State) = cs.CloneSnapshot();
        }
        else
        {
            Config = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(source.Config, SeekClawJsonContext.Default.SeekClawConfig), SeekClawJsonContext.Default.SeekClawConfig)!;
            State = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(source.State, SeekClawJsonContext.Default.RuntimeState), SeekClawJsonContext.Default.RuntimeState)!;
        }
    }

    public SeekClawConfig Config { get; }
    public RuntimeState State { get; }
    public bool HasAnomaly => _source.HasAnomaly;
    public string? AnomalyDetail => _source.AnomalyDetail;
    public string? BackupConfigFile => _source.BackupConfigFile;
    public void Save() { }
    public void SaveState() { }
    public void Reload() { }
    public void Reset() => throw new InvalidOperationException("A task configuration cannot reset global user state.");
}
