# 任务执行、自动化与 CI

SeekClaw 的任务基础设施由 Runtime 管理，Desktop、Daemon、CLI 共用实现。本文是 Git worktree、命令会话、Skills、MCP 认证、子 Agent、上下文自动化和非交互 CLI 的使用与实现说明。

## 目录与生命周期

一个任务有两个目录概念：**源工作区**决定对话、项目和计划任务的存储作用域；**执行目录**决定文件工具、命令和 Git 操作实际访问的位置。创建 worktree 后，对话仍然属于原项目，执行目录切换到独立 checkout。仓库中的子项目会映射到 checkout 内的同一相对路径。

`RuntimeTaskServices` 持有进程级命令会话、worktree 管理器、子 Agent 管理器、认证服务和对话协调器。短生命周期的前台、计划任务和子 Agent Runtime 借用这些服务。配置通过 `TaskConfigStore` 复制，因此模型、权限和专家提示的局部覆盖不会写回用户的全局配置。

`SessionTurnCoordinator` 使用内存信号量和文件租约协调同一对话。Agent 获取租约后重新读取历史，避免前台与计划任务拿着旧历史相互覆盖。worktree 的执行租约与创建、归档、恢复操作互斥。持久任务元数据保存在 SQLite 的 `execution_records` 表，用量日志使用跨进程互斥锁串行读写。

## Git worktree

Desktop：打开任务设置，在“Git worktree 隔离”中选择起始 Git 引用并创建。创建后，文件操作、外部终端、文件夹打开和 Git 面板使用执行目录。任务设置也提供归档和恢复入口。

CLI：在源项目目录运行：

```sh
seekclaw exec --worktree --base-ref HEAD --mode edit "实现并测试这个修复"
seekclaw worktree list
seekclaw worktree create <session-id> --base-ref main
seekclaw worktree archive <session-id>
seekclaw worktree restore <session-id>
```

每个源工作区与对话 ID 的组合对应一个 detached worktree，默认存放在 `~/.seekclaw/worktrees/`。未提交的源目录改动不会复制。Git 中未跟踪的本地提示、Skills 和 MCP 配置可从源目录回退读取，无须复制凭据。

归档先用临时 Git index 保存已跟踪和非忽略的未跟踪文件，创建快照提交，写入 `refs/seekclaw/worktrees/<id>`，然后移除 checkout。恢复会把这些文件作为快照提交的内容还原；原暂存区分组不会恢复。归档会拒绝含忽略文件、子模块、嵌套仓库、活动命令或活动子 Agent 的 checkout，避免丢失无法由该快照保存的数据。

删除对话或项目之前须先归档其中的 worktree 并停止活动任务。对话删除后，仍可用原 session ID 恢复已经保存的 worktree 文件。对话的 fork 使用源项目；需要隔离时为新对话显式创建 worktree。

worktree 提供 Git 工作目录隔离。命令仍按当前用户和任务权限运行，没有增加操作系统沙箱。若 checkout 被外部移动、替换或损坏，系统会报错并保留注册信息供检查。

## 可持续交互的命令会话

`command_session` 工具提供 `start`、`read`、`write`、`list`、`terminate`、`close`。句柄属于对话，能够跨工具调用和后续轮次继续使用。

```json
{"action":"start","command":"python -u script.py","pty":false,"timeout_seconds":1800}
```

```json
{"action":"read","session_id":"<returned-sessionId>","cursor":0,"wait_ms":1000}
```

```json
{"action":"write","session_id":"<returned-sessionId>","text":"yes\n","eof":false}
```

输出按字符游标分页。每次读取使用上次返回的 `cursor`；`hasMore=true` 表示仍需读取，即使进程已经退出。`endCursor` 是总输出位置；`truncated=true` 表示请求位置之前的输出已超出保留窗口。默认每页最多 16 Ki 字符，工具还会根据上下文预算缩小页面并调整游标，保持 JSON 结果完整。

每个运行时最多保留 32 个会话，每个会话最多保留最近 128 Ki 字符输出。进程最长运行一天，默认 30 分钟；完成且闲置超过 15 分钟的句柄在下次启动命令时回收。`terminate` 停止进程树，`close` 同时释放句柄。Windows PTY 使用 ConPTY；Linux/macOS 使用平台的 `script` 工具。普通管道模式不依赖 PTY。

