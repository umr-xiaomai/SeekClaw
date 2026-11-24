using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Mcp;

public sealed record McpServerStatus(string Name, string Transport, bool Connected, int ToolCount, string? Error)
{
    public bool Connecting { get; init; }
}

public interface IMcpManager : IAsyncDisposable
{
    /// <summary>Refreshes configuration and cached registrations without starting a server.</summary>
    Task<IReadOnlyList<McpServerStatus>> PrepareAsync(WorkspaceInfo workspace, CancellationToken ct);
    /// <summary>Explicitly connects configured servers, used by administration and manual reload.</summary>
    Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(WorkspaceInfo workspace, CancellationToken ct);
    /// <summary>Resets model-visible MCP schema activation once at the start of a user turn.</summary>
    void BeginTurn(string sessionId);
    void MarkConnecting(WorkspaceInfo workspace);
    IReadOnlyDictionary<string, McpServerConfig> LoadServerConfigs(WorkspaceInfo workspace);
    IReadOnlyList<McpServerStatus> Status { get; }
    IReadOnlyList<McpServerStatus> GetStatus(WorkspaceInfo workspace);
}

/// <summary>Per-runtime tool registrations backed by process-owned, lazily opened connections.</summary>
public sealed class McpManager : IMcpManager
{
    private readonly IConfigStore _configStore;
    private readonly IToolRegistry _tools;
    private readonly IPromptRegistry _prompts;
    private readonly McpConnectionPool _connections;
    private readonly bool _ownsConnections;
    private readonly List<IDisposable> _registrations = [];
    private readonly Dictionary<string, HashSet<string>> _loaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private readonly IDisposable? _searchRegistration;
    private readonly IDisposable _catalogRegistration;
    private readonly McpToolSearchTool _search;
    private List<McpServerStatus> _status = [];
    private WorkspaceInfo? _workspace;
    private IReadOnlyDictionary<string, McpServerConfig> _configs = new Dictionary<string, McpServerConfig>();
    private string? _binding;
    private bool _disposed;
    private string? _statusScope;

    public McpManager(IConfigStore configStore, IToolRegistry toolRegistry, IPromptRegistry promptRegistry,
        McpAuthentication? authentication = null, McpConnectionPool? connections = null)
    {
        _configStore = configStore;
        _tools = toolRegistry;
        _prompts = promptRegistry;
        _ownsConnections = connections is null;
        _connections = connections ?? new McpConnectionPool(authentication);
        _search = new McpToolSearchTool(this);
        _searchRegistration = _tools.TryRegister(_search);
        _catalogRegistration = _prompts.Register(new PromptContribution("mcp:catalog", PromptSlot.Tool,
            (context, _) => ValueTask.FromResult(CatalogInstruction(context))));
    }

    public IReadOnlyList<McpServerStatus> Status
    {
        get
        {
            WorkspaceInfo? workspace;
            lock (_state) workspace = _workspace;
            if (workspace is not null) return GetStatus(workspace);
            lock (_state) return _status.ToList();
        }
    }

    public IReadOnlyList<McpServerStatus> GetStatus(WorkspaceInfo workspace)
    {
        var statuses = _connections.Status(workspace, LoadServerConfigs(workspace)).ToList();
        lock (_state)
        {
            if (_statusScope != McpConnectionPool.Scope(workspace)) return statuses;
            return statuses.Select(status => status.Error == "disabled" ? status : !status.Connected && status.Error is null
                    && _status.Any(local => local.Name.Equals(status.Name, StringComparison.OrdinalIgnoreCase) && local.Connecting)
                ? status with { Connecting = true } : status).ToList();
        }
    }

