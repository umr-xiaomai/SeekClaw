using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Execution;

/// <summary>Includes creation time so a recycled operating-system PID cannot claim an old task.</summary>
internal static class ProcessIdentity
{
    public static bool IsAlive(JsonObject record)
    {
        if (record["processId"] is not JsonValue id || !id.TryGetValue<int>(out var pid)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToString("O") == record["processStartedAt"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
