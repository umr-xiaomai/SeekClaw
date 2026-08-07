using System.Text.Json;
using System.Text.Json.Serialization;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Scheduling;
using SeekClaw.Runtime.Sessions;

namespace SeekClaw.Runtime.Configuration;

/// <summary>Source-generated JSON metadata for all persisted types (Native AOT friendly).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SeekClawConfig))]
[JsonSerializable(typeof(WorkspaceConfig))]
[JsonSerializable(typeof(RuntimeState))]
[JsonSerializable(typeof(SessionHeader))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(ScheduledTask))]
[JsonSerializable(typeof(List<ScheduledTask>))]
[JsonSerializable(typeof(UsageEntry))]
[JsonSerializable(typeof(McpConfig))]
[JsonSerializable(typeof(List<SessionHeader>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class SeekClawJsonContext : JsonSerializerContext
{
    /// <summary>Compact variant for embedded session payloads and protocol serialization.</summary>
    public static SeekClawJsonContext Compact { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    });
}