    public void BeginTurn(string sessionId)
    {
        lock (_state) _loaded[sessionId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal bool IsLoaded(string sessionId, string toolName)
    {
        lock (_state) return _loaded.TryGetValue(sessionId, out var names) && names.Contains(toolName);
    }

    internal bool IsAvailable(WorkspaceInfo workspace, bool networkEnabled) => LoadServerConfigs(workspace)
        .Any(pair => pair.Value.Enabled && (networkEnabled || !IsRemote(pair.Value)));

    public IReadOnlyDictionary<string, McpServerConfig> LoadServerConfigs(WorkspaceInfo workspace)
    {
        // Model, role and tool preferences remain task snapshots. Service identities and
        // revocations follow their live source so an old task cannot resurrect a removed server.
        var source = _configStore is TaskConfigStore task ? task.Source : _configStore;
        var servers = new Dictionary<string, McpServerConfig>(source.Config.Mcp.Servers, StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(workspace.McpDir, "servers.json");
        if (File.Exists(file))
        {
            try
            {
                var config = JsonSerializer.Deserialize(File.ReadAllText(file), SeekClawJsonContext.Default.McpConfig);
                foreach (var pair in config?.Servers ?? []) servers[pair.Key] = pair.Value;
            }
            catch (JsonException) { }
        }
        var inline = workspace.Config?.Mcp;
        if (!workspace.IsGlobal && File.Exists(workspace.ConfigFile))
        {
            try
            {
                inline = JsonSerializer.Deserialize(File.ReadAllText(workspace.ConfigFile), SeekClawJsonContext.Default.WorkspaceConfig)?.Mcp;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable live policy cannot safely fall back to a stale allow-list.
                throw new McpException("Workspace MCP configuration could not be read; retry after the configuration is available.");
            }
        }
        foreach (var pair in inline?.Servers ?? []) servers[pair.Key] = pair.Value;
        return servers;
    }

    public void MarkConnecting(WorkspaceInfo workspace)
    {
        lock (_state)
        {
            _statusScope = McpConnectionPool.Scope(workspace);
            _status = LoadServerConfigs(workspace).Select(x => new McpServerStatus(
                x.Key, x.Value.Transport, false, 0, x.Value.Enabled ? null : "disabled") { Connecting = x.Value.Enabled }).ToList();
        }
    }

    public async Task<IReadOnlyList<McpServerStatus>> PrepareAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var configs = LoadServerConfigs(workspace);
            await _connections.SynchronizeAsync(workspace, configs, ct).ConfigureAwait(false);
            var binding = McpConnectionPool.Scope(workspace) + string.Concat(configs.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => "\0" + x.Key + ":" + _connections.Fingerprint(x.Key, x.Value)));
            if (_binding != binding) { lock (_state) _loaded.Clear(); _binding = binding; }
            _workspace = workspace;
            _configs = configs;
            RefreshRegistrations();
            return Status;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        await PrepareAsync(workspace, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _connections.InvalidateAsync(workspace, ct).ConfigureAwait(false);
            lock (_state) _loaded.Clear();
            var statuses = new List<McpServerStatus>();
            foreach (var pair in _configs)
            {
                if (!pair.Value.Enabled) { statuses.Add(new(pair.Key, pair.Value.Transport, false, 0, "disabled")); continue; }
                try
                {
                    var catalog = await _connections.GetAsync(workspace, pair.Key, pair.Value, ct).ConfigureAwait(false);
                    statuses.Add(new(pair.Key, pair.Value.Transport, true, catalog.Tools.Count, null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { statuses.Add(new(pair.Key, pair.Value.Transport, false, 0, DescribeConnectionError(ex))); }
            }
            RefreshRegistrations();
            lock (_state) _status = statuses;
            return Status;
        }
        finally { _gate.Release(); }
    }

    internal async Task<ToolResult> SearchAsync(string? query, string? server, int limit,
        ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId)) return ToolResult.Fail("Tool discovery requires a conversation.");
        await PrepareAsync(context.Workspace, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var workspace = _workspace!;
            var enabled = _configs.Where(x => x.Value.Enabled).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(server))
            {
                enabled = enabled.Where(x => x.Key.Equals(server, StringComparison.OrdinalIgnoreCase)).ToList();
                if (enabled.Count == 0) return ToolResult.Fail($"Enabled MCP server not found: {server}");
            }
            else if (string.IsNullOrWhiteSpace(query))
            {
                return ToolResult.Ok(new JsonObject
                {
                    ["servers"] = new JsonArray(enabled.Select(x => (JsonNode)new JsonObject
                    {
                        ["name"] = x.Key, ["transport"] = x.Value.Transport,
                        ["connected"] = _connections.Cached(workspace, x.Key, x.Value) is not null,
                    }).ToArray()),
                    ["hint"] = "Supply query and/or server to discover tools and load their schemas. Discovery does not execute the tools.",
                }.ToJsonString(), "Listed MCP servers", mutated: false);
            }
            else
            {
                // Prefer an explicitly named server or a matching cached tool catalog.
                // An unknown query inspects remaining enabled catalogs, within a bounded batch.
                var named = enabled.Where(x => Matches(x.Key, query)).ToList();
                if (named.Count > 0) enabled = named;
            }

            var errors = new JsonArray();
            var inspected = 0;
            foreach (var pair in enabled.Take(8))
            {
                if (IsRemote(pair.Value) && !context.NetworkEnabled)
                {
                    errors.Add((JsonNode)new JsonObject { ["server"] = pair.Key, ["error"] = "Network access is disabled for this conversation." });
                    continue;
                }
                inspected++;
                try { await _connections.GetAsync(workspace, pair.Key, pair.Value, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add((JsonNode)new JsonObject { ["server"] = pair.Key, ["error"] = DescribeConnectionError(ex) }); }
            }
            RefreshRegistrations();
            var mode = AgentModeExtensions.Parse(context.Workspace.Config?.Mode ?? context.Agent.Mode);
            var matches = _tools.All.OfType<McpToolAdapter>()
                .Where(tool => enabled.Any(x => x.Key.Equals(tool.ServerName, StringComparison.OrdinalIgnoreCase)))
                .Where(tool => !mode.IsReadOnly() || !tool.Mutating)
                .Where(tool => context.NetworkEnabled || !tool.RequiresNetwork)
                .Where(tool => context.Workspace.Config?.DisabledTools is not { Count: > 0 } disabled || !Agent.IsToolDisabled(tool, disabled))
                .Select(tool => (Tool: tool, Score: Score(tool, query)))
                .Where(x => string.IsNullOrWhiteSpace(query) || x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Tool.Name, StringComparer.Ordinal)
                .Take(Math.Clamp(limit, 1, 12)).ToList();
            var results = new JsonArray();
            foreach (var (tool, _) in matches)
            {
                lock (_state)
                {
                    if (!_loaded.TryGetValue(context.SessionId, out var names)) _loaded[context.SessionId] = names = new(StringComparer.OrdinalIgnoreCase);
                    names.Add(tool.Name);
                }
                results.Add((JsonNode)new JsonObject { ["name"] = tool.Name, ["server"] = tool.ServerName,
                    ["description"] = tool.Description, ["parameters"] = tool.ParameterSchema.DeepClone() });
            }
            return ToolResult.Ok(new JsonObject { ["tools"] = results, ["errors"] = errors,
                ["hasMoreServers"] = enabled.Count > 8, ["inspectedServers"] = inspected,
                ["hint"] = "Returned tools are loaded for this turn. Call them by name; normal permissions and approvals still apply." }.ToJsonString(),
                $"Loaded {results.Count} MCP tools", mutated: false);
        }
        finally { _gate.Release(); }
    }

    private void RefreshRegistrations()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        var statuses = new List<McpServerStatus>();
        var reservedNames = _tools.All.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_workspace is null) return;
        foreach (var pair in _configs.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var catalog = pair.Value.Enabled ? _connections.Cached(_workspace, pair.Key, pair.Value) : null;
            statuses.Add(new(pair.Key, pair.Value.Transport, catalog is not null, catalog?.Tools.Count ?? 0, pair.Value.Enabled ? null : "disabled"));
            if (catalog is null) continue;
            foreach (var remote in catalog.Tools.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var name = McpToolAdapter.BuildName(pair.Key, remote.Name);
                var suffix = 2;
                while (!reservedNames.Add(name))
                {
                    var suffixText = $"_{suffix++}";
                    var baseName = McpToolAdapter.BuildName(pair.Key, remote.Name);
                    name = baseName[..Math.Min(baseName.Length, 64 - suffixText.Length)] + suffixText;
                }
                var serverName = pair.Key;
                var tool = new McpToolAdapter(catalog.Client, remote, name,
                    invoke: (args, token) => CallAsync(serverName, remote.Name, catalog, args, token),
                    loaded: session => IsLoaded(session, name), requiresNetwork: IsRemote(pair.Value));
                if (_tools.TryRegister(tool) is { } registration) _registrations.Add(registration);
            }
            foreach (var prompt in catalog.Prompts)
            {
                var serverName = pair.Key;
                var promptName = prompt.Name;
                _registrations.Add(_prompts.Register(new PromptContribution($"mcp:{serverName}:{promptName}", PromptSlot.Tool,
                    async (context, token) => await PromptAsync(serverName, promptName, catalog, context, token).ConfigureAwait(false))));
            }
        }
        lock (_state) _status = statuses;
    }

