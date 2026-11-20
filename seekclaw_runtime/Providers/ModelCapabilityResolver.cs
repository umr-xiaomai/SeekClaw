using System.Text.RegularExpressions;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

/// <summary>
/// Fills neutral reasoning capabilities for models that were added without an explicit
/// declaration. Providers rarely report effort levels, so the resolver combines model-list
/// metadata with a small family table and falls back to "no reasoning" instead of advertising
/// the neutral maximum (which would leak unsupported effort values onto the wire).
/// </summary>
public static class ModelCapabilityResolver
{
    private sealed record Profile(
        bool Thinking,
        bool Reasoning,
        ReasoningLevel MaxLevel,
        IReadOnlyDictionary<ReasoningLevel, string>? WireEffort = null,
        bool MaxCompletionTokens = true);

    private static readonly Dictionary<ReasoningLevel, string> XaiWireEffort = new()
    {
        [ReasoningLevel.Low] = "low",
        [ReasoningLevel.Medium] = "high",
        [ReasoningLevel.High] = "high",
        [ReasoningLevel.Max] = "high",
    };

    /// <summary>Model families whose reasoning effort values are known to be safe on the wire.</summary>
    private static readonly (Regex Pattern, Profile Profile)[] Families =
    [
        // OpenAI reasoning models accept low|medium|high (plus minimal on newer GPT-5 builds).
        (new Regex(@"\b(o1|o3|o4)(-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Profile(true, true, ReasoningLevel.High)),
        (new Regex(@"\bgpt-5", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Profile(true, true, ReasoningLevel.High)),
        // Anthropic reasoning is expressed as a thinking budget, so any neutral level maps.
        (new Regex(@"\bclaude-(3[.-]7|4([.-]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Profile(true, true, ReasoningLevel.Max)),
        (new Regex(@"\bclaude-(opus|sonnet|haiku)-4", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Profile(true, true, ReasoningLevel.Max)),
        // Gemini OpenAI-compatible endpoints accept reasoning_effort low|medium|high but keep max_tokens.
        (new Regex(@"\bgemini-(2[.-]5|3)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Profile(true, true, ReasoningLevel.High, MaxCompletionTokens: false)),
        // xAI exposes a binary low|high effort ladder and keeps max_tokens.
        (new Regex(@"\bgrok-(3-mini|4)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Profile(true, true, ReasoningLevel.High, XaiWireEffort, MaxCompletionTokens: false)),
    ];

    /// <summary>Applies inference to every provider/model in the configuration.</summary>
    public static void Apply(SeekClawConfig config)
    {
        foreach (var provider in config.Providers)
        {
            foreach (var model in provider.Models)
                Apply(provider, model);
        }
    }

    /// <summary>
    /// Applies inference when the model has no explicit reasoning declaration. Values coming
    /// from the user (or an earlier resolution) are preserved verbatim.
    /// </summary>
    /// <param name="metadataSupportsReasoning">
    /// Model-list metadata hint: true/false when the provider explicitly states reasoning
    /// support, null when the response carries no such signal.
    /// </param>
    public static void Apply(ProviderConfig provider, ModelConfig model, bool? metadataSupportsReasoning = null)
    {
        if (IsConfigured(model.Capabilities)) return;

        if (metadataSupportsReasoning == false)
        {
            model.Capabilities.Thinking = false;
            model.Capabilities.Reasoning = false;
            model.Capabilities.MaxReasoningLevel = ReasoningLevel.None;
            return;
        }

        if (FindProfile(provider, model.Id) is { } profile)
        {
            model.Capabilities.Thinking = profile.Thinking;
            model.Capabilities.Reasoning = profile.Reasoning;
            model.Capabilities.MaxReasoningLevel = profile.MaxLevel;
            return;
        }

        if (metadataSupportsReasoning == true)
        {
            // The gateway confirms reasoning but the family is unknown: stay on the portable
            // effort ladder (Anthropic has no effort field and uses a thinking budget instead).
            model.Capabilities.Thinking = true;
            model.Capabilities.Reasoning = true;
            model.Capabilities.MaxReasoningLevel = provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
                ? ReasoningLevel.Max
                : ReasoningLevel.High;
            return;
        }

        model.Capabilities.Thinking = false;
        model.Capabilities.Reasoning = false;
        model.Capabilities.MaxReasoningLevel = ReasoningLevel.None;
    }

    /// <summary>
    /// True when the model already carries a non-default capability declaration, in which case
    /// inference must not overwrite it.
    /// </summary>
    public static bool IsConfigured(ModelCapabilities capabilities) =>
        capabilities.Thinking
        || capabilities.Reasoning
        || capabilities.MaxReasoningLevel != ReasoningLevel.Max;

    /// <summary>Family-specific wire value for a neutral level (e.g. xAI accepts only low|high).</summary>
    public static bool TryWireEffort(
        ProviderConfig provider,
        ModelConfig model,
        ReasoningLevel level,
        out string value)
    {
        value = "";
        if (FindProfile(provider, model.Id)?.WireEffort is not { } wire) return false;
        if (!wire.TryGetValue(level, out var mapped)) return false;
        value = mapped;
        return true;
    }

    /// <summary>
    /// Reasoning models normally use max_completion_tokens, but some compatible gateways
    /// (Gemini, xAI) still only accept max_tokens.
    /// </summary>
    public static bool PrefersMaxCompletionTokens(ProviderConfig provider, ModelConfig model) =>
        FindProfile(provider, model.Id)?.MaxCompletionTokens ?? true;

    private static Profile? FindProfile(ProviderConfig provider, string modelId)
    {
        var deepSeekModel = new ModelConfig { Id = modelId };
        if (ReasoningLevelAdapter.IsDeepSeek(provider, deepSeekModel))
            return new Profile(true, true, ReasoningLevel.Max);

        foreach (var (pattern, profile) in Families)
        {
            if (pattern.IsMatch(modelId)) return profile;
        }
        return null;
    }
}
