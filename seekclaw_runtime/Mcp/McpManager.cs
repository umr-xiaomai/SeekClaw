using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Mcp;

public sealed record McpServerStatus(string Name, string Transport, bool Connected, int ToolCount, string? Error);

public interface IMcpManager : IAsyncDisposable
{
    /// <summary>Connects every enabled MCP server and registers discovered tools and prompts.</summary>
    Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(WorkspaceInfo workspace, CancellationToken ct);

    IReadOnlyDictionary<string, McpServerConfig> LoadServerConfigs(WorkspaceInfo workspace);

    IReadOnlyList<McpServerStatus> Status { get; }
}

public sealed class McpManager(
    IConfigStore configStore,
    IToolRegistry toolRegistry,
    IPromptRegistry promptRegistry) : IMcpManager
{
    private readonly List<McpClient> _clients = [];
    private readonly List<IDisposable> _registrations = [];
    private readonly List<McpServerStatus> _status = [];

    public IReadOnlyList<McpServerStatus> Status => _status;

    public IReadOnlyDictionary<string, McpServerConfig> LoadServerConfigs(WorkspaceInfo workspace)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, server) in configStore.Config.Mcp.Servers)
            servers[name] = server;

        // <workspace>/mcp/servers.json
        var serversFile = Path.Combine(workspace.McpDir, "servers.json");
        if (File.Exists(serversFile))
        {
            try
            {
                var fileConfig = JsonSerializer.Deserialize(
                    File.ReadAllText(serversFile), SeekClawJsonContext.Default.McpConfig);
                foreach (var (name, server) in fileConfig?.Servers ?? [])
                    servers[name] = server;
            }
            catch (JsonException) { }
        }

        // Workspace .seekclaw/config.json wins.
        foreach (var (name, server) in workspace.Config?.Mcp?.Servers ?? [])
            servers[name] = server;

        return servers;
    }

    public async Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        _status.Clear();

        foreach (var (name, server) in LoadServerConfigs(workspace))
        {
            if (!server.Enabled)
            {
                _status.Add(new McpServerStatus(name, server.Transport, false, 0, "disabled"));
                continue;
            }

            try
            {
                var status = await ConnectOneAsync(name, server, ct).ConfigureAwait(false);
                _status.Add(status);
            }
            catch (Exception ex) when (ex is McpException or InvalidOperationException or IOException or HttpRequestException)
            {
                _status.Add(new McpServerStatus(name, server.Transport, false, 0, ex.Message));
            }
        }

        return _status;
    }

    private async Task<McpServerStatus> ConnectOneAsync(string name, McpServerConfig server, CancellationToken ct)
    {
        IMcpTransport transport = server.Transport.ToLowerInvariant() switch
        {
            "stdio" when !string.IsNullOrWhiteSpace(server.Command) =>
                new StdioMcpTransport(server.Command!, server.Args, server.Env),
            "sse" when !string.IsNullOrWhiteSpace(server.Url) =>
                new SseMcpTransport(server.Url!),
            "http" or "websocket" =>
                throw new McpException($"Transport '{server.Transport}' is reserved but not implemented yet."),
            _ => throw new McpException($"Server '{name}': invalid transport/command/url combination."),
        };

        var client = new McpClient(name, transport);
        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new McpException($"Server '{name}' timed out during initialization.");
        }

        _clients.Add(client);

        // Auto-discover tools.
        var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
        foreach (var tool in tools)
            _registrations.Add(toolRegistry.Register(new McpToolAdapter(client, tool)));

        // Auto-discover prompts into the prompt registry.
        var prompts = await client.ListPromptsAsync(ct).ConfigureAwait(false);
        foreach (var prompt in prompts)
        {
            var promptName = prompt.Name;
            _registrations.Add(promptRegistry.Register(new PromptContribution(
                $"mcp:{name}:{promptName}", PromptSlot.Tool,
                async (_, token) => await client.GetPromptAsync(promptName, token).ConfigureAwait(false))));
        }

        return new McpServerStatus(name, server.Transport, true, tools.Count, null);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }
}

/// <summary>Bridges a remote MCP tool into the local tool registry as mcp__server__tool.</summary>
public sealed class McpToolAdapter(McpClient client, McpTool tool) : ITool
{
    public string Name => $"mcp__{client.ServerName}__{tool.Name}";
    public string Description => tool.Description ?? $"MCP tool {tool.Name} from server {client.ServerName}.";
    public JsonObject ParameterSchema => tool.InputSchema;
    public bool Mutating => false;
    public string StatusLabel => $"Calling {client.ServerName}";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        try
        {
            var (success, text) = await client.CallToolAsync(tool.Name, arguments, ct).ConfigureAwait(false);
            var output = context.Truncate(text.Length == 0 ? "(no output)" : text, "MCP output");
            return success
                ? ToolResult.Ok(output, $"{tool.Name} via {client.ServerName}")
                : ToolResult.Fail(output);
        }
        catch (McpException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
