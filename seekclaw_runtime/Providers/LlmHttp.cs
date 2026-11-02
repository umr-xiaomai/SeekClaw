using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.IO;

namespace SeekClaw.Runtime.Providers;

/// <summary>Caches one HttpClient per provider (proxy / timeout / headers differ per provider).</summary>
public interface ILlmHttpFactory
{
    HttpClient GetClient(ProviderConfig provider);
}

public sealed class LlmHttpFactory : ILlmHttpFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public HttpClient GetClient(ProviderConfig provider)
    {
        var key = $"{provider.Id}|{provider.BaseUrl}|{provider.Proxy}|{provider.TimeoutSeconds}";
        return _clients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(Math.Min(provider.TimeoutSeconds, 30)),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (!string.IsNullOrWhiteSpace(provider.Proxy))
            {
                handler.Proxy = new WebProxy(provider.Proxy);
                handler.UseProxy = true;
            }

            // Provider clients apply TimeoutSeconds to both response headers and stream
            // consumption, so a stalled streaming/vision request cannot wait forever.
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }
}

/// <summary>One server-sent event: optional event name + data payload.</summary>
public readonly record struct SseMessage(string? Event, string Data);

internal static class LlmTransportErrors
{
    public static LlmException ReadingResponse(string providerId, Exception error)
    {
        // Buffered HttpContent reads can wrap the actual ResponseEnded failure.
        var cause = error.GetBaseException();
        var detail = ReferenceEquals(cause, error) ? error.Message : $"{error.Message} {cause.Message}";
        return new LlmException(
            $"Response from {providerId} was interrupted while reading: {detail}",
            retryable: error is not InvalidDataException && cause is not InvalidDataException,
            inner: error);
    }
}

public static class SseReader
{
    /// <summary>Parses a text/event-stream response body into discrete messages.</summary>
    public static IAsyncEnumerable<SseMessage> ReadAsync(
        Stream stream, CancellationToken ct) =>
        ReadAsync(stream, Timeout.InfiniteTimeSpan, ct);

    /// <summary>
    /// Parses a text/event-stream response body into discrete messages with a sliding idle timeout watchdog.
    /// As long as transport activity (data, events, or SSE comments/keep-alives) continues within the idle window,
    /// the watchdog timer is reset and the stream continues indefinitely.
    /// </summary>
    public static async IAsyncEnumerable<SseMessage> ReadAsync(
        Stream stream, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var lines = new BoundedLineReader(reader, ProtocolText.MaximumMessageChars);
        string? eventName = null;
        var data = new List<string>();
        var dataChars = 0;
        var hasTimeout = idleTimeout > TimeSpan.Zero && idleTimeout != Timeout.InfiniteTimeSpan;

        using var idleCts = hasTimeout ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;

        while (true)
        {
            idleCts?.CancelAfter(idleTimeout);
            string? line;
            try
            {
                var readToken = idleCts?.Token ?? ct;
                line = await lines.ReadLineAsync(readToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && (idleCts?.IsCancellationRequested ?? false))
            {
                throw new TimeoutException($"Stream idle timeout after {idleTimeout.TotalSeconds:0}s without activity.");
            }

            if (line is null) break;

            // Activity occurred (including comments/keep-alives) - reset idle timer
            idleCts?.CancelAfter(idleTimeout);

            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    yield return new SseMessage(eventName, string.Join('\n', data));
                    data.Clear();
                    dataChars = 0;
                }
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' ')) value = value[1..];
                dataChars += value.Length + (data.Count == 0 ? 0 : 1);
                if (dataChars > ProtocolText.MaximumMessageChars)
                    throw new InvalidDataException("SSE message exceeds the 16 MiB text limit.");
                data.Add(value);
            }
            // comments (":") and other fields are recognized as transport activity
        }

        if (data.Count > 0)
            yield return new SseMessage(eventName, string.Join('\n', data));
    }
}

public static class LlmUrl
{
    /// <summary>Joins a base URL and path segment tolerating trailing/leading slashes.</summary>
    public static string Join(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>Anthropic-style endpoint: appends v1/&lt;path&gt; unless the base already ends in /v1.</summary>
    public static string JoinV1(string baseUrl, string path)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{trimmed}/{path}"
            : $"{trimmed}/v1/{path}";
    }
}
