using System.Diagnostics;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

public sealed record HealthReport(string ProviderId, bool Online, double LatencyMs, string Detail);

public interface IHealthChecker
{
    Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default);
}

/// <summary>Probes a provider's model-listing endpoint to measure availability and latency.</summary>
public sealed class HealthChecker(ILlmHttpFactory httpFactory) : IHealthChecker
{
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
