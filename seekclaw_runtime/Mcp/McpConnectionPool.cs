using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Mcp;

/// <summary>
/// Process-owned MCP connections. Runtime managers only own local registrations, so
/// disposing a turn never tears down a connection used by another turn or child.
/// </summary>
public sealed class McpConnectionPool : IAsyncDisposable
{
    private const int MaximumConnections = 64;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly McpAuthentication? _authentication;
    private readonly Func<string, McpServerConfig, IMcpTransport>? _transportFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    public event Action? StatusChanged;

    public McpConnectionPool(McpAuthentication? authentication = null,
        Func<string, McpServerConfig, IMcpTransport>? transportFactory = null)
    {
        _authentication = authentication;
        _transportFactory = transportFactory;
        if (_authentication is not null) _authentication.CredentialsChanged += InvalidateAuthenticationAsync;
    }

    internal void NotifyStatusChanged() => StatusChanged?.Invoke();

    internal IReadOnlyList<McpServerStatus> Status(WorkspaceInfo workspace, IReadOnlyDictionary<string, McpServerConfig> configs)
    {
        var snapshots = new List<McpServerStatus>();
        lock (_gate)
        {
            foreach (var pair in configs)
            {
                var matches = _connections.TryGetValue(Key(workspace, pair.Key), out var connection)
                    && connection.Fingerprint == Fingerprint(pair.Key, pair.Value);
                snapshots.Add(!pair.Value.Enabled ? new(pair.Key, pair.Value.Transport, false, 0, "disabled")
                    : matches ? connection!.Status(pair.Key, pair.Value.Transport)
                    : new(pair.Key, pair.Value.Transport, false, 0, null));
            }
        }
        return snapshots;
    }