Desktop 任务设置中的“命令会话与子任务”支持读取输出、发送输入、关闭输入和停止命令。命令依赖运行时进程存活，关闭 Daemon/CLI 会清理其进程树；重启后不能恢复原进程的 stdin 或输出缓冲。

## Skills 按需加载

系统提示只公布已启用技能的名称和描述。模型使用 `load_skill` 读取所需指令和支持文件：

```json
{"name":"review-code"}
```

```json
{"name":"review-code","file":"references/checklist.md"}
```

支持两种格式：

- 标准 `SKILL.md`，可包含 YAML frontmatter 的 `name`、`description`，并保留其他元数据字段的兼容性。
- 现有 `skill.yaml` / `skill.yml` 与 `prompt.txt`，包括 manifest 指定的相对 prompt 路径。

例如：

```markdown
---
name: review-code
description: 检查变更中的正确性、回归与测试覆盖
---
先读取 references/checklist.md，再执行检查流程。
```

发现范围包括全局 `~/.seekclaw/skills/`、项目 `.claude/skills/`、`.agents/skills/` 和原有 `skills/` / `.seekclaw/skills/`。同名技能由项目原生目录覆盖兼容目录，项目技能覆盖全局技能。worktree 可回退到源目录的本地技能。

导入 `SKILL.md` 会保留其整个目录中的脚本和引用；ZIP 导入也识别该格式。替换技能先完成暂存复制，再替换已安装目录，复制失败保留旧版本。导入拒绝符号链接/重解析点，加载拒绝越界路径。单文件上限 512 KiB，发现阶段仅解析有界元数据，不向系统提示注入技能正文。

## MCP 认证与完整结果

远程 MCP 支持静态 `headers`、从环境变量取值的 `envHeaders`、`bearerTokenEnv`，以及 OAuth。环境变量必须存在于运行 CLI/Daemon 的进程环境中。

```json
{
  "mcp": {
    "servers": {
      "remote": {
        "transport": "http",
        "url": "https://example.com/mcp",
        "bearerTokenEnv": "MCP_TOKEN",
        "envHeaders": { "X-Api-Key": "MCP_API_KEY" }
      },
      "oauth-service": {
        "transport": "http",
        "url": "https://service.example/mcp",
        "oauth": {
          "clientId": "registered-client-id",
          "scope": "tools",
          "authorizationEndpoint": "https://service.example/authorize",
          "tokenEndpoint": "https://service.example/token"
        }
      }
    }
  }
}
```

OAuth 端点可省略，客户端会尝试受保护资源与授权服务器元数据发现。未配置 client ID 时，授权服务器需支持动态注册。机密客户端可通过 `oauth.clientSecretEnv` 引用客户端密钥。

```sh
seekclaw mcp login oauth-service
seekclaw mcp login oauth-service --no-browser
seekclaw mcp logout oauth-service
```

Desktop 的 MCP 页面提供登录/退出入口，编辑器支持环境变量引用与 OAuth client ID。OAuth 使用 PKCE、随机 state、十分钟 loopback 回调监听和自动刷新。错误 state、重复参数、非匹配回调和跨源凭据转发会被拒绝。认证端点须使用 HTTPS，loopback HTTP 除外。刷新和凭据写入使用跨进程租约；本地退出与正在进行的刷新串行化，并取消本实例未完成的登录。

OAuth 凭据与项目配置分开保存：Windows 使用当前用户 DPAPI，Unix 使用 `0700` 目录和 `0600` 文件。管理协议不会返回 access token、refresh token 或静态 header 值。退出清除本地 OAuth 凭据，不撤销远端令牌，也不清除环境变量提供的令牌。

