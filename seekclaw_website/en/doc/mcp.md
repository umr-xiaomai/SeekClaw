# Model Context Protocol (MCP)

SeekClaw acts as an MCP Client, connects to external servers, and registers discovered tools and prompts with the Runtime. The current client protocol version is `2024-11-05`.

## Supported surface

- **stdio** starts a local process and exchanges JSON-RPC 2.0 over stdin and stdout.
- **HTTP SSE** receives messages over a Server-Sent Events stream and sends requests to the POST endpoint announced by the server.
- **Streamable HTTP** posts directly to `/mcp`; the response may be a JSON body or a streaming SSE body.
- The manager calls `tools/list` and registers tools with `IToolRegistry`.
- It calls `prompts/list`, with `prompts/get` used for prompt content when supported.
- The client can list resources, but `McpManager` does not currently inject them into Agent context automatically.
- `websocket` is reserved but not implemented.

## Configure MCP in Desktop

Open “Settings → MCP” to add a global or workspace server, pick a connection method (stdio / HTTP SSE / Streamable HTTP), fill in the command or URL, arguments, environment, and enabled state, then select “Save and reload.”

![Desktop MCP Server configuration](/screenshots/desktop/mcp-servers.png)

Queries return MCP environment-variable names but not their sensitive values. When editing an existing server, enter again any environment values that should be persisted.

## JSON configuration

Global servers can be stored under `mcp.servers` in `~/.seekclaw/config.json`. A workspace can use `.seekclaw/mcp/servers.json` or the `mcp` field in `.seekclaw/config.json`; a workspace entry with the same name overrides the global one.

```json
{
  "servers": {
    "filesystem": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "E:\\Project"],
      "env": { "TOKEN": "..." },
      "enabled": true
    },
    "remote-tools": {
      "transport": "sse",
      "url": "https://mcp.example.com/sse",
      "enabled": true
    }
  }
}
```

The current `McpServerConfig` has no `autoConnect` or custom HTTP `headers` field: remote authentication must live in the URL or be allowed by the server.

## Connection and reload behavior

The Daemon begins accepting IPC connections before MCP initialization proceeds serially in the background. A workspace switch, configuration save, or `mcp.reload` unregisters old tools and prompts and closes old clients before loading the new configuration, preventing stale duplicate registrations. A server that fails to connect is reported as disconnected with its reason and no longer blocks the remaining servers; remote connections use a 10 second connect timeout and initialization waits at most 30 seconds.

Saving a server or flipping its enabled state returns immediately and connects in the background: affected servers are marked as connecting first, and the Daemon broadcasts an `mcp.updated` event to every connected client once the attempt finishes, so enabling an unreachable server never freezes the UI.

Use the CLI to inspect connections:

```bash
seekclaw mcp list
seekclaw mcp test
```

`mcp test` connects to every enabled server and reports its status and tool count. Desktop provides equivalent visual status and reload controls.