    internal Task InvalidateAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = Scope(workspace) + "\0";
        return RetireAsync(connection => connection.Key.StartsWith(scope, StringComparison.Ordinal));
    }

    private ValueTask InvalidateAuthenticationAsync(string key) => new(RetireAsync(connection => connection.AuthenticationKey == key));

    private async Task RetireAsync(Func<Connection, bool> predicate)
    {
        List<Connection> retired;
        lock (_gate)
        {
            if (_disposed) return;
            retired = _connections.Values.Where(predicate).ToList();
            foreach (var connection in retired) _connections.Remove(connection.Key);
        }
        foreach (var connection in retired) await connection.DisposeAsync().ConfigureAwait(false);
        if (retired.Count > 0) NotifyStatusChanged();
    }

    internal static string Scope(WorkspaceInfo workspace)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.ConfigurationRoot ?? workspace.Root));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    internal string Fingerprint(string name, McpServerConfig config)
    {
        // Only the digest is retained as identity. Environment-backed credentials are
        // included without exposing their values in tool names, status or diagnostics.
        var json = JsonSerializer.SerializeToNode(config, SeekClawJsonContext.Default.McpServerConfig)!.AsObject();
        var environment = new System.Text.Json.Nodes.JsonObject();
        foreach (var variable in (config.EnvHeaders?.Values.AsEnumerable() ?? [])
                     .Concat(new[] { config.BearerTokenEnv, config.OAuth?.ClientSecretEnv }.OfType<string>())
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            environment[variable] = Environment.GetEnvironmentVariable(variable);
        json["credentialEnvironment"] = environment;
        json["authenticationRevision"] = _authentication?.Revision(name, config) ?? 0;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    internal McpCatalog? Cached(WorkspaceInfo workspace, string name, McpServerConfig config)
    {
        var key = Key(workspace, name);
        var fingerprint = Fingerprint(name, config);
        lock (_gate)
        {
            if (_connections.TryGetValue(key, out var connection) && connection.Fingerprint == fingerprint)
                return connection.Cached;
            return null;
        }
    }

    /// <summary>Revokes removed, disabled and reconfigured servers without opening new ones.</summary>
    internal async Task SynchronizeAsync(WorkspaceInfo workspace,
        IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = Scope(workspace) + "\0";
        var expected = configs.Where(x => x.Value.Enabled).ToDictionary(
            x => Key(workspace, x.Key), x => Fingerprint(x.Key, x.Value), StringComparer.Ordinal);
        List<Connection> retired;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            retired = _connections.Where(x => x.Key.StartsWith(scope, StringComparison.Ordinal)
                    && (!expected.TryGetValue(x.Key, out var fingerprint) || fingerprint != x.Value.Fingerprint)
                || x.Value.IsIdle(IdleLifetime)).Select(x => x.Value).ToList();
            foreach (var connection in retired) _connections.Remove(connection.Key);
        }
        foreach (var connection in retired) await connection.DisposeAsync().ConfigureAwait(false);
        if (retired.Count > 0) NotifyStatusChanged();
    }

    internal Task<McpCatalog> GetAsync(WorkspaceInfo workspace, string name, McpServerConfig config,
        CancellationToken ct) => UseAsync(workspace, name, config, static (catalog, _) => Task.FromResult(catalog), ct);

    internal async Task<T> UseAsync<T>(WorkspaceInfo workspace, string name, McpServerConfig config,
        Func<McpCatalog, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        if (!config.Enabled) throw new McpException($"MCP server '{name}' is disabled.");
        var key = Key(workspace, name);
        var fingerprint = Fingerprint(name, config);
        List<Connection> retired = [];
        Connection connection;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections.TryGetValue(key, out var existing) && existing.Fingerprint != fingerprint)
            {
                _connections.Remove(key);
                retired.Add(existing);
            }
            if (!_connections.TryGetValue(key, out connection!))
            {
                if (_connections.Count >= MaximumConnections)
                {
                    var oldest = _connections.Values.Where(x => !x.Busy).MinBy(x => x.LastUsed);
                    if (oldest is null) throw new McpException("MCP connection limit reached; all connections are busy.");
                    _connections.Remove(oldest.Key);
                    retired.Add(oldest);
                }
                var snapshot = JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(config, SeekClawJsonContext.Default.McpServerConfig),
                    SeekClawJsonContext.Default.McpServerConfig)!;
                connection = new Connection(key, fingerprint, name, snapshot, this, _lifetime.Token);
                _connections.Add(key, connection);
            }
            // Acquire under the pool lock so a caller waiting for its bootstrap or
            // preparing a request cannot be mistaken for an idle eviction candidate.
            connection.Acquire();
        }
        try
        {
            foreach (var old in retired) await old.DisposeAsync().ConfigureAwait(false);
            var catalog = await connection.GetAsync(ct).ConfigureAwait(false);
            return await action(catalog, ct).ConfigureAwait(false);
        }
        finally { connection.Release(); }
    }

    private static string Key(WorkspaceInfo workspace, string name) => Scope(workspace) + "\0" + name.ToUpperInvariant();

    private IMcpTransport CreateTransport(string name, McpServerConfig config)
    {
        if (_transportFactory is not null) return _transportFactory(name, config);
        return config.Transport.ToLowerInvariant() switch
        {
            "stdio" when !string.IsNullOrWhiteSpace(config.Command) =>
                new StdioMcpTransport(config.Command, config.Args, config.Env),
            "sse" or "http" or "streamable-http" or "streamable_http" when !string.IsNullOrWhiteSpace(config.Url) =>
                new HttpMcpTransport(config.Url, headers: _authentication is null ? null
                    : token => _authentication.HeadersAsync(name, config, token)),
            "websocket" => throw new McpException($"Transport '{config.Transport}' is reserved but not implemented yet."),
            _ => throw new McpException($"Server '{name}': invalid transport/command/url combination."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        List<Connection> connections;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_authentication is not null) _authentication.CredentialsChanged -= InvalidateAuthenticationAsync;
            _lifetime.Cancel();
            connections = _connections.Values.ToList();
            _connections.Clear();
        }
        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class Connection(string key, string fingerprint, string name, McpServerConfig config,
        McpConnectionPool owner, CancellationToken lifetime) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private Task<McpCatalog>? _opening;
        private McpCatalog? _catalog;
        private int _users;
        private bool _disposed;
        private string? _error;
        public string Key { get; } = key;
        public string Fingerprint { get; } = fingerprint;
        public string AuthenticationKey { get; } = McpAuthentication.Key(name, config);
        public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
        public bool Busy { get { lock (_gate) return _users > 0 || _opening is { IsCompleted: false } || _catalog?.Client.HasPendingRequests == true; } }
        public McpCatalog? Cached { get { lock (_gate) return _catalog?.Client.IsConnected == true ? _catalog : null; } }
        public void Acquire() { lock (_gate) { _users++; LastUsed = DateTimeOffset.UtcNow; } }
        public void Release() { lock (_gate) { _users--; LastUsed = DateTimeOffset.UtcNow; } }
        public bool IsIdle(TimeSpan age) => !Busy && DateTimeOffset.UtcNow - LastUsed > age;
        public McpServerStatus Status(string serverName, string transport)
        {
            lock (_gate)
            {
                var connected = _catalog?.Client.IsConnected == true;
                return new(serverName, transport, connected, connected ? _catalog!.Tools.Count : 0,
                    _error ?? (_catalog is not null && !connected ? "disconnected" : null))
                    { Connecting = !connected && _error is null && Busy };
            }
        }

        public Task<McpCatalog> GetAsync(CancellationToken ct)
        {
            Task<McpCatalog> opening;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_catalog?.Client.IsConnected == true) return Task.FromResult(_catalog);
                // Start outside this lock. Even a fully synchronous transport can raise
                // status callbacks that inspect the pool (whose lock precedes this one).
                if (_opening is null || _opening.IsCompleted) _opening = Task.Run(OpenAsync);
                opening = _opening;
            }
            // One cancelled caller cannot cancel a shared bootstrap used by siblings.
            return opening.WaitAsync(ct);
        }

        private async Task<McpCatalog> OpenAsync()
        {
            McpCatalog? previous;
            lock (_gate) { previous = _catalog; _catalog = null; _error = null; }
            if (previous is not null) await previous.Client.DisposeAsync().ConfigureAwait(false);
            McpClient? client = null;
            try
            {
                client = new McpClient(name, owner.CreateTransport(name, config));
                client.Disconnected += owner.NotifyStatusChanged;
                using var initialize = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                initialize.CancelAfter(TimeSpan.FromSeconds(30));
                try { await client.InitializeAsync(initialize.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                { throw new McpException($"Server '{name}' timed out during initialization."); }
                var tools = await client.ListToolsAsync(_lifetime.Token).ConfigureAwait(false);
                var prompts = await client.ListPromptsAsync(_lifetime.Token).ConfigureAwait(false);
                var catalog = new McpCatalog(client, tools, prompts, Fingerprint);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _catalog = catalog;
                }
                owner.NotifyStatusChanged();
                return catalog;
            }
            catch (Exception error)
            {
                lock (_gate) _error = McpManager.DescribeConnectionError(error);
                if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                owner.NotifyStatusChanged();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task<McpCatalog>? opening;
            McpCatalog? catalog;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                opening = _opening;
                catalog = _catalog;
                _catalog = null;
            }
            if (opening is not null)
            {
                try { catalog = await opening.ConfigureAwait(false); } catch (Exception) { }
            }
            if (catalog is not null) await catalog.Client.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }
    }
}

internal sealed record McpCatalog(McpClient Client, IReadOnlyList<McpTool> Tools,
    IReadOnlyList<McpPrompt> Prompts, string Fingerprint);
