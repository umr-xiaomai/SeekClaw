using System.Text.Json.Nodes;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Mcp;

/// <summary>Loads schemas only; business tool execution remains a separate Agent tool call.</summary>
internal sealed class McpToolSearchTool(McpManager manager) : ITool
{
    public string Name => "tool_search";
    public string Description => "Discover enabled MCP servers and load relevant tool schemas. "
        + "Call with no query/server to list server metadata without connecting. "
        + "Supply a server and/or keywords to discover tools, then call returned tools by name. "
        + "This does not execute the remote tools.";
    public bool Mutating => false;
    public bool RequiresWorkspace => false;
    internal bool IsAvailable(WorkspaceInfo workspace, bool networkEnabled) => manager.IsAvailable(workspace, networkEnabled);
    public string StatusLabel => "Discovering tools";
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("query", ToolSchema.String("Tool name, operation or description to search for"), false),
        ("server", ToolSchema.String("An enabled server name from the MCP server catalog; narrows connection and search"), false),
        ("limit", ToolSchema.Integer("Maximum tool schemas to load, default 5, maximum 12"), false));

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        if (arguments["query"] is { } queryNode && (queryNode is not JsonValue queryValue || !queryValue.TryGetValue<string>(out _))
            || arguments["server"] is { } serverNode && (serverNode is not JsonValue serverValue || !serverValue.TryGetValue<string>(out _)))
            return Task.FromResult(ToolResult.Fail("query and server must be strings."));
        var query = arguments["query"]?.GetValue<string>();
        var server = arguments["server"]?.GetValue<string>();
        if (query?.Length > 1000 || server?.Length > 256)
            return Task.FromResult(ToolResult.Fail("Tool search query or server name exceeds the length limit."));
        var limit = 5;
        if (arguments["limit"] is { } limitNode
            && (limitNode is not JsonValue limitValue || !limitValue.TryGetValue<int>(out limit)))
            return Task.FromResult(ToolResult.Fail("limit must be an integer."));
        return manager.SearchAsync(query, server, limit, context, ct);
    }
}
