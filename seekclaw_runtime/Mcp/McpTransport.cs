using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.Mcp;

/// <summary>A bidirectional JSON-RPC message pipe to an MCP server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task SendAsync(JsonObject message, CancellationToken ct);
    ChannelReader<JsonObject> Incoming { get; }
}

/// <summary>stdio transport: spawns the server process, newline-delimited JSON-RPC over stdin/stdout.</summary>
public sealed class StdioMcpTransport(string command, IReadOnlyList<string>? args, IReadOnlyDictionary<string, string>? env)
    : IMcpTransport
{
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly ProcessJobScope _jobScope = new();
    private Process? _process;
    private Task? _readLoop;

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    public Task StartAsync(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var arg in args ?? []) startInfo.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (key, value) in env)
                startInfo.Environment[key] = value;

        var processGroupLeader = false;
        if (!OperatingSystem.IsWindows())
        {
            var setsidPath = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
            if (setsidPath is not null)
            {
                // Give the MCP server its own process group so Dispose/timeout can kill
                // descendants that were reparented after the launcher exited.
                startInfo.ArgumentList.Insert(0, command);
                startInfo.FileName = setsidPath;
                processGroupLeader = true;
            }
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP server process: {command}");
        _jobScope.AssignProcess(_process, processGroupLeader);

        // Drain stderr so the child never blocks on a full pipe.
        _ = Task.Run(async () =>
        {
            try { while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch (IOException) { }
        }, CancellationToken.None);

        _readLoop = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonNode.Parse(line) is JsonObject obj)
                            _incoming.Writer.TryWrite(obj);
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            catch (IOException) { }
            finally { _incoming.Writer.TryComplete(); }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (_process is null) throw new InvalidOperationException("Transport not started.");
        await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            _jobScope.Terminate(_process);
            _process.Dispose();
        }
        _jobScope.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
    }
}

/// <summary>
/// HTTP / SSE transport: supports both Streamable HTTP (MCP 2024-11/2025 specification)
/// where JSON-RPC requests are sent via POST and the response is streamed or returned as JSON,
/// and legacy SSE transport where a long-lived GET stream receives responses.
/// </summary>
public class HttpMcpTransport : IMcpTransport
{
    private readonly string _url;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly TaskCompletionSource<string> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _backgroundGate = new();
    private readonly List<Task> _backgroundReads = [];
    private string? _sessionId;
    private Task? _readLoop;

    public HttpMcpTransport(string url, HttpClient? httpClient = null)
    {
        _url = url;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? CreateDefaultClient();
    }

    /// <summary>
    /// SSE streams stay open for the whole session, so the overall timeout must
    /// remain infinite. The connect timeout is what stops an unreachable host from
    /// stalling the whole MCP reload, since a black-holed address otherwise hangs
    /// until the OS gives up.
    /// </summary>
    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    public virtual async Task StartAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linkedCts.Token;
        HttpResponseMessage? response = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            CaptureSessionId(response);

            if (response.IsSuccessStatusCode)
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (mediaType.Contains("event-stream"))
                {
                    // Server returned an SSE stream.
                    var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var ownedResponse = response;
                    response = null;
                    _readLoop = Task.Run(async () =>
                    {
                        using (ownedResponse)
                        using (stream)
                        {
                            try
                            {
                                await foreach (var sse in Providers.SseReader.ReadAsync(stream, _lifetime.Token).ConfigureAwait(false))
                                {
                                    if (sse.Event == "endpoint")
                                    {
                                        _endpoint.TrySetResult(new Uri(new Uri(_url), sse.Data).ToString());
                                    }
                                    else
                                    {
                                        DispatchSseData(sse.Data);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
                        }
                        // NOTE: Do not complete _incoming here because Streamable HTTP servers may finish GET immediately.
                    }, CancellationToken.None);

                    // Wait briefly for endpoint announcement if server announces endpoint via legacy SSE event.
                    var completed = await Task.WhenAny(_endpoint.Task, Task.Delay(3000, token)).ConfigureAwait(false);
                    if (completed != _endpoint.Task) _endpoint.TrySetResult(_url);
                    return;
                }
                else if (mediaType.Contains("json"))
                {
                    // Some servers return a JSON greeting with endpoint metadata (e.g. {"endpoint": "POST /mcp"})
                    using (response)
                    {
                        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (JsonNode.Parse(body) is JsonObject json)
                            {
                                if (json["endpoint"]?.GetValue<string>() is { } epStr)
                                {
                                    var resolvedEndpoint = epStr.StartsWith("POST ", StringComparison.OrdinalIgnoreCase)
                                        ? epStr[5..].Trim()
                                        : epStr.Trim();
                                    _endpoint.TrySetResult(new Uri(new Uri(_url), resolvedEndpoint).ToString());
                                }
                            }
                        }
                        catch { }
                    }
                    response = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Server might only accept POST /mcp and reject GET with 405/400.
            // Fall back directly to POSTing to _url.
        }
        finally
        {
            response?.Dispose();
        }

        _endpoint.TrySetResult(_url);
    }

    public virtual async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linkedCts.Token;

        var endpoint = await _endpoint.Task.WaitAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            CaptureSessionId(response);

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (mediaType.Contains("event-stream"))
            {
                // A streamable-HTTP response may stay open for the whole session. Read it
                // in the background so SendAsync returns and the JSON-RPC request timeout
                // still covers the complete send + response lifetime.
                var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var readTask = Task.Run(async () =>
                {
                    using (response)
                    {
                        try
                        {
                            await foreach (var sse in Providers.SseReader.ReadAsync(stream, _lifetime.Token).ConfigureAwait(false))
                            {
                                if (sse.Event == "endpoint")
                                    _endpoint.TrySetResult(new Uri(new Uri(_url), sse.Data).ToString());
                                else
                                    DispatchSseData(sse.Data);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
                    }
                }, CancellationToken.None);
                lock (_backgroundGate)
                {
                    _backgroundReads.RemoveAll(task => task.IsCompleted);
                    _backgroundReads.Add(readTask);
                }
                return;
            }

            using (response)
            {
                if (mediaType.Contains("json"))
                {
                    var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(body)) DispatchJson(body);
                }
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var id = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(id)) _sessionId = id;
        }
    }

    private void DispatchSseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return;
        DispatchJson(data);
    }

    private void DispatchJson(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj)
            {
                _incoming.Writer.TryWrite(obj);
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject itemObj)
                        _incoming.Writer.TryWrite(itemObj);
                }
            }
        }
        catch (System.Text.Json.JsonException) { }
    }

    private int _disposed;

    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _incoming.Writer.TryComplete();
        Task[] background;
        lock (_backgroundGate) background = _backgroundReads.ToArray();
        foreach (var task in background)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
        _lifetime.Dispose();
    }
}

/// <summary>
/// Backwards-compatible SSE transport alias for HttpMcpTransport.
/// </summary>
public sealed class SseMcpTransport(string url) : HttpMcpTransport(url);
