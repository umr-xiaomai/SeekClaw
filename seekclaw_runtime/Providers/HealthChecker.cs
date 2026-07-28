using System.Diagnostics;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

public sealed record HealthReport(string ProviderId, bool Online, double LatencyMs, string Detail);
public sealed record HealthCheckResult(string Name, bool Ok, string Detail);

public interface IHealthChecker
{
    Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default);
    IReadOnlyList<HealthCheckResult> RunChecks(Workspaces.WorkspaceInfo workspace);
}

/// <summary>Probes a provider's model-listing endpoint to measure availability and latency.</summary>
public sealed class HealthChecker(ILlmHttpFactory httpFactory, Configuration.IConfigStore configStore) : IHealthChecker
{
    public IReadOnlyList<HealthCheckResult> RunChecks(Workspaces.WorkspaceInfo workspace)
    {
        var results = new List<HealthCheckResult>();

        // 1. Workspace Root Check
        var rootOk = Directory.Exists(workspace.Root);
        results.Add(new HealthCheckResult("Workspace Root", rootOk, rootOk ? workspace.Root : $"Directory not found: {workspace.Root}"));

        // 2. SeekClaw Directory Check
        var seekClawOk = Directory.Exists(workspace.SeekClawDir);
        results.Add(new HealthCheckResult("SeekClaw Metadata Dir", seekClawOk, seekClawOk ? workspace.SeekClawDir : "Not initialized (run seekclaw init)"));

        // 3. Provider Configuration Check
        var providers = configStore.Config.Providers;
        var activeProfile = configStore.Config.GetActiveProfile();
        var hasProvider = providers.Count > 0;
        results.Add(new HealthCheckResult("Provider Config", hasProvider, hasProvider ? $"{providers.Count} provider(s) configured. Active profile: {configStore.Config.ActiveProfile}" : "No providers configured"));

        // 4. Memory File Check
        var memoryFile = workspace.MemoryFile;
        results.Add(new HealthCheckResult("Workspace Memory", File.Exists(memoryFile), File.Exists(memoryFile) ? memoryFile : "Memory file empty/missing"));

        return results;
    }

    public async Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        var url = provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? LlmUrl.JoinV1(provider.BaseUrl, "models")
            : LlmUrl.Join(provider.BaseUrl, "models");

        var http = httpFactory.GetClient(provider);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);

        var key = provider.ResolveApiKey();
        if (provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(key)) message.Headers.TryAddWithoutValidation("x-api-key", key);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(key))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();

            // Any HTTP answer proves the endpoint is alive; 401/403 mean "reachable, api key missing/invalid".
            var status = (int)response.StatusCode;
            var online = response.IsSuccessStatusCode || status is 401 or 403;
            var detail = response.IsSuccessStatusCode
                ? "ok"
                : status is 401 or 403 ? $"reachable, auth failed (HTTP {status})" : $"HTTP {status}";
            return new HealthReport(provider.Id, online, stopwatch.Elapsed.TotalMilliseconds, detail);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthReport(provider.Id, false, stopwatch.Elapsed.TotalMilliseconds, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new HealthReport(provider.Id, false, stopwatch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }
}
