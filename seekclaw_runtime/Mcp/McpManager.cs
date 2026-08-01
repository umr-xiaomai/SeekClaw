using System.Text;
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
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Lock _statusGate = new();

    public IReadOnlyList<McpServerStatus> Status
    {
        get { lock (_statusGate) return _status.ToList(); }
    }

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
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ResetConnectionsAsync().ConfigureAwait(false);
            lock (_statusGate) _status.Clear();

            foreach (var (name, server) in LoadServerConfigs(workspace))
            {
                if (!server.Enabled)
                {
                    lock (_statusGate)
                        _status.Add(new McpServerStatus(name, server.Transport, false, 0, "disabled"));
                    continue;
                }

                try
                {
                    var status = await ConnectOneAsync(name, server, ct).ConfigureAwait(false);
                    lock (_statusGate) _status.Add(status);
                }
                catch (Exception ex) when (ex is McpException or InvalidOperationException or IOException or HttpRequestException)
                {
                    lock (_statusGate)
                        _status.Add(new McpServerStatus(name, server.Transport, false, 0, ex.Message));
                }
            }

            return Status;
        }
        finally
        {
            _connectionGate.Release();
        }
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

        // Auto-discover tools. Names are sanitized to [a-zA-Z0-9_-] (the character set
        // most providers accept for function names) and de-duplicated so one server
        // cannot register two tools under the same local name.
        var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var baseName = McpToolAdapter.BuildName(name, tool.Name);
            var uniqueName = baseName;
            var suffix = 2;
            while (!usedNames.Add(uniqueName))
                uniqueName = $"{baseName}_{suffix++}";
            _registrations.Add(toolRegistry.Register(new McpToolAdapter(client, tool, uniqueName)));
        }

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
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetConnectionsAsync().ConfigureAwait(false);
            lock (_statusGate) _status.Clear();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async ValueTask ResetConnectionsAsync()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }
}

/// <summary>Bridges a remote MCP tool into the local tool registry as mcp__server__tool.</summary>
public sealed class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpTool _tool;
    private readonly string _name;
    private readonly bool _isMutating;

    public McpToolAdapter(McpClient client, McpTool tool, bool isMutating = false)
        : this(client, tool, BuildName(client.ServerName, tool.Name), isMutating)
    {
    }

    public McpToolAdapter(McpClient client, McpTool tool, string name, bool isMutating = false)
    {
        _client = client;
        _tool = tool;
        _name = name;
        _isMutating = isMutating;
    }

    public string Name => _name;
    public string Description => _tool.Description ?? $"MCP tool {_tool.Name} from server {_client.ServerName}.";
    public JsonObject ParameterSchema => _tool.InputSchema;
    public bool Mutating => _isMutating || InferMutating(_tool.Name);
    public string StatusLabel => $"Calling {_client.ServerName}";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        try
        {
            var (success, text) = await _client.CallToolAsync(_tool.Name, arguments, ct).ConfigureAwait(false);
            var output = context.Truncate(text.Length == 0 ? "(no output)" : text, "MCP output");
            return success
                ? ToolResult.Ok(output, $"{_tool.Name} via {_client.ServerName}")
                : ToolResult.Fail(output);
        }
        catch (McpException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Builds a provider-safe local name: MCP servers may use spaces, dots or unicode
    /// in tool names, but most LLM providers only accept [a-zA-Z0-9_-] (≤64 chars).
    /// </summary>
    public static string BuildName(string serverName, string toolName)
    {
        var full = $"mcp__{SanitizeSegment(serverName)}__{SanitizeSegment(toolName)}";
        return full.Length > 64 ? full[..64] : full;
    }

    internal static string SanitizeSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.Length == 0 ? "tool" : sb.ToString();
    }

    private static bool InferMutating(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("write") || n.Contains("edit") || n.Contains("create") ||
               n.Contains("delete") || n.Contains("update") || n.Contains("modify") ||
               n.Contains("patch") || n.Contains("apply") || n.Contains("exec") ||
               n.Contains("run") || n.Contains("git_commit") || n.Contains("git_push");
    }
}
