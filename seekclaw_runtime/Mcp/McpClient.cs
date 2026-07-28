using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace SeekClaw.Runtime.Mcp;

public sealed record McpTool(string Name, string? Description, JsonObject InputSchema);

public sealed record McpPrompt(string Name, string? Description);

public sealed record McpResource(string Uri, string? Name, string? Description);

/// <summary>Minimal MCP (Model Context Protocol) JSON-RPC 2.0 client with request correlation.</summary>
public sealed class McpClient(string serverName, IMcpTransport transport) : IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private long _nextId;
    private Task? _dispatchLoop;

    public string ServerName => serverName;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await transport.StartAsync(ct).ConfigureAwait(false);
        _dispatchLoop = Task.Run(DispatchLoopAsync, CancellationToken.None);

        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "seekclaw", ["version"] = "1.0.0" },
        }, ct).ConfigureAwait(false);

        await transport.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), ct).ConfigureAwait(false);
        var tools = new List<McpTool>();
        foreach (var node in result["tools"] as JsonArray ?? [])
        {
            if (node is not JsonObject tool) continue;
            tools.Add(new McpTool(
                tool["name"]?.GetValue<string>() ?? "",
                tool["description"]?.GetValue<string>(),
                tool["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" }));
        }
        return tools;
    }

    public async Task<(bool Success, string Text)> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.DeepClone(),
        }, ct).ConfigureAwait(false);

        var isError = result["isError"]?.GetValue<bool>() ?? false;
        var text = new StringBuilder();
        foreach (var node in result["content"] as JsonArray ?? [])
        {
            if (node is JsonObject item && item["type"]?.GetValue<string>() == "text")
                text.AppendLine(item["text"]?.GetValue<string>() ?? "");
        }
        return (!isError, text.ToString().TrimEnd());
    }

    public async Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken ct)
    {
        try
        {
            var result = await RequestAsync("prompts/list", new JsonObject(), ct).ConfigureAwait(false);
            return (result["prompts"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(p => new McpPrompt(
                    p["name"]?.GetValue<string>() ?? "",
                    p["description"]?.GetValue<string>()))
                .ToList();
        }
        catch (McpException)
        {
            return []; // server does not support prompts
        }
    }

    public async Task<string?> GetPromptAsync(string name, CancellationToken ct)
    {
        try
        {
            var result = await RequestAsync("prompts/get", new JsonObject { ["name"] = name }, ct).ConfigureAwait(false);
            var text = new StringBuilder();
            foreach (var node in result["messages"] as JsonArray ?? [])
                if (node is JsonObject message && message["content"] is JsonObject content
                    && content["type"]?.GetValue<string>() == "text")
                    text.AppendLine(content["text"]?.GetValue<string>() ?? "");
            return text.Length == 0 ? null : text.ToString().TrimEnd();
        }
        catch (McpException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct)
    {
        try
        {
            var result = await RequestAsync("resources/list", new JsonObject(), ct).ConfigureAwait(false);
            return (result["resources"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(r => new McpResource(
                    r["uri"]?.GetValue<string>() ?? "",
                    r["name"]?.GetValue<string>(),
                    r["description"]?.GetValue<string>()))
                .ToList();
        }
        catch (McpException)
        {
            return [];
        }
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await transport.SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }, ct).ConfigureAwait(false);

            return await tcs.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new McpException($"MCP server '{serverName}' did not answer '{method}' within {RequestTimeout.TotalSeconds:0}s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var message in transport.Incoming.ReadAllAsync().ConfigureAwait(false))
            {
                var idNode = message["id"];
                if (idNode is null) continue; // notification from server — ignored

                long id;
                try { id = idNode.GetValue<long>(); }
                catch (InvalidOperationException) { continue; }

                if (!_pending.TryRemove(id, out var tcs)) continue;

                if (message["error"] is JsonObject error)
                    tcs.TrySetException(new McpException(
                        $"MCP server '{serverName}' error: {error["message"]?.GetValue<string>() ?? "unknown"}"));
                else
                    tcs.TrySetResult(message["result"] as JsonObject ?? new JsonObject());
            }
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new McpException($"MCP server '{serverName}' error: {ex.Message}"));
            _pending.Clear();
            return;
        }

        foreach (var pending in _pending.Values)
            pending.TrySetException(new McpException($"MCP server '{serverName}' disconnected."));
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await transport.DisposeAsync().ConfigureAwait(false);
        if (_dispatchLoop is not null)
        {
            try { await _dispatchLoop.ConfigureAwait(false); } catch { }
        }
    }
}

public sealed class McpException(string message) : Exception(message);