MCP 结果保留 `content`、`structuredContent`、`_meta` 和未知内容块，经过工具结果、会话存储及 Daemon 返回仍可完整读取。文本和支持的图片另行投影给模型；音频、嵌入资源、资源链接和结构化结果可在 Desktop 展开查看。图片投影受数量和字节预算限制。单条协议消息/HTTP JSON 响应限制为 16 Mi 字符，OAuth JSON 响应限制为 1 Mi 字符。HTTP 请求使用协商后的 `MCP-Protocol-Version`，支持 `2024-11-05`、`2025-03-26` 和 `2025-06-18`。

## 子 Agent 生命周期

`subagent` 工具提供 `spawn`、`list`、`status`、`wait`、`send`、`cancel`：

```json
{"action":"spawn","role":"coder","task":"实现模块 A 并运行相关测试","worktree":true}
```

```json
{"action":"wait","id":"<task-id>","wait_ms":1000}
```

```json
{"action":"send","id":"<task-id>","task":"根据刚才的结果补充边界处理"}
```

任务 ID、子对话 ID、状态和结果持久化，`send` 在同一子对话中延续历史。任务最多并发 8 个，等待最多 60 秒，取消使用独立令牌。完成或取消后可再发指令；运行中需要先等待或取消。原有 `invoke_subagent` 保留同步兼容入口。

子 Agent 继承父任务的网络和审批上限，角色工具白名单在 MCP 注册后执行过滤。researcher 只读；coder 和 tester 可以在父任务允许的范围内编辑或运行命令。tester 的命令可能产生构建文件。子 Agent 不能继续委派。父轮次结束后，子任务仍可执行并请求审批；Daemon 通过 `subagent.updated` 发布最终状态，Desktop 可查看结果与取消活动子任务。

关闭进程会取消并等待活动子任务。异常退出后，旧 PID 与创建时间失配的任务显示为 `interrupted`，可以用 `send` 继续其已保存的上下文。程序不会复活原来的进程或自动重放未完成的工具调用。

## 延续上下文的自动化

Desktop 计划任务编辑器可以选择原项目中的已有对话，启用 worktree，并选择通知策略。Agent 可以使用 `schedule` 创建后续检查：

```json
{
  "action":"upsert",
  "name":"跟进构建",
  "prompt":"检查构建进展，仅在状态有意义地改变或需要处理失败时报告；完成后停止跟进。",
  "cron":"*/30 * * * *",
  "continue_context":true,
  "notification":"changes"
}
```

`continue_context` 默认 `true`，绑定当前对话的源工作区和历史。设为 `false` 时每次创建新对话。`worktree=true` 可为绑定对话创建独立 checkout；已有 worktree 会继续使用。Cron 是宿主本地时区的五段表达式。

通知策略为 `changes`（新建工具/UI 任务的默认值）、`failures` 或 `always`。无变化时模型可精确返回 `[NO_CHANGE]`；重复结果和无变化结果只产生 `schedule.changed` 刷新事件，不产生完成通知。旧任务保持 `always`，数据库采用增量迁移。

同一计划任务不会重复排队，前台和自动化访问同一对话时通过对话租约串行执行。`pause`、`resume`、`complete`、`delete` 可以管理生命周期，`complete` 停用后续触发。工作区或绑定对话不存在时，任务失败并保留错误，不会静默切换到其他目录。

计划任务由运行中的 Daemon 调度，没有注册系统级定时任务。无人值守执行使用任务现有权限；没有交互审批通道时，需要审批的工具会被拒绝。

## 面向脚本和 CI 的 CLI

`exec` 无需交互终端。默认仅向 stdout 写最终答案，进度和错误写 stderr；`--json` 切换为版本化 JSONL。

```sh
seekclaw exec --mode readonly "检查项目结构"
seekclaw exec --json --mode edit --worktree "完成修复并测试" > events.jsonl
seekclaw exec --resume <session-id> "继续处理测试失败"
seekclaw exec --continue "基于上一轮给出结论"
seekclaw exec --output-schema schema.json -o result.json "生成结构化结果"
```

管道输入示例（PowerShell）：

```powershell
Get-Content -Raw requirements.txt | seekclaw exec --json -
```

提示既可来自参数，也可来自 stdin；同时存在时 stdin 作为附加上下文。输入上限 4 Mi 字符。`--model provider/model` 显式覆盖模型，`--timeout-seconds` 默认为 1800，可设置 1–86400 秒。`--resume` 与 `--continue` 互斥。默认 `--mode guardrail`；需要交互批准的操作直接失败，不会等待终端输入。脚本可显式选择符合任务需要的权限模式。

