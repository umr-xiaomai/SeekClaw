# Daemon 守护进程与 IPC 协议

为了支持外部扩展（如 VS Code / Visual Studio 插件、JetBrains 插件、Electron 桌面客户端或 Web UI 页面），SeekClaw 提供了后台 Daemon 模式。

Daemon 守护进程由 `seekclaw_runtime/Daemon` 模块管理，通过高效的进程间通信 (IPC) 协议将 AI Agent 能力暴露给第三方应用程序。

---

## 通信信道 (Transport Channels)

SeekClaw Daemon 自动适配跨平台 IPC 信道：

- **Windows 操作系统**：命名管道 (Named Pipe)，例如 `\\.\pipe\seekclaw-daemon.pipe`。
- **Linux & macOS 操作系统**：Unix Domain Socket，例如 `/tmp/seekclaw-daemon.sock`。

---

## 基于行分隔的 JSON-RPC 消息协议

客户端与 Daemon 之间的通讯采用简单、高效的 JSON 行分隔（Line-delimited JSON）流式协议。

### 1. 启动任务请求 (`RunTurn`)

**Client -> Daemon**:
```json
{
  "jsonrpc": "2.0",
  "id": 101,
  "method": "agent/runTurn",
  "params": {
    "workspacePath": "E:\\Projects\\MyApp",
    "prompt": "对 AuthController 增加 Swagger 注解",
    "modelOverride": "openai/gpt-5.5"
  }
}
```

### 2. 实时事件推送 (`Notifications`)

Daemon 运行过程中会把 `EventBus` 中的状态增量推送到客户端：

**Daemon -> Client (Thinking Delta)**:
```json
{
  "jsonrpc": "2.0",
  "method": "agent/onThinkingDelta",
  "params": {
    "turnId": "turn-8821",
    "delta": "正在解析 AuthController.cs 的抽象依赖关系..."
  }
}
```

**Daemon -> Client (Tool Call Started)**:
```json
{
  "jsonrpc": "2.0",
  "method": "agent/onToolCallStarted",
  "params": {
    "toolName": "read_file",
    "path": "Controllers/AuthController.cs"
  }
}
```

---

## 启动 Daemon 服务命令

```bash
seekclaw daemon --pipe seekclaw-daemon.pipe
```

GUI 前端连接到 Pipe 后，即可实现原生界面的代码流式生成、Diff 差异比对面板和对话记录展示。
