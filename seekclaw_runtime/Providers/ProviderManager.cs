using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        CancellationToken ct,
        Func<ModelInfo, bool>? candidateFilter = null);

    /// <summary>Sends a minimal real completion to verify a model end-to-end.</summary>
    Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default);

    /// <summary>Fetches model identifiers from a provider's model-list endpoint.</summary>
    Task<IReadOnlyList<string>> FetchModelsAsync(ProviderConfig provider, string? url = null, CancellationToken ct = default);
}

public sealed class ProviderManager(
    IConfigStore configStore,
    IModelRegistry registry,
    ILlmClientFactory clientFactory,
    ILlmHttpFactory httpFactory,
    IUsageTracker usageTracker,
    IEventBus eventBus,
    CircuitBreaker? breaker = null) : IProviderManager
{
    private CircuitBreaker? _breaker;
    private RetryConfig Retry => configStore.Config.Routing.Retry;
    // A shared breaker can be injected by the daemon so circuit state survives
    // across isolated per-turn runtimes; standalone runtimes create their own.
    private CircuitBreaker Breaker => _breaker ??= breaker ?? new CircuitBreaker(Retry);

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
        [EnumeratorCancellation] CancellationToken ct,
        Func<ModelInfo, bool>? candidateFilter = null)
    {
        var candidates = candidateFilter is null
            ? BuildCandidates(workspace)
            : BuildCandidates(workspace).Where(candidateFilter).ToList();
        if (candidates.Count == 0)
            throw new LlmException(
                candidateFilter is null ? "No models configured." : "No compatible models configured.",
                retryable: false);

        // Optional automatic failover: when disabled, only the active model is tried and a
        // failed request stops the turn with the real error instead of switching to another
        // provider/model (routing.failoverEnabled, default on).
        if (!configStore.Config.Routing.FailoverEnabled && candidates.Count > 1)
            candidates = candidates.Take(1).ToList();

        LlmException? lastError = null;
        // Every candidate failure is remembered so the final error can explain each model
        // that was tried (the active model first) instead of surfacing only the last
        // fallback's error -- e.g. a default cloud provider that has no API key configured.
        var failures = new List<(string Ref, string Error)>();

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
                IAsyncEnumerator<LlmStreamEvent>? stream = null;

                // The finally below disposes the provider stream on every exit path,
                // including when the consumer abandons the iterator mid-stream (for
                // example Ctrl+C while a turn is still in "thinking"). Without it the
                // response body/connection would stay open until the provider times out.
                try
                {
                    stream = TryOpenStream(model, requestFactory, ct);

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
                            // Once the stream has yielded tokens we can no longer switch providers
                            // mid-answer, so propagate immediately. A non-retryable failure of the
                            // active model (bad key, invalid request...) is also surfaced directly
                            // instead of being hidden by a silent failover. But a fallback candidate
                            // rejecting the request (e.g. HTTP 401 from an unconfigured default)
                            // must not abort the remaining candidates: record it and keep walking.
                            if (committed || (candidateIndex == 0 && !ex.Retryable)) throw;
                            lastError = ex;
                            failures.Add((model.Ref, ex.Message));
                            goto NextCandidate;
                        }

                        committed = true;
                        if (evt is LlmCompleted done) completion = done.Completion;
                        yield return evt;
                    }

                    Breaker.RecordSuccess(model.Ref);
                    RecordUsage(model, completion, stopwatch, success: true);
                    yield break;

                    NextAttempt: ;
                }
                finally
                {
                    if (stream is not null)
                        await stream.DisposeAsync().ConfigureAwait(false);
                }
            }

            // All retries for this candidate were exhausted; remember the failure too.
            if (lastError is not null && !failures.Exists(failure => failure.Ref == model.Ref))
                failures.Add((model.Ref, lastError.Message));

            NextCandidate: ;
        }

        throw BuildFailoverException(failures, lastError);
    }

    /// <summary>
    /// Builds the terminal error when every candidate model failed. A single failure keeps its
    /// original message; multiple failures are aggregated in candidate order (the active model
    /// first) so clients see the real cause instead of a confusing fallback error.
    /// </summary>
    private static LlmException BuildFailoverException(
        IReadOnlyList<(string Ref, string Error)> failures,
        LlmException? lastError)
    {
        if (failures.Count == 0)
            return lastError ?? new LlmException("All providers failed.", retryable: false);
        if (failures.Count == 1)
            return lastError ?? new LlmException(failures[0].Error, retryable: false);

        var builder = new StringBuilder();
        builder.Append($"All candidate models failed after automatic failover ({failures.Count} tried):");
        foreach (var (refName, error) in failures)
        {
            builder.Append('\n');
            builder.Append("- ");
            builder.Append(refName);
            builder.Append(": ");
            builder.Append(error);
        }
        return new LlmException(builder.ToString(), lastError?.StatusCode, retryable: false, inner: lastError);
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

    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        ProviderConfig provider,
        string? url = null,
        CancellationToken ct = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(url)
            ? provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
                ? LlmUrl.JoinV1(provider.BaseUrl, "models")
                : LlmUrl.Join(provider.BaseUrl, "models")
            : url.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new LlmException($"Invalid model list URL: {endpoint}", retryable: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyModelListHeaders(request, provider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 5, 120)));

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.GetClient(provider)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"Fetching models from {provider.Id} timed out.", retryable: false);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Cannot fetch models from {provider.Id}: {ex.Message}", retryable: false, inner: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = body.Length > 240 ? body[..240] : body;
                throw new LlmException(
                    $"Model list request failed for {provider.Id} (HTTP {(int)response.StatusCode}): {detail}",
                    retryable: false);
            }

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new LlmException($"Model list response from {provider.Id} is not valid JSON.", retryable: false, inner: ex);
            }

            var array = root as JsonArray;
            if (array is null && root is JsonObject rootObject)
                array = rootObject["data"] as JsonArray ?? rootObject["models"] as JsonArray;
            if (array is null)
                throw new LlmException($"Model list response from {provider.Id} does not contain a model array.", retryable: false);

            var ids = array
                .Select(ModelId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0)
                throw new LlmException($"Model list response from {provider.Id} contains no model IDs.", retryable: false);
            return ids;
        }
    }

    // ---------------------------------------------------------------- helpers

    private IAsyncEnumerator<LlmStreamEvent> TryOpenStream(
        ModelInfo model, Func<ModelInfo, LlmRequest> requestFactory, CancellationToken ct)
    {
        var client = clientFactory.GetClient(model.Provider.Kind);
        return client.StreamAsync(requestFactory(model), ct).GetAsyncEnumerator(ct);
    }

    private static void ApplyModelListHeaders(HttpRequestMessage request, ProviderConfig provider)
    {
        var key = provider.ResolveApiKey();
        if (provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        if (!string.IsNullOrWhiteSpace(provider.Organization))
            request.Headers.TryAddWithoutValidation("OpenAI-Organization", provider.Organization);
        if (provider.Headers is not null)
            foreach (var (name, value) in provider.Headers)
                request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string? ModelId(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (node is not JsonObject model) return null;
        foreach (var key in new[] { "id", "name", "model" })
        {
            if (model[key] is JsonValue candidate && candidate.TryGetValue<string>(out var id))
                return id;
        }
        return null;
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
