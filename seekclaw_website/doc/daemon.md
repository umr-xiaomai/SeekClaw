# Daemon 守护进程与 IPC 协议

SeekClaw Daemon 通过本地 IPC 向桌面端、IDE 插件和其他客户端开放 Runtime。当前协议版本为 `2.1`，使用一行一个 JSON 对象的 JSONL 消息，不是完整的 JSON-RPC 2.0 实现。

## 连接地址

- Windows Named Pipe：`\\.\pipe\seekclaw`
- Linux / macOS Unix Socket：`~/.seekclaw/daemon.sock`

请求和响应都必须以换行符结尾。一次连接可以在 Agent 输出过程中继续发送控制请求。

```json
{"id":1,"method":"chat","params":{"sessionId":"20260731-120000-a1b2c3","message":"分析当前项目"}}
{"id":1,"event":"thinking","sessionId":"20260731-120000-a1b2c3","data":"正在检查项目结构"}
{"id":1,"event":"delta","sessionId":"20260731-120000-a1b2c3","data":"这是一个 .NET 项目。"}
{"id":1,"event":"done","sessionId":"20260731-120000-a1b2c3","data":"这是一个 .NET 项目。"}
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
| `agent.steer` | `{ "sessionId": "...", "message": "..." }` | 向正在运行的 turn 添加指导，不取消当前请求 |

`workspace.open` 会清除当前连接的旧式恢复 Session。新的 `chat` 请求应携带 `sessionId`，项目任务同时携带 `workspace`；turn 启动时会捕获自己的工作区，之后的工作区、模式或模型切换不会改变已经运行的 turn。

## 执行与取消

`chat` 是兼容旧客户端的主要执行方法，`agent.runTurn` 和 `agent/runTurn` 是别名。

支持视觉的模型可以接收 `images` 数组。每张图片包含客户端生成的 `id`、文件名、MIME 类型和不带 Data URL 前缀的 Base64 数据；单次最多 10 张、单张最多 10 MB、合计最多 40 MB。支持 `image/png`、`image/jpeg`、`image/webp` 和 `image/gif`，纯图片消息可以省略 `message`。

```json
{"id":9,"method":"chat","params":{"message":"比较两张图片","images":[{"id":"a","name":"before.png","mediaType":"image/png","data":"..."},{"id":"b","name":"after.webp","mediaType":"image/webp","data":"..."}]}}
```

```json
{"id":10,"method":"chat","params":{"message":"修复测试","reasoningLevel":"high"}}
{"id":11,"method":"agent.cancel","params":{"requestId":10}}
```

正在运行的 turn 可以接收附加指导。`agent.steer` 会把消息放入当前 turn 的指导队列，当前模型请求结束后再加入上下文并继续下一步，不会取消或打断正在进行的请求：

```json
{"id":12,"method":"agent.steer","params":{"sessionId":"20260731-120000-a1b2c3","message":"也请检查边界情况"}}
{"id":12,"event":"result","sessionId":"20260731-120000-a1b2c3","data":"guidance queued"}
```

`agent.cancel` 的 `requestId` 可省略，此时取消当前连接的活动 turn。取消请求本身返回 `result`，被取消的 chat 最终返回：

```json
{"id":11,"event":"result","data":"cancellation requested for 10"}
{"id":10,"event":"cancelled","data":"取消前已生成的部分文本"}
```

流式事件包括 `thinking`、`delta`、`steer`、`status`、`image_view`、`tool_start` 和 `tool_done`。`steer` 表示附加指导已经进入当前 turn 的上下文；`image_view` 的 `details.imageId` 指明模型正在查看哪张上传图片。终止事件包括 `done`、`cancelled` 和 `error`。

## 其他方法

| 方法 | 参数 | 说明 |
| --- | --- | --- |
| `session.list` | `{ "workspace": "...", "global": false, "includeArchived": true }` | 按工作区或全局范围列出 Session |
| `session.get` | `{ "id": "...", "workspace": "..." }` | 读取 Session 及其消息 |
| `session.update` | `{ "id": "...", "title": "...", "reasoningLevel": "high" }` | 更新标题、思考深度等 Session 元数据 |
| `session.archive` | `{ "id": "...", "archived": true }` | 归档或恢复 Session |
| `session.delete` | `{ "id": "..." }` | 永久删除 Session |
| `session.resume` | `{ "id": "...", "global": false }` | 恢复 Session |
| `session.new` | `{ "workspace": "...", "reasoningLevel": "high" }` 或 `{ "global": true }` | 创建并绑定一个新 Session |
| `model.list` | 无 | 列出可用的 `provider/model` 引用 |
| `model.catalog` | 无 | 返回模型详情、能力和活动状态 |
| `model.switch` | `{ "model": "provider/model" }` | 切换并持久化模型 |
| `model.test` | `{ "model": "provider/model" }` | 发送最小真实请求测试模型 |
| `doctor` | 无 | 返回 Runtime 健康检查摘要 |
| `doctor.run` | 无 | 返回结构化 Runtime 与 Provider 检查 |
| `lock.list` | 无 | 返回当前文件写锁的“文件-任务”占用表快照 |
| `shutdown` | 无 | 取消全部活动 turn，返回 `bye` 并优雅停止 Daemon |

Session 方法可传 `workspace` 指向具体项目，也可传 `global: true` 使用不绑定目录的全局 Session 空间。`includeArchived` 控制列表是否包含已归档任务。Desktop 在第一次发送消息时才调用 `session.new`，因此新建一个空白任务不会产生无内容的 Session。

`reasoningLevel` 使用统一枚举：`none`、`low`、`medium`、`high`、`max`、`xhigh`、`ultra`。它不是 Provider API 参数；Runtime 会根据模型能力和 Provider 适配后再生成请求。`xhigh`/`ultra` 属于扩展档位，默认最高能力为 `max`，DeepSeek 会明确将二者转换为 `max`。

## Desktop 管理方法

Desktop 设置中心通过结构化方法管理与 CLI 相同的配置，不直接读取配置文件。显式存储的 Provider `apiKey` 会通过 `provider.list` 返回，以便 Desktop 直接显示和编辑。Runtime 不会从环境变量读取 API Key；MCP 环境变量仍仅返回键名，不返回值。

| 方法 | 说明 |
| --- | --- |
| `profile.list/upsert/use/remove` | 管理运行 Profile |
| `provider.list/upsert/use/remove/test` | 管理和测试 Provider |
| `mcp.list/upsert/remove/reload` | 管理、重连 MCP Server 并刷新工具注册 |
| `skill.list/toggle` | 查询和启用/禁用 Skill |
| `usage.get` | 返回按模型聚合的调用、Token、成本和延迟 |

Daemon 会先建立 IPC 监听，再在后台初始化 MCP。`mcp.reload`、MCP 配置修改和工作区切换都会先注销旧工具与 Prompt，再串行连接新配置。

Daemon 对每个 `chat` 请求按 `sessionId` 创建独立的 Agent turn。每个 turn 使用隔离的 Runtime、Prompt/Skills、MCP 注册和事件订阅，因此同一连接或多个连接可以并发运行任意数量的任务；并发度由 CPU、内存、Provider 和本机 I/O 性能共同决定。管理类配置写入仍然串行化，避免配置文件互相覆盖，但不会阻塞已经启动的 Agent turn。

Daemon 进程内维护一个集中式文件写锁协调器（Task Coordinator），作为所有并发 turn 的文件锁唯一信任源。`write_file` 和 `edit_file` 在修改文件前会先按工作区+文件路径申请写锁：锁空闲则授予并完成修改后释放；锁被其他任务占用时工具最多等待 30 秒，超时后返回明确的失败提示（附当前持有者），要求模型等待并重新读取最新内容后重试。`edit_file` 在拿到锁后才读取文件，因此修改总是作用于磁盘上的最新内容，避免并行任务互相覆盖或产生无效编辑。turn 结束时（含取消）协调器会释放该任务持有的全部锁。可通过 `lock.list` 查看当前占用表。

## Desktop 的 Daemon 生命周期

打包版 Desktop 启动时先连接本地端点。若没有 Daemon，它会从 `resources/runtime` 启动自包含 Runtime，并在 24 次短间隔探测内等待端点就绪。Desktop 只记录自己创建的子进程；退出时向该实例发送 `shutdown`，等待其退出，超时后才终止进程。若启动时连接的是外部 Daemon，退出 Desktop 只断开连接。
