using System.Diagnostics;
using System.Runtime.CompilerServices;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Runtime.Providers;

public interface IProviderManager
{
    /// <summary>Resolves the model to use right now: workspace override → profile → routing strategy.</summary>
    ModelInfo ResolveActive(WorkspaceConfig? workspace = null);

    /// <summary>Ordered failover chain starting with the active model.</summary>
    IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null);

    /// <summary>
    /// Streams a completion with retry (exponential backoff + jitter), circuit breaking and
    /// automatic provider failover. Once the first token arrives the stream is committed and
    /// errors propagate instead of switching providers mid-answer.
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        Func<ModelInfo, LlmRequest> requestFactory,
        WorkspaceConfig? workspace,
        CancellationToken ct);

    /// <summary>Sends a minimal real completion to verify a model end-to-end.</summary>
    Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default);
}

public sealed class ProviderManager(
    IConfigStore configStore,
    IModelRegistry registry,
    ILlmClientFactory clientFactory,
    IUsageTracker usageTracker,
    IEventBus eventBus) : IProviderManager
{
    private CircuitBreaker? _breaker;
    private RetryConfig Retry => configStore.Config.Routing.Retry;
    private CircuitBreaker Breaker => _breaker ??= new CircuitBreaker(Retry);

    public ModelInfo ResolveActive(WorkspaceConfig? workspace = null)
    {
        var candidates = BuildCandidates(workspace);
        if (candidates.Count == 0)
            throw new LlmException(
                "No models configured. Run 'seekclaw provider add' or edit ~/.seekclaw/config.json.",
                retryable: false);
        return candidates[0];
    }

    public IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null)
    {
        var config = configStore.Config;
        var profile = config.GetActiveProfile();
        var explicitRefs = new List<string>();

        // 1. Workspace override beats everything.
        if (!string.IsNullOrWhiteSpace(workspace?.Model))
            explicitRefs.Add(Qualify(workspace!.Model!, workspace.Provider ?? profile.Provider));

        // 2. Active profile selection.
        if (!string.IsNullOrWhiteSpace(profile.Model))
            explicitRefs.Add(Qualify(profile.Model!, profile.Provider));

        // 3. Routing strategy candidates (load-balanced).
        var strategy = workspace?.Strategy ?? profile.Strategy ?? "balanced";
        var strategyRefs = config.Routing.Strategies.TryGetValue(strategy, out var refs)
            ? ApplyLoadBalance(strategy, refs)
            : [];

        // 4. Global fallback chain.
        var chain = explicitRefs
            .Concat(strategyRefs)
            .Concat(config.Routing.Fallback)
            .Select(registry.Resolve)
            .Where(m => m is not null && m.Provider.Enabled)
            .Select(m => m!)
            .DistinctBy(m => m.Ref, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chain.Count == 0)
            chain = registry.All().ToList();

        // Skip open circuits, but never filter down to nothing.
        var healthy = chain.Where(m => !Breaker.IsOpen(m.Ref)).ToList();
        return healthy.Count > 0 ? healthy : chain;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        Func<ModelInfo, LlmRequest> requestFactory,
        WorkspaceConfig? workspace,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var candidates = BuildCandidates(workspace);
        if (candidates.Count == 0)
            throw new LlmException("No models configured.", retryable: false);

        LlmException? lastError = null;

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var model = candidates[candidateIndex];
            if (candidateIndex > 0)
                eventBus.Publish(new ProviderSwitchedEvent(
                    candidates[candidateIndex - 1].Ref, model.Ref, lastError?.Message ?? "failover"));

            for (var attempt = 1; attempt <= Math.Max(1, Retry.MaxAttempts); attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var stopwatch = Stopwatch.StartNew();
                var committed = false;
                LlmCompletion? completion = null;
                var stream = TryOpenStream(model, requestFactory, ct);

                while (true)
                {
                    LlmStreamEvent? evt;
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false)) break;
                        evt = stream.Current;
                    }
                    catch (LlmException ex) when (!committed && ex.Retryable && !ct.IsCancellationRequested)
                    {
                        lastError = ex;
                        Breaker.RecordFailure(model.Ref);
                        RecordUsage(model, null, stopwatch, success: false);
                        await stream.DisposeAsync().ConfigureAwait(false);

                        if (attempt < Retry.MaxAttempts)
                        {
                            var delay = BackoffDelay(attempt);
                            eventBus.Publish(new ProviderRetryEvent(model.Ref, attempt, ex.Message, delay));
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                        }
                        goto NextAttempt;
                    }
                    catch (LlmException ex)
                    {
                        Breaker.RecordFailure(model.Ref);
                        RecordUsage(model, completion, stopwatch, success: false);
                        await stream.DisposeAsync().ConfigureAwait(false);
                        if (committed || !ex.Retryable) throw;
                        lastError = ex;
                        goto NextCandidate;
                    }

                    committed = true;
                    if (evt is LlmCompleted done) completion = done.Completion;
                    yield return evt;
                }

                await stream.DisposeAsync().ConfigureAwait(false);
                Breaker.RecordSuccess(model.Ref);
                RecordUsage(model, completion, stopwatch, success: true);
                yield break;

                NextAttempt: ;
            }

            NextCandidate: ;
        }

        throw lastError ?? new LlmException("All providers failed.", retryable: false);
    }

    public async Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(
        ModelInfo model, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = clientFactory.GetClient(model.Provider.Kind);
            var request = new LlmRequest
            {
                Provider = model.Provider,
                Model = model.Model,
                Messages = [ChatMessage.User("Reply with the single word: ok")],
                MaxTokens = 16,
            };

            var text = "";
            await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
                if (evt is LlmCompleted done)
                    text = done.Completion.Text;

            stopwatch.Stop();
            return (true, string.IsNullOrWhiteSpace(text) ? "(empty reply)" : text.Trim(), stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or OperationCanceledException)
        {
            stopwatch.Stop();
            return (false, ex.Message, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    // ---------------------------------------------------------------- helpers

    private IAsyncEnumerator<LlmStreamEvent> TryOpenStream(
        ModelInfo model, Func<ModelInfo, LlmRequest> requestFactory, CancellationToken ct)
    {
        var client = clientFactory.GetClient(model.Provider.Kind);
        return client.StreamAsync(requestFactory(model), ct).GetAsyncEnumerator(ct);
    }

    private void RecordUsage(ModelInfo model, LlmCompletion? completion, Stopwatch stopwatch, bool success)
    {
        stopwatch.Stop();
        var usage = completion?.Usage ?? new TokenUsage(0, 0);
        usageTracker.Record(new UsageEntry
        {
            Provider = model.Provider.Id,
            Model = model.Model.Id,
            InputTokens = usage.InputTokens,
            TotalInputTokens = usage.TotalInputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            CacheCreationInputTokens = usage.CacheCreationInputTokens,
            OutputTokens = usage.OutputTokens,
            Cost = ComputeCost(model.Model, usage),
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Success = success,
        });
    }

    public static decimal ComputeCost(ModelConfig model, TokenUsage usage) =>
        (model.InputPricePerMTok * usage.InputTokens + model.OutputPricePerMTok * usage.OutputTokens) / 1_000_000m;

    private TimeSpan BackoffDelay(int attempt)
    {
        var baseDelay = Retry.BaseDelaySeconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 0.5 + 0.75; // 0.75x – 1.25x
        return TimeSpan.FromSeconds(Math.Min(baseDelay * jitter, Retry.MaxDelaySeconds));
    }

    private static string Qualify(string modelRef, string? providerId) =>
        modelRef.Contains('/') || string.IsNullOrWhiteSpace(providerId) ? modelRef : $"{providerId}/{modelRef}";

    private List<string> ApplyLoadBalance(string strategy, List<string> refs)
    {
        var config = configStore.Config;
        switch (config.Routing.LoadBalance.ToLowerInvariant())
        {
            case "roundrobin":
            {
                var state = configStore.State;
                var cursor = state.RoundRobinCursors.GetValueOrDefault(strategy);
                state.RoundRobinCursors[strategy] = (cursor + 1) % Math.Max(1, refs.Count);
                configStore.SaveState();
                return [.. refs.Skip(cursor % Math.Max(1, refs.Count)), .. refs.Take(cursor % Math.Max(1, refs.Count))];
            }
            case "leastused":
                return OrderByUsage(refs, a => a.Calls);
            case "lowestcost":
                return refs.OrderBy(r =>
                {
                    var model = registry.Resolve(r);
                    return model is null ? decimal.MaxValue : model.Model.InputPricePerMTok + model.Model.OutputPricePerMTok;
                }).ToList();
            case "fastest":
                return OrderByUsage(refs, a => a.AvgLatencyMs);
            default: // priority / sticky keep configured order
                return refs;
        }
    }

    private List<string> OrderByUsage<TKey>(List<string> refs, Func<UsageAggregate, TKey> key)
    {
        var aggregates = usageTracker.Aggregate()
            .ToDictionary(a => $"{a.Provider}/{a.Model}", StringComparer.OrdinalIgnoreCase);
        return refs
            .OrderBy(r => aggregates.TryGetValue(r, out var agg) ? key(agg) : default)
            .ToList();
    }
}
