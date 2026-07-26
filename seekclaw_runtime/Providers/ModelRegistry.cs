using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

/// <summary>A model together with its owning provider; Ref is "providerId/modelId".</summary>
public sealed record ModelInfo(ProviderConfig Provider, ModelConfig Model)
{
    public string Ref => $"{Provider.Id}/{Model.Id}";
    public ModelCapabilities Capabilities => Model.Capabilities;
}

public interface IModelRegistry
{
    IReadOnlyList<ModelInfo> All(bool includeDisabledProviders = false);

    /// <summary>Resolves "provider/model", a model alias, or a bare model id (when unambiguous).</summary>
    ModelInfo? Resolve(string reference);

    IReadOnlyList<ModelInfo> Search(string query);
}

public sealed class ModelRegistry(IConfigStore configStore) : IModelRegistry
{
    public IReadOnlyList<ModelInfo> All(bool includeDisabledProviders = false) =>
        configStore.Config.Providers
            .Where(p => includeDisabledProviders || p.Enabled)
            .OrderBy(p => p.Priority)
            .SelectMany(p => p.Models.Select(m => new ModelInfo(p, m)))
            .ToList();

    public ModelInfo? Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var all = All(includeDisabledProviders: true);

        var slash = reference.IndexOf('/');
        if (slash > 0)
        {
            var providerId = reference[..slash];
            var modelId = reference[(slash + 1)..];
            var scoped = all.Where(m => m.Provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToList();
            return scoped.FirstOrDefault(m => m.Model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                ?? scoped.FirstOrDefault(m => modelId.Equals(m.Model.Alias, StringComparison.OrdinalIgnoreCase));
        }

        var byAlias = all.Where(m => reference.Equals(m.Model.Alias, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byAlias.Count == 1) return byAlias[0];

        var byId = all.Where(m => m.Model.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        return byId.Count == 1 ? byId[0] : null;
    }

    public IReadOnlyList<ModelInfo> Search(string query) =>
        All(includeDisabledProviders: true)
            .Where(m => m.Ref.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (m.Model.Alias?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (m.Model.Tags?.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false))
            .ToList();
}
