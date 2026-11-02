using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class McpAuthenticationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seekclaw-mcp-tests", Guid.NewGuid().ToString("N"));
    public McpAuthenticationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    private static McpServerConfig Server() => new() { Transport = "http", Url = "https://mcp.test/tools",
        OAuth = new McpOAuthConfig { ClientId = "client", AuthorizationEndpoint = "https://auth.test/authorize", TokenEndpoint = "https://auth.test/token" } };

    [Fact]
    public async Task OAuth_UsesPkceStateAndResource_RefreshesOnce_AndDoesNotExposeTokens()
    {
        var forms = new List<Dictionary<string, string>>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var form = Query(await request.Content!.ReadAsStringAsync());
            lock (forms) forms.Add(form);
            return Json(new JsonObject { ["access_token"] = forms.Count == 1 ? "sensitive-first" : "sensitive-refreshed",
                ["refresh_token"] = "sensitive-refresh", ["token_type"] = "Bearer", ["expires_in"] = forms.Count == 1 ? 1 : 3600 });
        }));
        var credentials = new McpCredentialStore(_root);
        await using var auth = new McpAuthentication(credentials, http);
        var server = Server();
        var login = await auth.BeginAsync("server", server, "http://127.0.0.1:40123/callback", CancellationToken.None);
        var state = login["state"]!.GetValue<string>();
        var query = Query(new Uri(login["authorizationUrl"]!.GetValue<string>()).Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(server.Url, query["resource"]);
        await Assert.ThrowsAsync<McpException>(() => auth.CompleteAsync(state, "http://127.0.0.1:40123/callback?code=code&state=wrong", CancellationToken.None));
        await auth.CompleteAsync(state, "http://127.0.0.1:40123/callback?code=code&state=" + state, CancellationToken.None);
        await Assert.ThrowsAsync<McpException>(() => auth.CompleteAsync(state, "http://127.0.0.1:40123/callback?code=code&state=" + state, CancellationToken.None));
        await using var secondAuth = new McpAuthentication(new McpCredentialStore(_root), http);
        var headers = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? auth : secondAuth).HeadersAsync("server", server, CancellationToken.None)));
        Assert.All(headers, value => Assert.Equal("Bearer sensitive-refreshed", value["Authorization"]));
        Assert.Equal(2, forms.Count);
        Assert.Equal("authorization_code", forms[0]["grant_type"]);
        Assert.Equal("refresh_token", forms[1]["grant_type"]);
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(forms[0]["code_verifier"])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        Assert.DoesNotContain("sensitive", login.ToJsonString());
        Assert.Empty(await auth.HeadersAsync("other", server, CancellationToken.None));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("sensitive", File.ReadAllText(Directory.GetFiles(_root).Single()));
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Directory.GetFiles(_root).Single()));
        await auth.LogoutAsync("server", server);
        Assert.Empty(await auth.HeadersAsync("server", server, CancellationToken.None));
    }

    [Fact]
    public async Task OAuth_LoopbackListener_CompletesARealBrowserCallback()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new JsonObject { ["access_token"] = "token" }))));
        await using var auth = new McpAuthentication(new McpCredentialStore(_root), http);
        var login = await auth.BeginLoopbackAsync("server", Server(), CancellationToken.None);
        var state = login["state"]!.GetValue<string>();
        Assert.Equal("pending", auth.LoginStatus(state)["status"]!.GetValue<string>());
        var redirect = Query(new Uri(login["authorizationUrl"]!.GetValue<string>()).Query)["redirect_uri"];
        using var browser = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using (var aborted = new System.Net.Sockets.TcpClient()) await aborted.ConnectAsync("127.0.0.1", new Uri(redirect).Port);
        var invalid = await browser.GetAsync(redirect + "?code=accepted&state=" + state + "suffix");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("pending", auth.LoginStatus(state)["status"]!.GetValue<string>());
        var response = await browser.GetAsync(redirect + "?code=accepted&state=" + state);
        response.EnsureSuccessStatusCode();
        await auth.WaitForLoginAsync(state, CancellationToken.None);
        Assert.Equal("completed", auth.LoginStatus(state)["status"]!.GetValue<string>());
        Assert.DoesNotContain("token", auth.LoginStatus(state).ToJsonString());
    }

    [Fact]
    public async Task OAuth_LogoutCancelsPendingLogin_AndCannotBeUndoneByAnOldCallback()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new JsonObject { ["access_token"] = "token" }))));
        await using var auth = new McpAuthentication(new McpCredentialStore(_root), http);
        var login = await auth.BeginLoopbackAsync("server", Server(), CancellationToken.None);
        var state = login["state"]!.GetValue<string>();
        var redirect = Query(new Uri(login["authorizationUrl"]!.GetValue<string>()).Query)["redirect_uri"];
        await auth.LogoutAsync("server", Server());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.WaitForLoginAsync(state, CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => auth.CompleteAsync(state, redirect + "?code=old&state=" + state, CancellationToken.None));
        Assert.Empty(await auth.HeadersAsync("server", Server(), CancellationToken.None));
    }

    [Fact]
    public async Task OAuth_RejectsInsecureEndpointsAndNonLoopbackCallbacks()
    {
        await using var auth = new McpAuthentication(new McpCredentialStore(_root));
        await Assert.ThrowsAsync<McpException>(() => auth.BeginAsync("server", Server(), "https://evil.test/callback", CancellationToken.None));
        var server = Server(); server.OAuth!.TokenEndpoint = "http://evil.test/token";
        await Assert.ThrowsAsync<McpException>(() => auth.BeginAsync("server", server, "http://127.0.0.1:9000/callback", CancellationToken.None));
    }

    [Fact]
    public void FullContent_ProjectsImagesAndResources_AndRoundTripsWithoutLosingUnknownBlocks()
    {
        var result = McpCallResult.Parse(JsonNode.Parse("""
            {"content":[
              {"type":"text","text":"hello","annotations":{"audience":["user"]}},
              {"type":"image","mimeType":"image/png","data":"AQID"},
              {"type":"audio","mimeType":"audio/wav","data":"BAUG"},
              {"type":"resource_link","uri":"custom://report","name":"report"},
              {"type":"resource","resource":{"uri":"custom://body","mimeType":"text/plain","text":"embedded"}},
              {"type":"future","value":{"any":true}}
            ],"structuredContent":{"answer":42},"_meta":{"vendor":"metadata"}}
            """)!.AsObject());
        Assert.Contains("hello", result.Text);
        Assert.Contains("embedded", result.Text);
        Assert.Contains("custom://report", result.Text);
        Assert.Single(result.Images());
        var store = new SessionStore(new SeekClawDatabase(Path.Combine(_root, "sessions.db")));
        var workspace = new WorkspaceInfo { Root = _root, ProjectKinds = [] };
        var session = store.Create(workspace);
        store.Append(session, ChatMessage.ToolResult("call", "mcp__test", result.Text, result.Success,
            images: result.Images(), content: result.Content, structuredContent: result.StructuredContent, metadata: result.Metadata));
        var reloaded = Assert.Single(store.Load(workspace, session.Header.Id)!.Messages);
        Assert.True(JsonNode.DeepEquals(result.Content, reloaded.ToolContent));
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, reloaded.ToolStructuredContent));
        Assert.True(JsonNode.DeepEquals(result.Metadata, reloaded.ToolMetadata));
        Assert.Single(reloaded.Images!);
    }

    private static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(JsonObject content) => new(HttpStatusCode.OK) { Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
    }
}
