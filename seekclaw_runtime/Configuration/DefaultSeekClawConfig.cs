using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Configuration;

/// <summary>
/// Builds the baseline global configuration used when ~/.seekclaw/config.json does not
/// exist yet. The config store serializes this object to JSON on first run, so there is
/// no shipped static template file — provider/model data stays fully data-driven while
/// the default set itself lives in code.
/// </summary>
public static class DefaultSeekClawConfig
{
    public static SeekClawConfig Build() => new()
    {
        ActiveProfile = "default",
        Profiles = new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new() { Strategy = "balanced" },
            ["work"] = new() { Strategy = "quality" },
            ["local"] = new() { Provider = "ollama", Strategy = "offline" },
        },
        Providers =
        [
            Anthropic(),
            OpenAi(),
            Google(),
            OpenRouter(),
            Ollama(),
            MiMo(),
            LmStudio(),
        ],
        Routing = new RoutingConfig
        {
            FailoverEnabled = true,
            Strategies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["fast"] = ["openai/gpt-5.5-mini", "google/gemini-2.5-flash"],
                ["balanced"] = ["anthropic/claude-sonnet-5", "openai/gpt-5.5", "google/gemini-2.5-pro", "mimo/mimo-v2-pro"],
                ["quality"] = ["anthropic/claude-opus-5", "openai/gpt-5.5", "google/gemini-2.5-pro", "mimo/mimo-v2-pro"],
                ["cheap"] = ["openai/gpt-5.5-mini", "google/gemini-2.5-flash", "openrouter/auto"],
                ["offline"] = ["ollama/qwen3", "lmstudio/local-model"],
            },
            Fallback = ["anthropic/claude-sonnet-5", "openai/gpt-5.5", "google/gemini-2.5-flash", "mimo/mimo-v2-pro", "openrouter/auto", "ollama/qwen3"],
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
            ReviewModels = [],
            MaxReviewRounds = 2,
            SystemPrompt = "system/default",
            ThinkingBudgetTokens = 16_384,
            ReasoningLevel = ReasoningLevel.High,
            MaxToolOutputChars = 60_000,
            BashTimeoutSeconds = 180,
        },
        Mcp = new McpConfig(),
    };

    private static ProviderConfig Anthropic() => new()
    {
        Id = "anthropic",
        Name = "Anthropic",
        Kind = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        PromptCaching = true,
        Priority = 0,
        Models =
        [
            new ModelConfig
            {
                Id = "claude-opus-5",
                Alias = "opus",
                ContextWindow = 200_000,
                MaxOutput = 64_000,
                Capabilities = new ModelCapabilities
                {
                    Thinking = true,
                    Vision = true,
                    JsonMode = true,
                    Reasoning = true,
                    MaxReasoningLevel = ReasoningLevel.Ultra,
                },
                InputPricePerMTok = 15m,
                OutputPricePerMTok = 75m,
                Tags = ["quality"],
            },
            new ModelConfig
            {
                Id = "claude-sonnet-5",
                Alias = "sonnet",
                ContextWindow = 200_000,
                MaxOutput = 64_000,
                Capabilities = new ModelCapabilities
                {
                    Thinking = true,
                    Vision = true,
                    JsonMode = true,
                    Reasoning = true,
                    MaxReasoningLevel = ReasoningLevel.Ultra,
                },
                InputPricePerMTok = 3m,
                OutputPricePerMTok = 15m,
                Tags = ["balanced"],
            },
        ],
    };

    private static ProviderConfig OpenAi() => new()
    {
        Id = "openai",
        Name = "OpenAI",
        Kind = "openai",
        BaseUrl = "https://api.openai.com/v1",
        Priority = 1,
        Models =
        [
            new ModelConfig
            {
                Id = "gpt-5.5",
                ContextWindow = 400_000,
                MaxOutput = 128_000,
                Capabilities = new ModelCapabilities
                {
                    Vision = true,
                    JsonMode = true,
                    Reasoning = true,
                    MaxReasoningLevel = ReasoningLevel.XHigh,
                },
                InputPricePerMTok = 1.25m,
                OutputPricePerMTok = 10m,
                Tags = ["balanced", "quality"],
            },
            new ModelConfig
            {
                Id = "gpt-5.5-mini",
                ContextWindow = 400_000,
                MaxOutput = 128_000,
                Capabilities = new ModelCapabilities { Vision = true, JsonMode = true },
                InputPricePerMTok = 0.25m,
                OutputPricePerMTok = 2m,
                Tags = ["fast", "cheap"],
            },
        ],
    };

    private static ProviderConfig Google() => new()
    {
        Id = "google",
        Name = "Google (OpenAI-compatible endpoint)",
        Kind = "openai",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
        Priority = 2,
        Models =
        [
            new ModelConfig
            {
                Id = "gemini-2.5-pro",
                Alias = "gemini-pro",
                ContextWindow = 1_000_000,
                MaxOutput = 65_536,
                Capabilities = new ModelCapabilities { Vision = true, JsonMode = true, Reasoning = true },
                InputPricePerMTok = 1.25m,
                OutputPricePerMTok = 10m,
                Tags = ["quality"],
            },
            new ModelConfig
            {
                Id = "gemini-2.5-flash",
                Alias = "gemini-flash",
                ContextWindow = 1_000_000,
                MaxOutput = 65_536,
                Capabilities = new ModelCapabilities { Vision = true, JsonMode = true, Reasoning = true },
                InputPricePerMTok = 0.3m,
                OutputPricePerMTok = 2.5m,
                Tags = ["fast", "cheap"],
            },
        ],
    };

    private static ProviderConfig OpenRouter() => new()
    {
        Id = "openrouter",
        Name = "OpenRouter",
        Kind = "openai",
        BaseUrl = "https://openrouter.ai/api/v1",
        Enabled = false,
        Priority = 3,
        Models =
        [
            new ModelConfig
            {
                Id = "openrouter/auto",
                Alias = "auto",
                ContextWindow = 128_000,
                MaxOutput = 16_384,
                Tags = ["cheap"],
            },
        ],
    };

    private static ProviderConfig Ollama() => new()
    {
        Id = "ollama",
        Name = "Ollama (local)",
        Kind = "openai",
        BaseUrl = "http://localhost:11434/v1",
        Enabled = false,
        Priority = 4,
        Models =
        [
            new ModelConfig
            {
                Id = "qwen3",
                ContextWindow = 32_768,
                MaxOutput = 8_192,
                Tags = ["offline", "cheap"],
            },
        ],
    };

    private static ProviderConfig MiMo() => new()
    {
        Id = "mimo",
        Name = "MiMo (Xiaomi)",
        Kind = "openai",
        BaseUrl = "https://token-plan-cn.xiaomimimo.com/v1",
        ApiKey = "",
        Priority = 3,
        Models =
        [
            new ModelConfig
            {
                Id = "mimo-v2-pro",
                Alias = "mimo",
                ContextWindow = 128_000,
                MaxOutput = 8_192,
                Capabilities = new ModelCapabilities { Vision = true, JsonMode = true },
                Tags = ["balanced"],
            },
        ],
    };

    private static ProviderConfig LmStudio() => new()
    {
        Id = "lmstudio",
        Name = "LM Studio (local)",
        Kind = "openai",
        BaseUrl = "http://localhost:1234/v1",
        Enabled = false,
        Priority = 5,
        Models =
        [
            new ModelConfig
            {
                Id = "local-model",
                ContextWindow = 32_768,
                MaxOutput = 8_192,
                Capabilities = new ModelCapabilities { ToolCalling = false, Mcp = false },
                Tags = ["offline"],
            },
        ],
    };
}
