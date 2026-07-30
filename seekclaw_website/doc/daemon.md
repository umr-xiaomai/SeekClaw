# Daemon 守护进程与 IPC 协议

SeekClaw Daemon 通过本地 IPC 向桌面端、IDE 插件和其他客户端开放 Runtime。当前协议版本为 `2.0`，使用一行一个 JSON 对象的 JSONL 消息，不是完整的 JSON-RPC 2.0 实现。

## 连接地址

- Windows Named Pipe：`\\.\pipe\seekclaw`
- Linux / macOS Unix Socket：`~/.seekclaw/daemon.sock`

请求和响应都必须以换行符结尾。一次连接可以在 Agent 输出过程中继续发送控制请求。

```json
{"id":1,"method":"chat","params":{"message":"分析当前项目"}}
{"id":1,"event":"thinking","data":"正在检查项目结构"}
{"id":1,"event":"delta","data":"这是一个 .NET 项目。"}
{"id":1,"event":"done","data":"这是一个 .NET 项目。"}
```

响应保持统一事件信封：`id` 对应请求，`event` 表示事件类型，`data` 始终为字符串。`result` 中的结构化结果会编码成 JSON 字符串，由客户端再次解析。

## 协议与状态

| 方法 | 参数 | 说明 |
| --- | --- | --- |
| `ping` | 无 | 返回 `pong` |
| `protocol.info` | 无 | 返回协议版本、能力与方法列表 |
| `workspace.get` | 无 | 返回当前工作区路径、项目类型和模式 |
| `workspace.open` | `{ "path": "..." }` | 验证并切换 Runtime 工作区 |
| `workspace.init` | 无 | 初始化当前工作区的 SeekClaw 目录 |
| `agent.mode.get` | 无 | 返回 `plan`、`readonly`、`edit` 或 `auto` |
| `agent.mode.switch` | `{ "mode": "edit" }` | 切换并持久化 Agent 模式 |

`workspace.open` 会清除当前连接已恢复的 Session。活动 turn 运行时，工作区、模式和模型切换会返回 `error`，避免执行上下文在中途改变。

## 执行与取消

`chat` 是兼容旧客户端的主要执行方法，`agent.runTurn` 和 `agent/runTurn` 是别名。

```json
{"id":10,"method":"chat","params":{"message":"修复测试"}}
{"id":11,"method":"agent.cancel","params":{"requestId":10}}
```

`agent.cancel` 的 `requestId` 可省略，此时取消当前连接的活动 turn。取消请求本身返回 `result`，被取消的 chat 最终返回：

```json
{"id":11,"event":"result","data":"cancellation requested for 10"}
{"id":10,"event":"cancelled","data":"取消前已生成的部分文本"}
```

流式事件包括 `thinking`、`delta`、`status`、`tool_start` 和 `tool_done`。终止事件包括 `done`、`cancelled` 和 `error`。

## 其他方法

| 方法 | 参数 | 说明 |
| --- | --- | --- |
| `session.list` | `{ "workspace": "...", "global": false, "includeArchived": true }` | 按工作区或全局范围列出 Session |
| `session.get` | `{ "id": "...", "workspace": "..." }` | 读取 Session 及其消息 |
| `session.update` | `{ "id": "...", "title": "..." }` | 更新标题等 Session 元数据 |
| `session.archive` | `{ "id": "...", "archived": true }` | 归档或恢复 Session |
| `session.delete` | `{ "id": "..." }` | 永久删除 Session |
| `session.resume` | `{ "id": "...", "global": false }` | 恢复 Session |
| `session.new` | `{ "global": false }` | 创建并绑定一个新 Session |
| `model.list` | 无 | 列出可用的 `provider/model` 引用 |
| `model.catalog` | 无 | 返回模型详情、能力和活动状态 |
| `model.switch` | `{ "model": "provider/model" }` | 切换并持久化模型 |
| `model.test` | `{ "model": "provider/model" }` | 发送最小真实请求测试模型 |
| `doctor` | 无 | 返回 Runtime 健康检查摘要 |
| `doctor.run` | 无 | 返回结构化 Runtime 与 Provider 检查 |
| `shutdown` | 无 | 取消活动 turn，返回 `bye` 并优雅停止 Daemon |

Session 方法可传 `workspace` 指向具体项目，也可传 `global: true` 使用不绑定目录的全局 Session 空间。`includeArchived` 控制列表是否包含已归档任务。Desktop 在第一次发送消息时才调用 `session.new`，因此新建一个空白任务不会产生无内容的 Session。

## Desktop 管理方法

Desktop 设置中心通过结构化方法管理与 CLI 相同的配置，不直接读取配置文件。显式存储的 Provider `apiKey` 会通过 `provider.list` 返回，以便 Desktop 直接显示和编辑；`apiKeyEnv` 指向的环境变量值不会被解析并返回。MCP 环境变量仅返回键名，不返回值。

| 方法 | 说明 |
| --- | --- |
| `profile.list/upsert/use/remove` | 管理运行 Profile |
| `provider.list/upsert/use/remove/test` | 管理和测试 Provider |
| `mcp.list/upsert/remove/reload` | 管理、重连 MCP Server 并刷新工具注册 |
| `skill.list/toggle` | 查询和启用/禁用 Skill |
| `usage.get` | 返回按模型聚合的调用、Token、成本和延迟 |

Daemon 会先建立 IPC 监听，再在后台初始化 MCP。`mcp.reload`、MCP 配置修改和工作区切换都会先注销旧工具与 Prompt，再串行连接新配置。

当前 Daemon 共享一个 `SeekClawRuntime`、一个活动工作区和一个事件总线，因此全局只允许一个 Agent turn。其他连接在 Runtime 忙碌时会收到明确的 `error`；这是协议保证，不应通过并行请求绕过。

## Desktop 的 Daemon 生命周期

打包版 Desktop 启动时先连接本地端点。若没有 Daemon，它会从 `resources/runtime` 启动自包含 Runtime，并在 24 次短间隔探测内等待端点就绪。Desktop 只记录自己创建的子进程；退出时向该实例发送 `shutdown`，等待其退出，超时后才终止进程。若启动时连接的是外部 Daemon，退出 Desktop 只断开连接。
