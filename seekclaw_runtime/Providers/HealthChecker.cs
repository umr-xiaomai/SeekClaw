using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Providers;

public sealed record HealthReport(string ProviderId, bool Online, double LatencyMs, string Detail);
public sealed record HealthCheckResult(string Name, bool Ok, string Detail);

public interface IHealthChecker
{
    Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default);
    IReadOnlyList<HealthCheckResult> RunChecks(Workspaces.WorkspaceInfo workspace);
}

/// <summary>Probes a provider's chat/completions or messages endpoint to measure availability and latency.</summary>
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
        var hasProvider = providers.Count > 0;
        results.Add(new HealthCheckResult("Provider Config", hasProvider, hasProvider ? $"{providers.Count} provider(s) configured. Active provider: {configStore.Config.Provider ?? "-"}, model: {configStore.Config.Model ?? "-"}" : "No providers configured"));

        // 4. Memory File Check
        var memoryFile = workspace.MemoryFile;
        results.Add(new HealthCheckResult("Workspace Memory", File.Exists(memoryFile), File.Exists(memoryFile) ? memoryFile : "Memory file empty/missing"));

        return results;
    }

    public async Task<HealthReport> CheckAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        var isAnthropic = provider.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase);
        var url = isAnthropic
            ? LlmUrl.JoinV1(provider.BaseUrl, "messages")
            : LlmUrl.Join(provider.BaseUrl, "chat/completions");

        var modelName = provider.Models.Count > 0 && !string.IsNullOrWhiteSpace(provider.Models[0].Id)
            ? provider.Models[0].Id
            : (isAnthropic ? "claude-3-haiku-20240307" : "gpt-4o-mini");

        var payload = isAnthropic
            ? new JsonObject
            {
                ["model"] = modelName,
                ["max_tokens"] = 1,
                ["messages"] = new JsonArray { (JsonNode)new JsonObject { ["role"] = "user", ["content"] = "ping" } }
            }
            : new JsonObject
            {
                ["model"] = modelName,
                ["max_tokens"] = 1,
                ["messages"] = new JsonArray { (JsonNode)new JsonObject { ["role"] = "user", ["content"] = "ping" } },
                ["stream"] = false
            };

        var http = httpFactory.GetClient(provider);
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        var key = provider.ResolveApiKey();
        if (isAnthropic)
        {
            if (!string.IsNullOrWhiteSpace(key)) message.Headers.TryAddWithoutValidation("x-api-key", key);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            if (!string.IsNullOrWhiteSpace(provider.Organization))
            {
                message.Headers.TryAddWithoutValidation("OpenAI-Organization", provider.Organization);
            }
        }

        if (provider.Headers is { Count: > 0 })
        {
            foreach (var (headerKey, headerVal) in provider.Headers)
            {
                if (!string.IsNullOrWhiteSpace(headerKey))
                    message.Headers.TryAddWithoutValidation(headerKey, headerVal);
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(provider.TimeoutSeconds, 15)));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new HealthReport(provider.Id, true, stopwatch.Elapsed.TotalMilliseconds, "ok");
            }

            if (status is 401 or 403)
            {
                return new HealthReport(provider.Id, false, stopwatch.Elapsed.TotalMilliseconds, $"reachable, auth failed (HTTP {status})");
            }

            // HTTP 400/422/429 means the endpoint is online and actively parsed our request
            if (status is 400 or 422 or 429)
            {
                var tag = status == 429 ? "rate limited" : "reachable";
                return new HealthReport(provider.Id, true, stopwatch.Elapsed.TotalMilliseconds, $"{tag} (HTTP {status})");
            }

            return new HealthReport(provider.Id, false, stopwatch.Elapsed.TotalMilliseconds, $"HTTP {status}");
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
