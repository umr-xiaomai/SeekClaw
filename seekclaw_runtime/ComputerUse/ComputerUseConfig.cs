namespace SeekClaw.Runtime.ComputerUse;

/// <summary>
/// Configuration for Computer Use subsystem.
/// Can be independently toggled on/off without affecting normal SeekClaw operations.
/// </summary>
public sealed class ComputerUseConfig
{
    /// <summary>Whether Computer Use tools are registered and enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Driver selection: "auto", "windows", "linux", "mac", or "vision". Default "auto".</summary>
    public string Driver { get; set; } = "auto";

    /// <summary>Whether dangerous/irreversible operations trigger an interactive confirmation.</summary>
    public bool RequireConfirmation { get; set; } = true;

    /// <summary>Maximum computer action steps permitted in a single turn.</summary>
    public int MaxStepsPerTurn { get; set; } = 30;

    /// <summary>Delay in milliseconds between successive atomic actions to allow UI stabilization.</summary>
    public int ActionDelayMs { get; set; } = 300;
}
