using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

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
        };
        foreach (var arg in args ?? []) startInfo.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (key, value) in env)
                startInfo.Environment[key] = value;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP server process: {command}");

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
            _process.Dispose();
        }
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
        }
    }
}

/// <summary>
/// SSE transport: GET an event stream; the server announces a POST endpoint via an
/// "endpoint" event, then JSON-RPC responses arrive as "message" events.
/// (HTTP and WebSocket transports are reserved for future versions.)
/// </summary>
public sealed class SseMcpTransport(string url) : IMcpTransport
{
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TaskCompletionSource<string> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readLoop;

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    public async Task StartAsync(CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        _readLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var sse in Providers.SseReader.ReadAsync(stream, _lifetime.Token).ConfigureAwait(false))
                {
                    if (sse.Event == "endpoint")
                    {
                        _endpoint.TrySetResult(new Uri(new Uri(url), sse.Data).ToString());
                    }
                    else
                    {
                        try
                        {
                            if (JsonNode.Parse(sse.Data) is JsonObject obj)
                                _incoming.Writer.TryWrite(obj);
                        }
                        catch (System.Text.Json.JsonException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { }
            finally { _incoming.Writer.TryComplete(); }
        }, CancellationToken.None);

        // Wait briefly for the endpoint announcement; some servers accept POST to the same URL.
        var completed = await Task.WhenAny(_endpoint.Task, Task.Delay(5000, ct)).ConfigureAwait(false);
        if (completed != _endpoint.Task) _endpoint.TrySetResult(url);
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var endpoint = await _endpoint.Task.WaitAsync(ct).ConfigureAwait(false);
        using var content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _incoming.Writer.TryComplete();
        _http.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
