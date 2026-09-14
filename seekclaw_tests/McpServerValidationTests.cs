using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Daemon;
using Xunit;

namespace SeekClaw.Tests;

public class McpServerValidationTests
{
    [Theory]
    [InlineData("stdio", "npx", null)]
    [InlineData("STDIO", "npx", null)]
    [InlineData("sse", null, "http://localhost:5070/mcp")]
    [InlineData("http", null, "https://example.com/mcp")]
    [InlineData("streamable-http", null, "https://example.com/mcp")]
    [InlineData("streamable_http", null, "https://example.com/mcp")]
    public void ValidateMcpServer_AcceptsEveryTransportOfferedByTheDesktopUi(string transport, string? command, string? url)
    {
        var server = new McpServerConfig { Transport = transport, Command = command, Url = url };

        DaemonAdminApi.ValidateMcpServer("test-server", server);
    }

    [Fact]
    public void ValidateMcpServer_StreamableHttpWithoutUrl_IsRejectedWithAReadableMessage()
    {
        var server = new McpServerConfig { Transport = "http", Url = "" };

        var error = Assert.Throws<DaemonRequestException>(() => DaemonAdminApi.ValidateMcpServer("StarLife", server));

        Assert.Contains("URL", error.Message);
    }

    [Fact]
    public void ValidateMcpServer_StdioWithoutCommand_IsRejectedWithAReadableMessage()
    {
        var server = new McpServerConfig { Transport = "stdio", Command = "  " };

        var error = Assert.Throws<DaemonRequestException>(() => DaemonAdminApi.ValidateMcpServer("filesystem", server));

        Assert.Contains("启动命令", error.Message);
    }

    [Fact]
    public void ValidateMcpServer_UnknownTransport_IsRejected()
    {
        var server = new McpServerConfig { Transport = "carrier-pigeon" };

        var error = Assert.Throws<DaemonRequestException>(() => DaemonAdminApi.ValidateMcpServer("weird", server));

        Assert.Contains("carrier-pigeon", error.Message);
    }
}
