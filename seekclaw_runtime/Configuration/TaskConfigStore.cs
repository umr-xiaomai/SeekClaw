using System.Text.Json;

namespace SeekClaw.Runtime.Configuration;

/// <summary>A task inherits the launching runtime's configuration without persisting its local overrides.</summary>
internal sealed class TaskConfigStore(IConfigStore source) : IConfigStore
{
    public SeekClawConfig Config { get; } = JsonSerializer.Deserialize(
        JsonSerializer.Serialize(source.Config, SeekClawJsonContext.Default.SeekClawConfig), SeekClawJsonContext.Default.SeekClawConfig)!;
    public RuntimeState State { get; } = JsonSerializer.Deserialize(
        JsonSerializer.Serialize(source.State, SeekClawJsonContext.Default.RuntimeState), SeekClawJsonContext.Default.RuntimeState)!;
    public bool HasAnomaly => source.HasAnomaly;
    public string? AnomalyDetail => source.AnomalyDetail;
    public string? BackupConfigFile => source.BackupConfigFile;
    public void Save() { }
    public void SaveState() { }
    public void Reload() { }
    public void Reset() => throw new InvalidOperationException("A task configuration cannot reset global user state.");
}
