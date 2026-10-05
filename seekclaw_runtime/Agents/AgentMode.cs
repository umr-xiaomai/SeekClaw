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

    /// <summary>Manual approval mode (asks confirmation for external file edits, commands, or network).</summary>
    Manual = 4,

    /// <summary>Guardrail approval mode (auto approves safe actions, prompts on detected risk operations).</summary>
    Guardrail = 5,

    /// <summary>Full access mode (unrestricted access to files, tools, and commands without approval pauses).</summary>
    Full = 6,
}

public static class AgentModeExtensions
{
    public static AgentMode Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AgentMode.Guardrail;
        return text.Trim().ToLowerInvariant() switch
        {
            "plan" or "planning" => AgentMode.Plan,
            "readonly" or "read-only" or "ro" => AgentMode.ReadOnly,
            "auto" or "autonomous" => AgentMode.Auto,
            "manual" or "ask" => AgentMode.Manual,
            "guardrail" => AgentMode.Guardrail,
            "full" or "full_access" or "full-access" or "bypass" => AgentMode.Full,
            "edit" => AgentMode.Edit,
            _ => AgentMode.Edit,
        };
    }

    public static string ToModeString(this AgentMode mode) => mode switch
    {
        AgentMode.Manual => "manual",
        AgentMode.Guardrail => "guardrail",
        AgentMode.Full => "full",
        AgentMode.Plan => "plan",
        AgentMode.ReadOnly => "readonly",
        AgentMode.Auto => "auto",
        _ => "edit",
    };

    public static string ToDisplayString(this AgentMode mode) => mode switch
    {
        AgentMode.Plan => "Plan Mode (计划模式)",
        AgentMode.ReadOnly => "ReadOnly Mode (只读模式)",
        AgentMode.Auto => "Auto Mode (自主模式)",
        AgentMode.Manual => "请求批准 (Manual)",
        AgentMode.Full => "完全访问权限 (Full Access)",
        AgentMode.Guardrail => "帮我批准 (Guardrail)",
        _ => "Edit Mode (编辑模式)",
    };

    public static bool IsFullAccess(this AgentMode mode) => mode is AgentMode.Full or AgentMode.Auto;
    public static bool IsReadOnly(this AgentMode mode) => mode is AgentMode.Plan or AgentMode.ReadOnly;
}
