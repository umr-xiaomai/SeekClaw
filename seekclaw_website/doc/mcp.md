# Model Context Protocol (MCP) 集成

SeekClaw 全量集成 Anthropic 推出的 **Model Context Protocol (MCP)** 标准协议。通过 MCP，SeekClaw 可以原生作为 MCP 客户端（Client），自动发现、挂载和调度各种外部 MCP 服务器提供的工具（Tools）、提示模板（Prompts）与数据资源（Resources）。

---

## 传输协议支持

SeekClaw 内置支持两类 MCP 传输通道：

1. **Stdio 传输**：通过启动子进程并通过标准输入/输出 (stdin/stdout) 进行 JSON-RPC 2.0 通信。适用于本地 node/python 编写的 MCP 工具。
2. **SSE 传输 (Server-Sent Events)**：通过 HTTP/HTTPS 和 SSE 实现与远端 MCP 服务器的长连接通信。

---

## `mcp/servers.json` 配置文件

可在工作区 `.seekclaw/mcp/servers.json` 或全局 `~/.seekclaw/mcp/servers.json` 中定义连接的 MCP 服务：

```json
{
  "servers": {
    "git-mcp": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-git"],
      "transport": "stdio",
      "autoConnect": true
    },
    "postgresql": {
      "command": "node",
      "args": ["/opt/mcp-servers/postgres.js"],
      "env": {
        "DATABASE_URL": "postgresql://localhost:5432/mydb"
      },
      "transport": "stdio"
    },
    "remote-docs-mcp": {
      "url": "https://mcp.internal.example.com/sse",
      "transport": "sse",
      "headers": {
        "Authorization": "Bearer token-xxxx"
      }
    }
  }
}
```

---

## 工具与资源的自动发现

当 SeekClaw 启动或运行 `ConnectAllAsync` 时：

1. **自动握手**：按照 JSON-RPC 2.0 规范向各个 MCP 服务发送 `initialize`。
2. **动态注册**：调用 `tools/list` 提取可用工具，并无缝注入 SeekClaw 的 `IToolRegistry` 中。对于 LLM 而言，MCP 工具与原生内置工具完全一致。
3. **提示词扩展**：调用 `prompts/list` 引入外部 prompt 资源。
4. **资源注入**：调用 `resources/read` 动态加载特定文件或 URI 数据填入 Agent 上下文。

---

## 命令行查看 MCP 状态

```bash
# 查看所有已连接的 MCP 服务器及其注册工具数量
seekclaw mcp status

# 重新加载并连接 MCP 服务器
seekclaw mcp reload
```