    private async Task<McpCallResult> CallAsync(string name, string tool, McpCatalog expected, JsonObject arguments, CancellationToken ct)
    {
        var workspace = _workspace ?? throw new McpException("MCP workspace is unavailable.");
        var configs = LoadServerConfigs(workspace);
        if (!configs.TryGetValue(name, out var config) || !config.Enabled) throw new McpException($"MCP server '{name}' is disabled or removed.");
        if (_connections.Fingerprint(name, config) != expected.Fingerprint)
            throw new McpException($"MCP server '{name}' changed configuration or authentication; use tool_search again.");
        if (!ReferenceEquals(_connections.Cached(workspace, name, config), expected))
            throw new McpException($"MCP server '{name}' disconnected or reloaded; use tool_search again.");
        return await _connections.UseAsync(workspace, name, config, async (catalog, token) =>
        {
            if (!ReferenceEquals(catalog, expected)) throw new McpException($"MCP server '{name}' reloaded; use tool_search again.");
            if (!catalog.Tools.Any(x => x.Name == tool)) throw new McpException($"MCP tool '{tool}' is no longer available; search again.");
            // Never retry tools/call automatically: a lost response can follow a completed mutation.
            return await catalog.Client.CallToolResultAsync(tool, arguments, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<string?> PromptAsync(string name, string prompt, McpCatalog expected, PromptRenderContext context, CancellationToken ct)
    {
        var workspace = _workspace;
        if (workspace is null || !LoadServerConfigs(workspace).TryGetValue(name, out var config) || !config.Enabled) return null;
        if (_connections.Fingerprint(name, config) != expected.Fingerprint
            || !ReferenceEquals(_connections.Cached(workspace, name, config), expected)) return null;
        if (IsRemote(config) && !NetworkEnabled(context)) return null;
        // A reused connection must not fetch every server prompt on an unrelated turn.
        // The composed tool list already reflects discovery, role and conversation permissions.
        var visible = VisibleTools(context);
        if (!_tools.All.OfType<McpToolAdapter>().Any(tool => tool.ServerName.Equals(name, StringComparison.OrdinalIgnoreCase)
                && visible.Contains(tool.Name))) return null;
        return await _connections.UseAsync(workspace, name, config,
            (catalog, token) => ReferenceEquals(catalog, expected) ? catalog.Client.GetPromptAsync(prompt, token)
                : Task.FromResult<string?>(null), ct).ConfigureAwait(false);
    }

    private string? CatalogInstruction(PromptRenderContext context)
    {
        if (!ReferenceEquals(_tools.Resolve(_search.Name), _search) || !VisibleTools(context).Contains(_search.Name)) return null;
        var networkEnabled = NetworkEnabled(context);
        var names = _configs.Where(x => x.Value.Enabled && (networkEnabled || !IsRemote(x.Value)))
            .Select(x => x.Key).Order(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
        return names.Count == 0 ? null : "MCP servers available for on-demand tool discovery: "
            + JsonSerializer.Serialize(names, SeekClawJsonContext.Default.ListString)
            + ". Use tool_search with a server and/or query to load relevant tool schemas before calling them. "
            + "Tool discovery does not execute the remote tools. Returned tools remain subject to permissions and approvals.";
    }

    private static bool NetworkEnabled(PromptRenderContext context) => !context.Variables.TryGetValue("network", out var value)
        || !value.Equals("disabled", StringComparison.OrdinalIgnoreCase);
    private static HashSet<string> VisibleTools(PromptRenderContext context) => context.Variables.TryGetValue("tool", out var tools)
        ? tools.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new(StringComparer.OrdinalIgnoreCase);

    private static bool IsRemote(McpServerConfig config) => !config.Transport.Equals("stdio", StringComparison.OrdinalIgnoreCase);
    private static bool Matches(string text, string? query) => !string.IsNullOrWhiteSpace(query) && text.Contains(query, StringComparison.OrdinalIgnoreCase);
    private static int Score(McpToolAdapter tool, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        var score = Matches(tool.Name, query) ? 20 : 0;
        if (Matches(tool.ServerName, query)) score += 10;
        foreach (var word in query.Split([' ', '\t', ',', '/', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Matches(tool.Name, word)) score += 5;
            if (Matches(tool.Description, word)) score += 1;
        }
        return score;
    }

    internal static string DescribeConnectionError(Exception error) => error switch
    {
        Win32Exception win32 => $"无法启动 MCP 服务器进程：{win32.Message}",
        OperationCanceledException or TimeoutException => "连接超时",
        HttpRequestException http => $"连接失败：{http.Message}",
        _ => error.Message,
    };

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var registration in _registrations) registration.Dispose();
            _registrations.Clear();
            _searchRegistration?.Dispose();
            _catalogRegistration.Dispose();
            lock (_state) { _status.Clear(); _loaded.Clear(); }
            if (_ownsConnections) await _connections.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Remote tool invocation stays in the normal Agent permission and approval pipeline.</summary>
public sealed class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpTool _tool;
    private readonly bool _isMutating;
    private readonly Func<JsonObject, CancellationToken, Task<McpCallResult>>? _invoke;
    private readonly Func<string, bool>? _loaded;

    public McpToolAdapter(McpClient client, McpTool tool, bool isMutating = false)
        : this(client, tool, BuildName(client.ServerName, tool.Name), isMutating) { }

    public McpToolAdapter(McpClient client, McpTool tool, string name, bool isMutating = false)
        : this(client, tool, name, isMutating, null, null, false) { }

    internal McpToolAdapter(McpClient client, McpTool tool, string name, bool isMutating = false,
        Func<JsonObject, CancellationToken, Task<McpCallResult>>? invoke = null,
        Func<string, bool>? loaded = null, bool requiresNetwork = false)
    {
        _client = client; _tool = tool; Name = name; _isMutating = isMutating;
        _invoke = invoke; _loaded = loaded; RequiresNetwork = requiresNetwork;
    }

    public string Name { get; }
    public string ServerName => _client.ServerName;
    public string Description => _tool.Description ?? $"MCP tool {_tool.Name} from server {_client.ServerName}.";
    public JsonObject ParameterSchema => (JsonObject)_tool.InputSchema.DeepClone();
    public bool Mutating => _isMutating || InferMutating(_tool.Name);
    public bool RequiresWorkspace => false;
    public bool RequiresNetwork { get; }
    public string StatusLabel => $"Calling {_client.ServerName}";
    public bool IsLoaded(string sessionId) => _loaded?.Invoke(sessionId) ?? true;

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        if (!IsLoaded(context.SessionId)) return ToolResult.Fail("MCP tool schema is not loaded for this turn. Use tool_search first.");
        if (RequiresNetwork && !context.NetworkEnabled) return ToolResult.Fail("Network access is disabled for this conversation.");
        try
        {
            var result = _invoke is null ? await _client.CallToolResultAsync(_tool.Name, arguments, ct).ConfigureAwait(false)
                : await _invoke(arguments, ct).ConfigureAwait(false);
            var text = result.Text;
            return new ToolResult { Success = result.Success,
                Output = context.Truncate(text.Length == 0 ? "(no output)" : text, "MCP output"),
                Summary = $"{_tool.Name} via {_client.ServerName}", Content = result.Content,
                StructuredContent = result.StructuredContent, Metadata = result.Metadata, Images = result.Images() };
        }
        catch (McpException ex) { return ToolResult.Fail(ex.Message); }
    }

    public static string BuildName(string serverName, string toolName)
    {
        var full = $"mcp__{SanitizeSegment(serverName)}__{SanitizeSegment(toolName)}";
        return full.Length > 64 ? full[..64] : full;
    }

    internal static string SanitizeSegment(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var ch in value) result.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return result.Length == 0 ? "tool" : result.ToString();
    }

    private static bool InferMutating(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("write") || n.Contains("edit") || n.Contains("create") || n.Contains("delete")
            || n.Contains("update") || n.Contains("modify") || n.Contains("patch") || n.Contains("apply")
            || n.Contains("exec") || n.Contains("run") || n.Contains("git_commit") || n.Contains("git_push");
    }
}
