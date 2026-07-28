namespace SeekClaw.Runtime.Agents;

/// <summary>
/// Execution mode governing agent authority and behavior.
/// </summary>
public enum AgentMode
{
    /// <summary>Standard interactive developer mode (reads & edits files, auto verify enabled).</summary>
    Edit = 0,

    /// <summary>Plan-first mode (disables mutating tools, guides model to produce structured plans).</summary>
    Plan = 1,

    /// <summary>Strict read-only safety mode (blocks all mutating tools).</summary>
    ReadOnly = 2,

    /// <summary>Fully autonomous mode (all tools enabled, maximum repair attempts).</summary>
    Auto = 3,
}

public static class AgentModeExtensions
{
    public static AgentMode Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AgentMode.Edit;
        return text.Trim().ToLowerInvariant() switch
        {
            "plan" or "planning" => AgentMode.Plan,
            "readonly" or "read-only" or "ro" => AgentMode.ReadOnly,
            "auto" or "autonomous" => AgentMode.Auto,
            _ => AgentMode.Edit,
        };
    }

    public static string ToDisplayString(this AgentMode mode) => mode switch
    {
        AgentMode.Plan => "Plan Mode",
        AgentMode.ReadOnly => "ReadOnly Mode",
        AgentMode.Auto => "Auto Mode",
        _ => "Edit Mode",
    };

    public static bool IsReadOnly(this AgentMode mode) =>
        mode is AgentMode.Plan or AgentMode.ReadOnly;
}