`--output-schema` 在调用模型之前解析并建立 schema 约束；最终结果必须是可解析且符合 schema 的 JSON。仅成功时原子替换 `--output-last-message` / `-o` 指定的文件，失败不会覆盖旧文件。

| 退出码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `1` | 执行、审批或输出写入失败 |
| `2` | 参数、模型或输入 schema 错误 |
| `3` | 最终结果不符合输出 schema |
| `130` | 取消或超时 |

JSONL 每行包含 `version: 1` 和 `type`。正常任务从 `thread.started` 开始，输出 `turn.started`、消息、工具、文件和用量事件，最后为 `turn.completed` 或 `turn.failed`。参数或初始化阶段失败可能没有 `thread.started`；终止失败事件提供 `exitCode` 和 `error`。

模型响应断流时，按 `routing.retry` 的次数和退避配置重试当前模型请求，不重跑之前完成的工具。`provider.retry` 包含 `model`、`attempt`、`reason`、`delayMs`，其中 `attempt` 是失败请求的序号；若已有部分输出，下一次尝试前会发送 `message.reset`。消费者应从当前未完成回复末尾撤回 `textLength` 个 UTF-16 代码单元，再接收新的 `message.delta`；`thinkingLength` 表示同一尝试的思考内容长度。已经完成的消息和工具结果不受影响。重试耗尽后返回失败，并保存最后一次尝试的部分内容、`finishReason` 和 `errorMessage`。没有收到完整响应的工具调用不会执行。

```json
{"version":1,"type":"thread.started","sessionId":"...","workspace":"..."}
{"version":1,"type":"turn.completed","sessionId":"...","output":"done","exitCode":0,"error":null}
```

## Daemon 接口

新增能力通过 `protocol.info` 公布，使用现有 JSONL 请求/响应封装。`workspace` 指源项目绝对路径；全局对话使用 `global:true`。

| 方法 | 主要参数 |
| --- | --- |
| `session.new` | 原有参数，加 `worktree`、`baseRef` |
| `session.get` | `id`；结果增加 `executionWorkspace`、`worktree` 和工具完整内容 |
| `worktree.list` | `workspace` |
| `worktree.create/archive/restore` | `workspace`、`sessionId`；创建可传 `baseRef` |
| `command.list` | `workspace/global`、`sessionId` |
| `command.read` | 同上，加 `commandId`、`cursor`、`waitMs` |
| `command.write` | 同上，加 `commandId`、`text`、`closeInput` |
| `command.terminate/close` | 同上，加 `commandId` |
| `subagent.list` | `workspace/global`、`sessionId`（父对话） |
| `subagent.status/wait/cancel` | 同上，加 `taskId`；等待可传 `waitMs` |
| `mcp.auth.login` | `name`；返回 `state`、`authorizationUrl` |
| `mcp.auth.status` | `state`；返回 pending/completed/failed/cancelled |
| `mcp.auth.logout` | `name` |
| `schedule.create/update` | 原有参数，加 `sessionId`、`runInWorktree`、`notificationPolicy` |

命令启动与子 Agent 的 spawn/send 由 Agent 工具执行，保留原有任务权限和审批路径。管理接口仅操作经对话作用域校验的句柄，不接受任意操作系统 PID。

## 验证

```sh
dotnet test SeekClaw.slnx --no-restore
```

在 `seekclaw_desktop` 中运行：

```sh
pnpm test
pnpm build
```

自动化测试覆盖实际 Git checkout/快照、Windows 管道与 ConPTY 输入、进程取消与输出上限、技能兼容与导入回滚、OAuth PKCE/loopback/并发刷新、完整 MCP 结果持久化、父轮次销毁后的子任务审批、对话串行化、计划任务上下文与去重，以及 CLI JSONL、schema、审批和取消。LLM 行为使用确定性测试客户端，OAuth 使用模拟服务器与真实本地回调；这些测试不代表已验证所有远端提供商。
