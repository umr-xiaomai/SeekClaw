using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Mcp;
using Xunit;

namespace SeekClaw.Tests;

public class McpTransportTests
{
    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    public async Task HttpMcpTransport_UsesNegotiatedProtocolVersionOnSubsequentRequests(string version)
    {
        string? initializedVersion = null;
        using var http = new HttpClient(new DelegatingMockHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            if (request.Headers.TryGetValues("MCP-Protocol-Version", out var values)) initializedVersion = Assert.Single(values);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
            { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject { ["protocolVersion"] = version } }.ToJsonString(), Encoding.UTF8, "application/json") };
        }));
        await using var client = new McpClient("test", new HttpMcpTransport("http://localhost/mcp", http));
        await client.InitializeAsync(CancellationToken.None);
        Assert.Equal(version, initializedVersion);
    }

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    private sealed class TrackingResponse : HttpResponseMessage
    {
        public TrackingResponse(HttpStatusCode status, HttpContent content, string mediaType) : base(status)
        {
            Content = content;
            Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task HttpMcpTransport_StreamableHttp_HandlesSseResponseOnPost()
    {
        // Simulates StarLife: GET returns JSON welcome and closes, POST returns SSE with event: message
        var handler = new DelegatingMockHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"status":"online","server":"starlife_webAPI","transport":"Streamable HTTP","endpoint":"POST /mcp"}""",
                        Encoding.UTF8, "application/json")
                };
            }

            var sseContent = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"starlife_webAPI\",\"version\":\"1.0\"}}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseContent, Encoding.UTF8, "text/event-stream")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:5070/mcp", client);
        await using var mcpClient = new McpClient("StarLife", transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mcpClient.InitializeAsync(cts.Token);
    }

    [Fact]
    public async Task HttpMcpTransport_DirectHttp_HandlesJsonResponseOnPost()
    {
        var handler = new DelegatingMockHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            var jsonContent = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"json-mcp","version":"1.0"}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);
        await using var mcpClient = new McpClient("JsonMcp", transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mcpClient.InitializeAsync(cts.Token);
    }

    [Fact]
    public async Task HttpMcpTransport_DisposesStartSseResponseAfterReadLoopEnds()
    {
        var response = new TrackingResponse(
            HttpStatusCode.OK,
            new StringContent("event: endpoint\ndata: /mcp\n\n", Encoding.UTF8, "text/event-stream"),
            "text/event-stream");
        var handler = new DelegatingMockHandler(_ => response);

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);
        await transport.StartAsync(CancellationToken.None);
        await transport.DisposeAsync();

        Assert.True(response.Disposed);
    }

    [Fact]
    public async Task HttpMcpTransport_DisposesJsonGreetingResponse()
    {
        var response = new TrackingResponse(
            HttpStatusCode.OK,
            new StringContent("""{"endpoint":"POST /mcp"}""", Encoding.UTF8),
            "application/json");
        var handler = new DelegatingMockHandler(_ => response);

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);
        await transport.StartAsync(CancellationToken.None);

        Assert.True(response.Disposed);
    }

    [Fact]
    public async Task HttpMcpTransport_PreservesSessionIdAcrossRequests()
    {
        string? capturedSessionId = null;
        var handler = new DelegatingMockHandler(req =>
        {
            if (req.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                capturedSessionId = values.FirstOrDefault();
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"session-server"}}}""", Encoding.UTF8, "application/json")
            };
            resp.Headers.Add("Mcp-Session-Id", "session-xyz-789");
            return resp;
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:9000/mcp", client);

        await transport.StartAsync(CancellationToken.None);
        await transport.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize" }, CancellationToken.None);

        // Second request should send the session ID captured from the first
        await transport.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, CancellationToken.None);

        Assert.Equal("session-xyz-789", capturedSessionId);
    }

    [Fact]
    public async Task McpClient_HandlesStringIdsInResponse()
    {
        var handler = new DelegatingMockHandler(req =>
        {
            // Server returns string "id": "1" instead of numeric 1
            var jsonContent = """{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"string-id-server"}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);
        await using var mcpClient = new McpClient("StringIdMcp", transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mcpClient.InitializeAsync(cts.Token);
    }
}
