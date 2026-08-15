using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Configuration;

/// <summary>
/// Builds the baseline global configuration used when ~/.seekclaw/config.json does not
/// exist yet. The config store serializes this object to JSON on first run, so there is
/// no shipped static template file. Provider and model data are intentionally left empty:
/// users must create their own provider/model entries before the daemon can route work.
/// </summary>
public static class DefaultSeekClawConfig
{
    public static SeekClawConfig Build() => new()
    {
        ActiveProfile = "default",
        Profiles = new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new ProfileConfig(),
        },
        Providers = [],
        Routing = new RoutingConfig
        {
            FailoverEnabled = true,
            DeepSeekOptimizationEnabled = false,
            Strategies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            Fallback = [],
            LoadBalance = "priority",
            Retry = new RetryConfig
            {
                MaxAttempts = 3,
                BaseDelaySeconds = 1.0,
                MaxDelaySeconds = 20.0,
                CircuitBreakThreshold = 4,
                CircuitCooldownSeconds = 60.0,
            },
        },
        Agent = new AgentConfig
        {
            MaxSteps = 40,
            AutoVerify = true,
            MaxRepairAttempts = 3,
            EnableContextCompaction = true,
            MaxOutputContinuations = 6,
            SystemPrompt = "system/default",
            ThinkingBudgetTokens = 16_384,
            ReasoningLevel = ReasoningLevel.High,
            MaxToolOutputChars = 60_000,
            BashTimeoutSeconds = 180,
            ScheduledTurnTimeoutSeconds = 1_800,
        },
        Mcp = new McpConfig(),
    };
}
