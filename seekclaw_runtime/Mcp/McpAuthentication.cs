using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.IO;
using SeekClaw.Runtime.Execution;

namespace SeekClaw.Runtime.Mcp;

/// <summary>Remote MCP credentials, OAuth discovery, PKCE authorization code flow and refresh.</summary>
public sealed class McpAuthentication : IDisposable, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly McpCredentialStore _credentials;
    private readonly SessionTurnCoordinator _credentialLocks;
    private readonly ConcurrentDictionary<string, PendingLogin> _pending = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refresh = new();
    private readonly ConcurrentDictionary<string, LoginAttempt> _logins = new();
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<string, long> _logoutVersions = new();
    private readonly CancellationTokenSource _lifetime = new();
    public McpAuthentication(McpCredentialStore credentials, HttpClient? http = null)
    {
        _credentials = credentials; _ownsHttp = http is null;
        _credentialLocks = new SessionTurnCoordinator(credentials.LockDirectory);
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    private static string Key(string name, McpServerConfig server) => name + "|" + server.Url;

    public async Task<IReadOnlyDictionary<string, string>> HeadersAsync(string name, McpServerConfig server, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in server.Headers ?? []) headers[pair.Key] = pair.Value;
        foreach (var pair in server.EnvHeaders ?? []) headers[pair.Key] = Environment.GetEnvironmentVariable(pair.Value)
            ?? throw new McpException($"MCP credential environment variable is missing: {pair.Value}");
        if (!string.IsNullOrWhiteSpace(server.BearerTokenEnv))
            headers["Authorization"] = "Bearer " + (Environment.GetEnvironmentVariable(server.BearerTokenEnv)
                ?? throw new McpException($"MCP token environment variable is missing: {server.BearerTokenEnv}"));
        if (!headers.ContainsKey("Authorization"))
        {
            var key = Key(name, server);
            var gate = _refresh.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var lease = await _credentialLocks.AcquireAsync(key, ct).ConfigureAwait(false);
                var token = _credentials.Get(key);
                if (token is not null)
                {
                    if (DateTimeOffset.TryParse(token["expiresAt"]?.GetValue<string>(), out var expiration)
                        && expiration <= DateTimeOffset.UtcNow.AddSeconds(30))
                    {
                        if (token["refresh_token"]?.GetValue<string>() is not { Length: > 0 } refresh)
                            throw new McpException($"MCP '{name}' requires login: its access token has expired.");
                        token = await ExchangeAsync(token, server, new()
                        { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, ct).ConfigureAwait(false);
                        _credentials.Put(key, token);
                    }
                    headers["Authorization"] = "Bearer " + token["access_token"]!.GetValue<string>();
                }
            }
            finally { gate.Release(); }
        }
        return headers;
    }

    public async Task<JsonObject> BeginAsync(string name, McpServerConfig server, string redirectUri, CancellationToken ct)
    {
        ValidateEndpoint(server.Url ?? throw new McpException("Remote MCP URL is required."));
        var redirect = new Uri(redirectUri);
        if (redirect.Scheme != "http" || !redirect.IsLoopback || redirect.UserInfo.Length > 0 || redirect.Fragment.Length > 0 || redirect.Query.Length > 0)
            throw new McpException("OAuth redirect must be an HTTP loopback URL without a query or fragment.");
        var key = Key(name, server);
        long logoutVersion;
        lock (_pendingGate) logoutVersion = _logoutVersions.GetValueOrDefault(key);
        var settings = server.OAuth ?? new McpOAuthConfig();
        var metadata = await DiscoverAsync(server, ct).ConfigureAwait(false);
        var authorization = settings.AuthorizationEndpoint ?? metadata["authorization_endpoint"]?.GetValue<string>()
            ?? throw new McpException("OAuth authorization endpoint was not discovered.");
        var endpoint = settings.TokenEndpoint ?? metadata["token_endpoint"]?.GetValue<string>()
            ?? throw new McpException("OAuth token endpoint was not discovered.");
        ValidateEndpoint(authorization); ValidateEndpoint(endpoint);
        var clientId = settings.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            var registration = metadata["registration_endpoint"]?.GetValue<string>()
                ?? throw new McpException("Set oauth.clientId; this authorization server does not support dynamic registration.");
            ValidateEndpoint(registration);
            using var request = new HttpRequestMessage(HttpMethod.Post, registration) { Content = new StringContent(new JsonObject
            {
                ["client_name"] = "SeekClaw", ["redirect_uris"] = new JsonArray(redirectUri),
                ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                ["response_types"] = new JsonArray("code"), ["token_endpoint_auth_method"] = "none",
            }.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var registered = await ReadSuccessAsync(response, ct).ConfigureAwait(false);
            clientId = registered["client_id"]?.GetValue<string>() ?? throw new McpException("OAuth registration did not return a client ID.");
        }
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = redirectUri,
            ["state"] = state, ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["resource"] = server.Url ?? "",
        };
        if (!string.IsNullOrWhiteSpace(settings.Scope)) fields["scope"] = settings.Scope;
        var query = string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        lock (_pendingGate)
        {
            if (_logoutVersions.GetValueOrDefault(key) != logoutVersion)
                throw new McpException("OAuth login was cancelled by logout.");
            foreach (var item in _pending.Where(x => x.Value.Expires < DateTimeOffset.UtcNow)) _pending.TryRemove(item.Key, out _);
            if (_pending.Count >= 16) throw new McpException("Too many pending OAuth logins.");
            _pending[state] = new PendingLogin(key, server, clientId, endpoint, redirectUri, verifier,
                DateTimeOffset.UtcNow.AddMinutes(10));
        }
        return new JsonObject { ["state"] = state, ["authorizationUrl"] = authorization + (authorization.Contains('?') ? "&" : "?") + query };
    }

    public async Task CompleteAsync(string state, string callback, CancellationToken ct)
    {
        if (!_pending.TryGetValue(state, out var login) || login.Expires < DateTimeOffset.UtcNow)
            throw new McpException("OAuth login expired or state is invalid.");
        var uri = new Uri(callback);
        var expected = new Uri(login.Redirect);
        if (uri.GetLeftPart(UriPartial.Path) != expected.GetLeftPart(UriPartial.Path)) throw new McpException("OAuth callback does not match the redirect URI.");
        var values = ParseQuery(uri.Query);
        if (!values.TryGetValue("state", out var returnedState) || returnedState != state)
            throw new McpException("OAuth state mismatch.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new McpException("Authorization was denied or no code was returned.");
        var gate = _refresh.GetOrAdd(login.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await _credentialLocks.AcquireAsync(login.Key, ct).ConfigureAwait(false);
            if (!_pending.TryRemove(state, out _)) throw new McpException("OAuth callback was already consumed or cancelled.");
            var token = await ExchangeAsync(new JsonObject { ["clientId"] = login.ClientId, ["tokenEndpoint"] = login.Endpoint }, login.Server,
                new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = login.Redirect,
                    ["code_verifier"] = login.Verifier }, ct).ConfigureAwait(false);
            _credentials.Put(login.Key, token);
        }
        finally { gate.Release(); }
    }

    public async Task LogoutAsync(string name, McpServerConfig server, CancellationToken ct = default)
    {
        var key = Key(name, server);
        var gate = _refresh.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await _credentialLocks.AcquireAsync(key, ct).ConfigureAwait(false);
            lock (_pendingGate)
            {
                _logoutVersions[key] = _logoutVersions.GetValueOrDefault(key) + 1;
                foreach (var item in _pending.Where(x => x.Value.Key == key)) _pending.TryRemove(item.Key, out _);
                foreach (var attempt in _logins.Values.Where(x => x.Key == key && !x.Task.IsCompleted)) attempt.Cancellation.Cancel();
            }
            _credentials.Remove(key);
        }
        finally { gate.Release(); }
    }

    /// <summary>Starts a bounded loopback callback listener; no credentials cross the admin protocol.</summary>
    public async Task<JsonObject> BeginLoopbackAsync(string name, McpServerConfig server, CancellationToken ct)
    {
        foreach (var finished in _logins.Where(x => x.Value.Task.IsCompleted
                     && DateTimeOffset.UtcNow - x.Value.CreatedAt > TimeSpan.FromMinutes(30)))
            if (_logins.TryRemove(finished.Key, out var removed)) removed.Cancellation.Dispose();
        if (_logins.Count >= 64) throw new McpException("OAuth login history is full; retry after older attempts expire.");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(4);
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback";
            var login = await BeginAsync(name, server, redirect, ct).ConfigureAwait(false);
            var state = login["state"]!.GetValue<string>();
            lock (_pendingGate)
            {
                if (!_pending.ContainsKey(state)) throw new McpException("OAuth login was cancelled.");
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _logins[state] = new LoginAttempt(Key(name, server), cancellation,
                    ReceiveCallbackAsync(listener, redirect, state, cancellation.Token), DateTimeOffset.UtcNow);
            }
            return login;
        }
        catch { listener.Stop(); throw; }
    }

    public JsonObject LoginStatus(string state)
    {
        if (!_logins.TryGetValue(state, out var attempt)) throw new McpException("OAuth login was not found.");
        var task = attempt.Task;
        return new JsonObject { ["status"] = task.IsFaulted ? "failed" : task.IsCanceled ? "cancelled" : task.IsCompleted ? "completed" : "pending",
            ["error"] = task.Exception?.GetBaseException().Message };
    }

    public Task WaitForLoginAsync(string state, CancellationToken ct) => _logins.TryGetValue(state, out var attempt)
        ? attempt.Task.WaitAsync(ct) : throw new McpException("OAuth login was not found.");

    private async Task ReceiveCallbackAsync(TcpListener listener, string redirect, string state, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                Exception? failure = null;
                var valid = false;
                try
                {
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var lines = new BoundedLineReader(reader, 8192);
                    var first = await lines.ReadLineAsync(requestDeadline.Token).ConfigureAwait(false);
                    var size = first?.Length ?? 0;
                    string? line;
                    while ((line = await lines.ReadLineAsync(requestDeadline.Token).ConfigureAwait(false)) is { Length: > 0 })
                    {
                        size += line.Length + 2;
                        if (size > 8192) throw new IOException("Callback headers exceed the limit.");
                    }
                    var firstLine = first?.Split(' ') ?? [];
                    if (line is not null && firstLine.Length == 3 && firstLine[0] == "GET"
                        && firstLine[1].StartsWith("/callback?", StringComparison.Ordinal))
                    {
                        try
                        {
                            var values = ParseQuery(new Uri(redirect + firstLine[1]["/callback".Length..]).Query);
                            valid = values.TryGetValue("state", out var returned) && returned == state;
                        }
                        catch (Exception ex) when (ex is McpException or UriFormatException) { }
                    }
                    var body = "Invalid callback.";
                    if (valid)
                    {
                        try
                        {
                            await CompleteAsync(state, redirect + firstLine[1]["/callback".Length..], deadline.Token).ConfigureAwait(false);
                            body = "SeekClaw login completed. You can close this window.";
                        }
                        catch (Exception ex) { failure = ex; body = "SeekClaw login failed. Return to the application for details."; }
                    }
                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 {(valid && failure is null ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n{body}");
                    await stream.WriteAsync(response, requestDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                catch (IOException) { }
                if (failure is not null) throw failure;
                if (valid) return;
            }
        }
        finally { listener.Stop(); _pending.TryRemove(state, out _); }
    }

    private async Task<JsonObject> ExchangeAsync(JsonObject previous, McpServerConfig server, Dictionary<string, string> form, CancellationToken ct)
    {
        var endpoint = previous["tokenEndpoint"]!.GetValue<string>();
        ValidateEndpoint(endpoint);
        form["client_id"] = previous["clientId"]!.GetValue<string>();
        form["resource"] = server.Url ?? "";
        if (server.OAuth?.ClientSecretEnv is { Length: > 0 } variable)
            form["client_secret"] = Environment.GetEnvironmentVariable(variable) ?? throw new McpException("OAuth client secret environment variable is missing.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var token = await ReadSuccessAsync(response, ct).ConfigureAwait(false);
        if (token["access_token"]?.GetValue<string>() is not { Length: > 0 }) throw new McpException("OAuth response did not contain an access token.");
        if (token["token_type"]?.GetValue<string>() is { } type && !type.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new McpException("Only OAuth Bearer access tokens are supported.");
        token["clientId"] = previous["clientId"]!.DeepClone(); token["tokenEndpoint"] = endpoint;
        if (token["refresh_token"] is null && previous["refresh_token"] is { } refresh) token["refresh_token"] = refresh.DeepClone();
        if (token["expires_in"] is { } expires) token["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(expires.GetValue<double>()).ToString("O");
        return token;
    }

    private async Task<JsonObject> DiscoverAsync(McpServerConfig server, CancellationToken ct)
    {
        if (server.OAuth?.AuthorizationEndpoint is not null && server.OAuth.TokenEndpoint is not null) return new();
        var resource = new Uri(server.Url ?? throw new McpException("Remote MCP URL is required."));
        ValidateEndpoint(resource.AbsoluteUri);
        var authority = resource.GetLeftPart(UriPartial.Authority);
        var protectedMetadata = await TryGetAsync(authority + "/.well-known/oauth-protected-resource" + resource.AbsolutePath.TrimEnd('/'), ct).ConfigureAwait(false)
            ?? await TryGetAsync(authority + "/.well-known/oauth-protected-resource", ct).ConfigureAwait(false);
        var issuer = (protectedMetadata?["authorization_servers"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? authority;
        ValidateEndpoint(issuer);
        var issuerUri = new Uri(issuer);
        var metadataUrl = issuerUri.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-authorization-server" + issuerUri.AbsolutePath.TrimEnd('/');
        var metadata = await TryGetAsync(metadataUrl, ct).ConfigureAwait(false)
            ?? await TryGetAsync(issuer.TrimEnd('/') + "/.well-known/openid-configuration", ct).ConfigureAwait(false)
            ?? throw new McpException("OAuth server metadata was not found; configure explicit OAuth endpoints.");
        if (metadata["issuer"]?.GetValue<string>() is { } declared
            && !string.Equals(declared.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.Ordinal))
            throw new McpException("OAuth metadata issuer does not match the discovered authorization server.");
        return metadata;
    }

    private async Task<JsonObject?> TryGetAsync(string url, CancellationToken ct)
    {
        ValidateEndpoint(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }
    private static async Task<JsonObject> ReadSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new McpException($"OAuth server returned HTTP {(int)response.StatusCode}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return JsonNode.Parse(await ProtocolText.ReadBodyAsync(response.Content, deadline.Token, 1024 * 1024).ConfigureAwait(false)) as JsonObject
            ?? throw new McpException("OAuth server returned an invalid response.");
    }
    internal static void ValidateEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new McpException("Authenticated MCP/OAuth endpoints must use HTTPS (HTTP is allowed only on loopback).");
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (!values.TryAdd(key, pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : ""))
                throw new McpException("OAuth callback contains duplicate query parameters.");
        }
        return values;
    }
    public void Dispose() { _lifetime.Cancel(); if (_ownsHttp) _http.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await Task.WhenAll(_logins.Values.Select(x => x.Task)).ConfigureAwait(false); } catch (Exception) { /* Login status retains failures. */ }
        foreach (var attempt in _logins.Values) attempt.Cancellation.Dispose();
        if (_ownsHttp) _http.Dispose();
        _lifetime.Dispose();
    }
    private sealed record PendingLogin(string Key, McpServerConfig Server, string ClientId, string Endpoint,
        string Redirect, string Verifier, DateTimeOffset Expires);
    private sealed record LoginAttempt(string Key, CancellationTokenSource Cancellation, Task Task, DateTimeOffset CreatedAt);
}
