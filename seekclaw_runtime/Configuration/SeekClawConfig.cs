using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Configuration;

/// <summary>Root of ~/.seekclaw/config.json. All model/provider data is user data — never hard-coded.</summary>
public sealed class SeekClawConfig
{
    public string ActiveProfile { get; set; } = "default";
    public Dictionary<string, ProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = new ProfileConfig(),
    };

    public List<ProviderConfig> Providers { get; set; } = [];
    public RoutingConfig Routing { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();

    public ProfileConfig GetActiveProfile()
    {
        if (!Profiles.TryGetValue(ActiveProfile, out var profile))
        {
            profile = new ProfileConfig();
            Profiles[ActiveProfile] = profile;
        }
        return profile;
    }

    public ProviderConfig? FindProvider(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A named runtime environment (e.g. work / home / local) switchable in one command.</summary>
public sealed class ProfileConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    /// <summary>Routing strategy: fast | balanced | quality | cheap | offline.</summary>
    public string? Strategy { get; set; }
    public double? Temperature { get; set; }
    /// <summary>Agent mode: edit | plan | readonly | auto.</summary>
    public string? Mode { get; set; }
}

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>Wire protocol: openai | anthropic. Every OpenAI-compatible service (Ollama, LM Studio, OpenRouter, Azure…) uses "openai".</summary>
    public string Kind { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? Organization { get; set; }
    public string? Proxy { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Optional URL used by the Desktop "fetch models" action; defaults to the provider /models endpoint.</summary>
    public string? ModelListUrl { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Optional neutral-level to provider wire-value overrides.</summary>
    public Dictionary<string, string>? ReasoningEffortMap { get; set; }
    /// <summary>
    /// Enables provider-native prompt caching hints. OpenAI-compatible providers keep using
    /// their automatic prefix cache; Anthropic requests add explicit cache checkpoints.
    /// Disable this for an Anthropic-compatible endpoint that does not accept cache_control.
    /// </summary>
    public bool PromptCaching { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public List<ModelConfig> Models { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Returns the API key explicitly stored in the configuration file.</summary>
    public string? ResolveApiKey() => ApiKey;
}

public sealed class ModelConfig
{
    public string Id { get; set; } = "";
    public string? Alias { get; set; }
    public int ContextWindow { get; set; } = 128_000;
    public int MaxOutput { get; set; } = 8_192;
    public ModelCapabilities Capabilities { get; set; } = new();
    /// <summary>USD per 1M tokens.</summary>
    public decimal InputPricePerMTok { get; set; }
    public decimal OutputPricePerMTok { get; set; }
    /// <summary>Free-form routing tags: fast, quality, cheap, offline…</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Unified capability model. Business code must branch on these flags,
/// never on provider or model names.
/// </summary>
public sealed class ModelCapabilities
{
    public bool Streaming { get; set; } = true;
    public bool Thinking { get; set; }
    public bool Vision { get; set; }
    public bool Image { get; set; }
    public bool ToolCalling { get; set; } = true;
    public bool JsonMode { get; set; }
    public bool Reasoning { get; set; }
    /// <summary>Highest neutral reasoning level accepted by this model.</summary>
    public ReasoningLevel MaxReasoningLevel { get; set; } = ReasoningLevel.Max;
    public bool Embedding { get; set; }
    public bool Mcp { get; set; } = true;
}

public sealed class RoutingConfig
{
    /// <summary>strategy name → ordered "provider/model" references.</summary>
    public Dictionary<string, List<string>> Strategies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Global failover chain, tried in order after the active model fails.</summary>
    public List<string> Fallback { get; set; } = [];

    /// <summary>priority | roundRobin | leastUsed | lowestCost | fastest | sticky</summary>
    public string LoadBalance { get; set; } = "priority";

    public RetryConfig Retry { get; set; } = new();
}

public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 3;
    public double BaseDelaySeconds { get; set; } = 1.0;
    public double MaxDelaySeconds { get; set; } = 20.0;
    /// <summary>Consecutive failures before a model's circuit opens.</summary>
    public int CircuitBreakThreshold { get; set; } = 4;
    public double CircuitCooldownSeconds { get; set; } = 60.0;
}

public sealed class AgentConfig
{
    public int MaxSteps { get; set; } = 40;
    public bool AutoVerify { get; set; } = true;
    public int MaxRepairAttempts { get; set; } = 3;
    /// <summary>Summarizes older history when the context window would overflow, so a single turn can keep going.</summary>
    public bool EnableContextCompaction { get; set; } = true;
    /// <summary>Consecutive output-cap truncations allowed before a turn gives up instead of ending silently.</summary>
    public int MaxOutputContinuations { get; set; } = 6;
    /// <summary>Cross-vendor review panel: "provider/model" refs run adversarial review after each turn. Empty = auto-pick.</summary>
    public List<string> ReviewModels { get; set; } = [];
    /// <summary>How many review-fix rounds a turn may run before the panel stops.</summary>
    public int MaxReviewRounds { get; set; } = 2;
    /// <summary>Agent mode: edit | plan | readonly | auto.</summary>
    public string Mode { get; set; } = "edit";
    /// <summary>Prompt key of the main system prompt (relative to prompts/, no extension).</summary>
    public string SystemPrompt { get; set; } = "system/default";
    public int ThinkingBudgetTokens { get; set; } = 16_384;
    public ReasoningLevel ReasoningLevel { get; set; } = ReasoningLevel.High;
    /// <summary>Hard cap safeguard; effective tool output budget adapts to the model context window.</summary>
    public int MaxToolOutputChars { get; set; } = 60_000;
    public int BashTimeoutSeconds { get; set; } = 180;
}

public sealed class McpConfig
{
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class McpServerConfig
{
    /// <summary>stdio | sse (http / websocket reserved).</summary>
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Per-workspace overrides stored in &lt;workspace&gt;/.seekclaw/config.json.</summary>
public sealed class WorkspaceConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Strategy { get; set; }
    public double? Temperature { get; set; }
    public string? Mode { get; set; }
    public string? SystemPrompt { get; set; }
    public List<string>? DisabledSkills { get; set; }
    public List<string>? DisabledTools { get; set; }
    public McpConfig? Mcp { get; set; }
    public bool? AutoVerify { get; set; }
    /// <summary>Overrides the auto-detected build/check command used by the verify loop.</summary>
    public string? VerifyCommand { get; set; }
}

/// <summary>Small mutable runtime state persisted in ~/.seekclaw/state.json (round-robin cursors, last session…).</summary>
public sealed class RuntimeState
{
    public Dictionary<string, int> RoundRobinCursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastSessionId { get; set; }
    public Dictionary<string, string> DisabledSkills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
