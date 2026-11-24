# SeekClaw 与 Codex 的 Runtime 能力差距

本文记录 Agent 执行、会话与任务生命周期、工具协议及扩展接口的源码比较。SeekClaw 当前已有持续命令会话、任务 worktree、异步子 Agent、延续会话的定时任务、Skills 按需加载、MCP OAuth 与富结果，以及非交互 CLI。本轮补充持久会话目标、独立上下文检查点、子 Agent 的有界背景继承与运行中指导、进程内 MCP 连接复用和按需工具发现。具体实现与覆盖边界分别列在下文。

## 比较基线与判定方式

- 审查日期为 2026-11-24。
- SeekClaw 基线为提交 `0d4976d`，包含本轮工作区改动。
- Codex 基线为本地开源 checkout `E:\Project\TempProject\codex` 的提交 `af81d2770f`。线上 Desktop 与服务端版本不在本次审查范围内。
- “已有”表示存在可追踪的实现。“部分”表示核心路径已有，覆盖范围或生命周期存在差异。“缺少”表示审查范围内未找到等价实现。“不作差距判定”表示参考源码不足以支持结论。
- 本文以源码审查为主。R01 另包含本轮真实本地 runtime 实例的 PTY 交互验证；目标、检查点、子 Agent 协作、MCP 发现与桌面状态同步有对应回归测试，未将这些测试视作完整产品端到端验收。

文内 SeekClaw 链接相对于本项目。Codex 链接相对于当前本地 checkout 的位置，参考仓库移动后需要调整路径。

Codex 的 `Stage::Removed` 用于已无作用、为兼容保留的功能开关。具体能力是否存在由注册和调用路径决定。本基线中 `tool_search` 已始终启用，`MultiAgentMode` 是兼容空开关。[功能阶段与枚举说明](../../TempProject/codex/codex-rs/features/src/lib.rs)。

## 已有能力

| 能力 | SeekClaw 当前实现与边界 | 源码入口 |
| --- | --- | --- |
| 按任务隔离的 Git worktree | 按源仓库与会话定位隔离 checkout，包含子目录映射、租约、归档快照和恢复。 | [WorktreeManager](../seekclaw_runtime/Workspaces/WorktreeManager.cs)、[worktree CLI](../seekclaw_cli/Commands/WorktreeCommands.cs) |
| 持续交互命令 | 进程由共享管理器持有，跨 Agent 轮次保留。支持 stdin、输出游标、等待、终止、列表与 PTY，本轮已注册 `exec_command` / `write_stdin` 主入口。 | [CommandSessionManager](../seekclaw_runtime/Execution/CommandSessionManager.cs)、[命令入口](../seekclaw_runtime/Execution/ExecCommandTool.cs)、[输入入口](../seekclaw_runtime/Execution/WriteStdinTool.cs)、[Runtime 注册](../seekclaw_runtime/SeekClawRuntime.cs) |
| Skills 按需与格式兼容 | 系统上下文注入目录元数据，正文和支持文件通过工具加载。兼容 `SKILL.md` 与原有 YAML/提示词文件，支持启停和工作区覆盖。 | [SkillManager](../seekclaw_runtime/Skills/SkillManager.cs)、[LoadSkillTool](../seekclaw_runtime/Skills/LoadSkillTool.cs) |
| MCP 认证与富结果 | 已有 stdio、HTTP/SSE、请求头认证、OAuth discovery/PKCE/刷新，以及 content、structuredContent、元数据和媒体内容保留。 | [McpTransport](../seekclaw_runtime/Mcp/McpTransport.cs)、[McpAuthentication](../seekclaw_runtime/Mcp/McpAuthentication.cs)、[McpCredentialStore](../seekclaw_runtime/Mcp/McpCredentialStore.cs)、[McpCallResult](../seekclaw_runtime/Mcp/McpCallResult.cs) |
| MCP 连接与按需发现 | 按配置根、配置和认证指纹在进程内复用连接。`tool_search` 先列服务器元数据，再按查询加载有限工具 schema；新用户轮次重新激活所需 schema，保持权限过滤。 | [McpConnectionPool](../seekclaw_runtime/Mcp/McpConnectionPool.cs)、[McpManager](../seekclaw_runtime/Mcp/McpManager.cs)、[McpToolSearchTool](../seekclaw_runtime/Mcp/McpToolSearchTool.cs) |
| 子 Agent 生命周期 | 已有异步 spawn、list、status、wait、send/followup、cancel、独立会话、可选 worktree、进度事件和持久任务记录。支持选择性继承父会话背景；活跃子任务接收排队指导，空闲子任务在原会话续派。父轮次结束后子任务仍可运行。 | [SubAgentControlTool](../seekclaw_runtime/SubAgents/SubAgentControlTool.cs)、[SubAgentTaskManager](../seekclaw_runtime/SubAgents/SubAgentTaskManager.cs)、[SubAgentRunner](../seekclaw_runtime/SubAgents/SubAgentRunner.cs)、[上下文选择器](../seekclaw_runtime/SubAgents/SubAgentContextBuilder.cs) |
| 持久会话目标 | 每会话保存一个目标、完成条件、进度与暂停原因，提供 active/paused/complete 状态、有限续步和连接恢复、桌面卡片及原会话手动续跑。目标持久化不代表进程重启后自动执行。 | [SessionGoalStore](../seekclaw_runtime/Goals/SessionGoalStore.cs)、[GoalExecution](../seekclaw_runtime/Goals/GoalExecution.cs)、[GoalTool](../seekclaw_runtime/Goals/GoalTool.cs)、[桌面目标卡片](../seekclaw_desktop/src/renderer/src/components/ConversationGoal.vue) |
| 会话记录与上下文检查点 | 摘要及覆盖到的消息单独持久化。模型请求使用摘要和未覆盖尾部，原始消息继续供展示、fork 与恢复；回退时检查点随覆盖范围失效。 | [Agent.Context](../seekclaw_runtime/Agents/Agent.Context.cs)、[SessionStore](../seekclaw_runtime/Sessions/SessionStore.cs)、[会话数据库](../seekclaw_runtime/Data/SeekClawDatabase.cs) |
| 延续上下文的自动化 | `continue_context` 默认开启。定时任务可以使用原会话、独立 worktree 和通知策略，支持暂停、恢复、完成、删除和手动触发。 | [ScheduleTool](../seekclaw_runtime/Scheduling/ScheduleTool.cs)、[ScheduleService](../seekclaw_runtime/Scheduling/ScheduleService.cs)、[ScheduleStore](../seekclaw_runtime/Scheduling/ScheduleStore.cs) |
| 脚本与 CI CLI | 已有 stdin、JSONL、最后回复文件、schema 校验、resume/continue、worktree、超时和退出码处理。schema 约束层级见 R30。 | [ExecCommand](../seekclaw_cli/Commands/ExecCommand.cs)、[CLI 注册](../seekclaw_cli/Program.cs) |
| 会话模型固定与参数自动配置 | 已有会话模型绑定、旧会话回填和不可用模型错误。上下文/输出长度支持自动解析及默认值。 | [Agent](../seekclaw_runtime/Agents/Agent.cs)、[ModelCapabilityResolver](../seekclaw_runtime/Providers/ModelCapabilityResolver.cs)、[配置模型](../seekclaw_runtime/Configuration/SeekClawConfig.cs) |
| 计划、提问和运行中追加指令 | 已有 `update_plan`、`ask_user`，以及模型/工具步骤之间消费的 steering 队列。 | [PlanTool](../seekclaw_runtime/Tools/Builtin/PlanTool.cs)、[AskUserTool](../seekclaw_runtime/Tools/Builtin/AskUserTool.cs)、[AgentSteeringQueue](../seekclaw_runtime/Agents/AgentSteeringQueue.cs) |
| 会话浏览器与桌面操作 | 已有会话隔离的浏览器桥接、结构化快照、截图、交互工具与桌面操作扩展。 | [BrowserTool](../seekclaw_runtime/Browser/BrowserTool.cs)、[DesktopBrowserBridge](../seekclaw_runtime/Browser/DesktopBrowserBridge.cs)、[ComputerTools](../seekclaw_runtime/ComputerUse/ComputerTools.cs) |

## 一、命令执行、隔离与远端环境

### R01 持续命令接口与生命周期（部分）

SeekClaw 已注册 `exec_command` / `write_stdin`，复用共享的 `CommandSessionManager`。本轮通过真实本地 runtime 实例完成 PowerShell PTY `Read-Host` 往返，确认提示可见、stdin 可写、输出游标可续读、会话所有权校验及退出状态有效。管理器支持最多 32 个会话，保留 128 Ki 字符输出缓冲，命令生命周期默认 1800 秒、上限 86400 秒。[命令入口](../seekclaw_runtime/Execution/ExecCommandTool.cs)、[输入入口](../seekclaw_runtime/Execution/WriteStdinTool.cs)、[管理器](../seekclaw_runtime/Execution/CommandSessionManager.cs)、[共享服务](../seekclaw_runtime/Execution/RuntimeTaskServices.cs)。

Codex 的类型化执行参数还包括 `shell`、`login`、`max_output_tokens`、`sandbox_permissions`、`additional_permissions` 与 `prefix_rule`，执行管理器会发出 `TerminalInteraction` 事件。SeekClaw 当前主入口提供 cmd、workdir、tty、yield 与生命周期参数，结果通过字符预算和游标控制。两者的活跃进程句柄均由运行中的管理器持有，重启后的历史记录与活跃句柄恢复分别受各自实现约束。[SeekClaw 参数与结果格式](../seekclaw_runtime/Execution/CommandSessionTool.cs)、[Codex 执行参数](../../TempProject/codex/codex-rs/core/src/tools/handlers/unified_exec.rs)、[Codex 进程与终端事件](../../TempProject/codex/codex-rs/core/src/unified_exec/process_manager.rs)、[Codex 进程存储](../../TempProject/codex/codex-rs/core/src/unified_exec/mod.rs)。

### R02 Shell 环境快照（缺少）

SeekClaw 的持续 PTY 保留同一 shell 的交互状态，新的独立命令会重新启动 shell，尚未找到快照生成、失效和复用层。Codex 单独实现 shell snapshot，让后续执行复用捕获的环境。`ShellSnapshot` 为 Stable，`ShellSnapshotV2` 为 UnderDevelopment。[SeekClaw CommandProcess](../seekclaw_runtime/Execution/CommandProcess.cs)、[Codex shell_snapshot](../../TempProject/codex/codex-rs/core/src/unified_exec/shell_snapshot.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R03 操作系统级文件与进程沙箱（缺少）

SeekClaw 的路径校验、工具启停、只读模式和审批作用于应用层。获得执行权限的 shell 具有宿主账户允许的访问范围，Job Object/进程组负责进程树清理。Codex 有按平台实现的沙箱层及命令服务中的文件/进程沙箱集成。[SeekClaw WorkspacePathGuard/ToolContext](../seekclaw_runtime/Tools/ToolTypes.cs)、[进程树管理](../seekclaw_runtime/Tools/WindowsJobObject.cs)、[Codex sandboxing](../../TempProject/codex/codex-rs/sandboxing/src)、[exec-server](../../TempProject/codex/codex-rs/exec-server/src)、[Windows sandbox](../../TempProject/codex/codex-rs/windows-sandbox-rs)。

### R04 按目录、时限和作用域授予权限（部分）

SeekClaw 有七种 Agent 模式、单次工具审批和工作区路径边界。Codex 另外实现权限 profile 解析及 `request_permissions` 工具，覆盖当前 turn 或会话范围的追加授权。SeekClaw 当前未找到对等的授权对象与作用域组合。[SeekClaw AgentMode](../seekclaw_runtime/Agents/AgentMode.cs)、[ToolApproval](../seekclaw_runtime/Tools/ToolApproval.cs)、[Codex resolved_permission_profile](../../TempProject/codex/codex-rs/core/src/config/resolved_permission_profile.rs)、[permission_profile_catalog](../../TempProject/codex/codex-rs/core/src/config/permission_profile_catalog.rs)、[request_permissions](../../TempProject/codex/codex-rs/core/src/tools/handlers/request_permissions.rs)。

### R05 可持久化的命令审批规则（部分）

SeekClaw 审批结果主要作用于本次工具调用。Codex execpolicy 支持持久命令前缀规则、allow/prompt/forbidden、解释、匹配/不匹配样例及可执行文件路径元数据。Codex 文档将该 CLI/API 标为 preview。[SeekClaw ToolApproval](../seekclaw_runtime/Tools/ToolApproval.cs)、[Agent.Tools](../seekclaw_runtime/Agents/Agent.Tools.cs)、[Codex execpolicy 语义与限制](../../TempProject/codex/codex-rs/execpolicy/README.md)。

### R06 子进程网络边界（部分）

SeekClaw 联网开关过滤网络工具并传入工具上下文，shell 子进程使用宿主机的网络权限。Codex 有网络代理、子进程网络策略决策及权限协议层。[SeekClaw ActiveTools](../seekclaw_runtime/Agents/Agent.Prompting.cs)、[ToolContext](../seekclaw_runtime/Tools/ToolTypes.cs)、[Codex network-proxy](../../TempProject/codex/codex-rs/network-proxy)、[network_policy_decisions](../../TempProject/codex/codex-rs/exec-server/src/network_policy_decisions.rs)、[权限协议](../../TempProject/codex/codex-rs/app-server-protocol/src/protocol/v2/permissions.rs)。

### R07 远端执行环境协议（缺少，Codex 入口为 experimental）

SeekClaw 可以启动 SSH 命令，文件工具、命令工具和路径解析主要面向本机。Codex exec-server 抽象远端进程、文件系统与环境注册，其 CLI 入口标为 experimental。本项覆盖 Agent 工具执行环境，Desktop 完整远端工作流的可用性未在本次验证。[SeekClaw CommandProcess](../seekclaw_runtime/Execution/CommandProcess.cs)、[文件工具](../seekclaw_runtime/Tools/Builtin/FileTools.cs)、[Codex remote](../../TempProject/codex/codex-rs/exec-server/src/remote.rs)、[remote_process](../../TempProject/codex/codex-rs/exec-server/src/remote_process.rs)、[remote_file_system](../../TempProject/codex/codex-rs/exec-server/src/remote_file_system.rs)、[environment_registry](../../TempProject/codex/codex-rs/exec-server/src/environment_registry.rs)、[CLI](../../TempProject/codex/codex-rs/cli/src/main.rs)。

## 二、上下文、模型传输与登录

### R08 上下文预算与多模态核算（部分）

SeekClaw 已有系统提示预算、注入片段上限、历史裁剪、工具结果预算、成对保留工具调用和摘要压缩，核心 token 估算包含字符数近似。Codex 的 ContextManager 同样使用粗略估算，并额外维护已记录 usage 的边界、图片与工具音频的处理和预算。两者在媒体核算与已知 token 使用量的复用范围上存在差异。Codex `AsyncCompaction` 为 UnderDevelopment。[SeekClaw ContextPlanner](../seekclaw_runtime/Agents/ContextPlanner.cs)、[Agent.Context](../seekclaw_runtime/Agents/Agent.Context.cs)、[Codex History 与 token 估算](../../TempProject/codex/codex-rs/core/src/context_manager/history.rs)、[compact](../../TempProject/codex/codex-rs/core/src/compact.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R09 完整会话记录与模型有效上下文的分离（部分）

SeekClaw 本轮将压缩改为独立检查点：保存摘要、覆盖消息数及末条消息身份，模型请求组合检查点与未覆盖尾部，原始会话记录继续保留。同一个长回合也可压缩较早完成的步骤，用户指令、附件和近期工具调用保留在当前请求中。摘要按连续前缀分批生成，提交时验证覆盖范围仍有效；回退会清理越界检查点，fork 复制其保留消息范围内可用的检查点。已有原始历史保留、fork、回退与并发失效的回归覆盖。Codex 另外有独立 rollout、thread-store 与上下文管理层，两者的存储格式、事件回放和媒体预算不能据此视作完全等价。[SeekClaw Agent.Context](../seekclaw_runtime/Agents/Agent.Context.cs)、[SessionStore](../seekclaw_runtime/Sessions/SessionStore.cs)、[Codex rollout](../../TempProject/codex/codex-rs/rollout)、[thread-store](../../TempProject/codex/codex-rs/thread-store)、[context_manager](../../TempProject/codex/codex-rs/core/src/context_manager)。

### R10 Responses 与 WebSocket 模型传输（部分）

SeekClaw 的 OpenAI-compatible 客户端主要使用 Chat Completions，另有其他供应商适配。Codex 本基线使用 Responses 协议，并实现 Responses WebSocket 端点。传输差异涉及消息项、工具调用及请求状态管理，具体服务能力仍受供应商支持范围约束。[SeekClaw OpenAiCompatibleClient](../seekclaw_runtime/Providers/OpenAiCompatibleClient.cs)、[Codex model-provider-info](../../TempProject/codex/codex-rs/model-provider-info/src/lib.rs)、[responses](../../TempProject/codex/codex-rs/codex-api/src/endpoint/responses.rs)、[responses_websocket](../../TempProject/codex/codex-rs/codex-api/src/endpoint/responses_websocket.rs)。

### R11 模型账户登录与凭据生命周期（缺少等价账户层）

SeekClaw 支持供应商 API key 配置，OAuth 实现目前位于 MCP 认证层。Codex 模型账户层还覆盖浏览器登录、设备码登录、账户 grant 管理、令牌刷新、凭据存储与登录状态协调。这些账户能力包含特定供应商与服务端约束。[SeekClaw ProviderConfig](../seekclaw_runtime/Configuration/SeekClawConfig.cs)、[MCP 凭据](../seekclaw_runtime/Mcp/McpCredentialStore.cs)、[Codex login](../../TempProject/codex/codex-rs/login/src/lib.rs)、[auth manager](../../TempProject/codex/codex-rs/login/src/auth/manager.rs)、[auth storage](../../TempProject/codex/codex-rs/login/src/auth/storage.rs)、[device_code_auth](../../TempProject/codex/codex-rs/login/src/device_code_auth.rs)。

## 三、工具发现、Skills、MCP 与扩展

### R12 工具 schema 按需发现（部分）

SeekClaw 按权限筛选并直接提供内置工具定义。MCP 工具先通过 `tool_search` 查询，匹配的完整 schema 随本轮后续模型请求发送。执行时再次校验该请求是否提供了所调用的 MCP 工具定义。Codex 在所比较的源码版本中也有延迟工具暴露、搜索、定义加载及后续路由。[SeekClaw ActiveTools](../seekclaw_runtime/Agents/Agent.Prompting.cs)、[McpManager](../seekclaw_runtime/Mcp/McpManager.cs)、[Codex tool_search](../../TempProject/codex/codex-rs/core/src/tools/handlers/tool_search.rs)、[mcp_tool_exposure](../../TempProject/codex/codex-rs/core/src/mcp_tool_exposure.rs)、[功能定义](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R13 Skills 元数据、调用策略与依赖（部分）

SeekClaw 元数据主要包含名称、描述、版本与正文位置，已兼容基础 `SKILL.md` 加载。Codex 还定义界面展示名、图标、默认提示、隐式调用策略、产品约束、MCP 工具依赖和插件归属，并有 MCP 依赖安装路径。`SkillMcpDependencyInstall` 为 Stable。[SeekClaw SkillManifest/SkillManager](../seekclaw_runtime/Skills/SkillManager.cs)、[Codex SkillMetadata](../../TempProject/codex/codex-rs/skills/src/model.rs)、[mcp_skill_dependencies](../../TempProject/codex/codex-rs/core/src/mcp_skill_dependencies.rs)、[Skills 加载](../../TempProject/codex/codex-rs/skills/src/loading.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R14 用户可安装的插件包（缺少）

SeekClaw 的 `IRuntimeExtension` 和 `ExtensionManager` 负责内置能力初始化、工具注册与释放，默认发现对象来自当前程序集。Codex 插件包具有身份、命名空间、资源组合及安装/发现流程，可组合 skills、MCP 等资源。SeekClaw 当前未找到等价的外部插件包加载层。[SeekClaw IRuntimeExtension](../seekclaw_runtime/Extensions/IRuntimeExtension.cs)、[ExtensionManager](../seekclaw_runtime/Extensions/ExtensionManager.cs)、[Codex plugin](../../TempProject/codex/codex-rs/plugin)、[core-plugins](../../TempProject/codex/codex-rs/core-plugins)、[CLI 插件入口](../../TempProject/codex/codex-rs/cli/src/main.rs)。

### R15 用户可配置的生命周期 Hooks（缺少）

SeekClaw 内部扩展接口提供初始化、工具注册与释放，当前未暴露用户配置式的模型、工具、压缩及子 Agent 生命周期钩子。Codex Hooks 定义 14 种事件，包含模型请求前后、工具调用前后、权限请求、压缩前后、会话启停、用户输入、子 Agent 启停、Stop 和 Interrupt，并有配置解析与分发。`CodexHooks` 为 Stable。[SeekClaw 扩展接口](../seekclaw_runtime/Extensions/IRuntimeExtension.cs)、[Agent.Tools](../seekclaw_runtime/Agents/Agent.Tools.cs)、[Codex Hooks](../../TempProject/codex/codex-rs/hooks/src/lib.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R16 MCP 连接共享、延迟启动与工具发现（部分）

SeekClaw 本轮由 `RuntimeTaskServices` 持有进程级 `McpConnectionPool`，跨 turn 与子 Agent runtime 复用连接；各 runtime 只拥有本地工具注册。连接按配置根、服务器、配置和凭据指纹区分，配置移除/禁用、认证变更、显式 reload 或空闲清理可使连接失效。普通 turn 使用 `PrepareAsync` 更新注册而不启动服务器。`tool_search` 的空查询只列服务器元数据；指定服务器或关键词后按批连接，最多检查 8 个服务器，默认加载 5 个、最多 12 个工具 schema。每个用户轮次重新激活 schema，工具执行仍经过角色工具白名单、模式、联网和禁用规则。

显式管理 reload 强制重连并刷新目录；发现阶段可重建断开的连接，远程工具调用不自动重试。连接与进程句柄不会跨 daemon 重启保留，也未实现服务器工具变更通知后的自动目录刷新（见 R18）。Codex 有共享连接与 stdio 延迟启动能力，`McpConnectionSharing` 和 `McpLazyStdioStartup` 均为 Stable；其更完整的动态发现行为不在本轮等价声明范围内。[SeekClaw McpManager](../seekclaw_runtime/Mcp/McpManager.cs)、[McpConnectionPool](../seekclaw_runtime/Mcp/McpConnectionPool.cs)、[McpToolSearchTool](../seekclaw_runtime/Mcp/McpToolSearchTool.cs)、[RuntimeTaskServices](../seekclaw_runtime/Execution/RuntimeTaskServices.cs)、[Codex MCP](../../TempProject/codex/codex-rs/core/src/mcp.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R17 MCP 资源与分页（部分）

SeekClaw 已有 `tools/list`、`prompts/list`/`prompts/get` 和 `resources/list`，当前请求路径未遍历游标分页，也未提供资源模板发现及资源正文读取工具。Codex 有资源列表、资源模板和资源读取的独立工具处理器。[SeekClaw McpClient](../seekclaw_runtime/Mcp/McpClient.cs)、[Codex 资源列表](../../TempProject/codex/codex-rs/core/src/tools/handlers/mcp_resource/list_mcp_resources.rs)、[资源模板](../../TempProject/codex/codex-rs/core/src/tools/handlers/mcp_resource/list_mcp_resource_templates.rs)、[资源读取](../../TempProject/codex/codex-rs/core/src/tools/handlers/mcp_resource/read_mcp_resource.rs)。

### R18 MCP 双向请求与通知（缺少）

SeekClaw dispatch loop 忽略无 id 的服务器通知，主要处理客户端请求后的响应，尚未找到 elicitation 等服务器到客户端请求的路由。Codex RMCP 与 app-server 协议包含对应交互，`ToolCallMcpElicitation` 为 Stable。`McpToolListChanged`、`MultiEnvironmentMcp` 和更新协议实验项分别受独立开关控制。[SeekClaw McpClient](../seekclaw_runtime/Mcp/McpClient.cs)、[Codex rmcp-client](../../TempProject/codex/codex-rs/rmcp-client)、[MCP 协议](../../TempProject/codex/codex-rs/app-server-protocol/src/protocol/v2/mcp.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

## 四、子 Agent、目标、规划与自动化

### R19 子 Agent 继承父上下文与多层任务树（部分）

SeekClaw 创建独立子会话并继承权限上限。本轮默认按相关性选取父会话的用户要求、摘要、进度与工具结果，并允许 `inherit_context=false` 或显式 `context`。选择器最多检查近期 128 条消息及最初用户要求，保留至多 10 段、总长至多 12,000 字符，不复制私有思考、签名、图片或完整工具参数。它是有界背景摘录，不是完整历史 fork。当前委派深度仍限制为一层。子会话沿用原项目存储作用域，执行目录可为父 checkout 或独立 worktree。Codex spawn schema 支持完整历史继承或仅初始提示，并通过任务路径管理 Agent 树。`MultiAgentV2` 为 Stable，默认关闭。[SeekClaw 上下文选择器](../seekclaw_runtime/SubAgents/SubAgentContextBuilder.cs)、[SubAgentRunner](../seekclaw_runtime/SubAgents/SubAgentRunner.cs)、[SubAgentControlTool](../seekclaw_runtime/SubAgents/SubAgentControlTool.cs)、[Codex multi_agents_spec](../../TempProject/codex/codex-rs/core/src/tools/handlers/multi_agents_spec.rs)、[Agent control](../../TempProject/codex/codex-rs/core/src/agent/control.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R20 运行中的子 Agent 消息与任务续派（部分）

SeekClaw 的 `send` 与 `followup` 别名现在按状态分流：活跃子 Agent 将指导加入容量 32 的队列，在下一模型边界、或当前整批工具结果保存后消费；空闲子 Agent 从持久记录恢复原会话并启动新轮次。`instructionQueued` 表示已接收，不表示已完成；连接失败或取消前尚未消费的已接收指导会保存供续跑。`cancel` 负责中止，`wait` 保留独立等待语义。桌面只读子会话展示已消费指导并与保存历史去重。Codex 分别提供 `send_message`、`followup_task` 与 `interrupt_agent`；SeekClaw 当前没有与这些入口逐项独立对应的消息协议，也没有任意深度的 Agent 树。[SeekClaw SubAgentTaskManager](../seekclaw_runtime/SubAgents/SubAgentTaskManager.cs)、[SubAgentRunner.SendAsync](../seekclaw_runtime/SubAgents/SubAgentRunner.cs)、[AgentSteeringQueue](../seekclaw_runtime/Agents/AgentSteeringQueue.cs)、[桌面子会话](../seekclaw_desktop/src/renderer/src/subagent-conversation.ts)、[Codex 多 Agent 工具定义](../../TempProject/codex/codex-rs/core/src/tools/handlers/multi_agents_spec.rs)、[v2 实现](../../TempProject/codex/codex-rs/core/src/tools/handlers/multi_agents_v2.rs)。

### R21 持久目标与自动延续（部分）

SeekClaw 本轮新增每会话一个持久目标，保存 objective、criteria、progress、pauseReason 及 active/paused/complete 状态。`session_goal` 提供创建、读取、检查点、暂停与完成；仅前台主 Agent 可管理，工具说明要求用户明确授权长任务。活跃目标可有限追加执行步数、对暂时模型连接故障进行有限恢复；没有新检查点的重复文本收尾会暂停，文本答复本身不会完成目标。完成请求经过最终回复及适用的工作区验证后才写入完成状态。验证失败时保留已记录的进度，继续修复或暂停。取消和异常保留目标供续跑。

Desktop 展示目标卡片，支持暂停与沿原会话历史恢复；daemon `chat` 的 `resumeGoal` 及交互 CLI `/goal resume` 进入同一路径。普通聊天不会自动重启已暂停目标，fork 不复制目标，截断历史会暂停并清空目标检查点。本轮没有 token/耗时目标预算、blocked/usage_limited/budget_limited 状态，也没有进程重启后自主恢复工作的调度服务。Codex Goals 记录 objective、状态、token 使用与时间，包含上述更细状态，并有恢复与自动延续服务。`Goals` 为 Stable/default true，独立的 `TokenBudget` 开关为 UnderDevelopment。[SeekClaw SessionGoalStore](../seekclaw_runtime/Goals/SessionGoalStore.cs)、[GoalExecution](../seekclaw_runtime/Goals/GoalExecution.cs)、[GoalTool](../seekclaw_runtime/Goals/GoalTool.cs)、[Agent](../seekclaw_runtime/Agents/Agent.cs)、[桌面卡片](../seekclaw_desktop/src/renderer/src/components/ConversationGoal.vue)、[Codex ThreadGoal](../../TempProject/codex/codex-rs/state/src/model/thread_goal.rs)、[goal extension](../../TempProject/codex/codex-rs/ext/goal/src/lib.rs)、[目标 API](../../TempProject/codex/codex-rs/app-server/src/request_processors/thread_goal_processor.rs)、[功能阶段](../../TempProject/codex/codex-rs/features/src/lib.rs)。

### R22 协作式 Plan 模式（部分）

SeekClaw 有只读 Plan 模式和 `update_plan` 清单工具。Codex 对话式 Plan 模式包含探索、澄清与最终结构化方案的交互契约，`update_plan` 清单工具在该模式不可用。Codex collaboration mode 列表协议仍带 experimental 注释。[SeekClaw AgentMode](../seekclaw_runtime/Agents/AgentMode.cs)、[PlanTool](../seekclaw_runtime/Tools/Builtin/PlanTool.cs)、[Codex Plan 模板](../../TempProject/codex/codex-rs/collaboration-mode-templates/templates/plan.md)、[Plan handler](../../TempProject/codex/codex-rs/core/src/tools/handlers/plan.rs)、[协作模式协议](../../TempProject/codex/codex-rs/app-server-protocol/src/protocol/v2/collaboration_mode.rs)。

### R23 结构化澄清问题（部分）

SeekClaw `ask_user` 支持一个问题、选项展示和等待回答。Codex 问题协议还包含稳定 question id、分组标题、选项说明、多问题与自由补充选项，提供更完整的答案映射数据。[SeekClaw AskUserTool](../seekclaw_runtime/Tools/Builtin/AskUserTool.cs)、[Codex request_user_input_spec](../../TempProject/codex/codex-rs/core/src/tools/handlers/request_user_input_spec.rs)。

### R24 延续会话的定时任务（已有，Desktop 完整对比不作判定）

SeekClaw 定时任务支持原会话延续、worktree、去重、执行超时、后台手动触发及通知策略。cron 使用宿主机本地时区，任务执行依赖 daemon 存活。本次 Codex 开源 checkout 未提供足够证据还原完整 Desktop heartbeat/cron 调度服务，对该产品流程保留比较边界。[SeekClaw ScheduleTool](../seekclaw_runtime/Scheduling/ScheduleTool.cs)、[ScheduleService](../seekclaw_runtime/Scheduling/ScheduleService.cs)、[ScheduleCron](../seekclaw_runtime/Scheduling/ScheduleCron.cs)、[ScheduleModels](../seekclaw_runtime/Scheduling/ScheduleModels.cs)。

## 五、会话操作、审查与开发者接口

### R25 会话操作的 CLI 暴露面（部分）

SeekClaw 后端已有 fork、截断、归档、删除和模型绑定，CLI 以列出会话、resume/continue 为主，部分 Desktop/daemon 操作尚无脚本命令。Codex CLI 对 fork、archive/unarchive、queue 与 review 有专门入口。[SeekClaw DaemonAdminApi](../seekclaw_runtime/Daemon/DaemonAdminApi.cs)、[CLI Program](../seekclaw_cli/Program.cs)、[ManagementCommands](../seekclaw_cli/Commands/ManagementCommands.cs)、[Codex CLI](../../TempProject/codex/codex-rs/cli/src/main.rs)。

会话回滚需要按本基线单独判断。Codex `ThreadRolledBack` 仅保留旧记录回放，源码明确标注 live rollback 不受支持。SeekClaw 会话截断修改消息历史，文件系统撤销属于另一个执行层面。[SeekClaw TruncateSession](../seekclaw_runtime/Daemon/DaemonAdminApi.cs)、[Codex ThreadRolledBack 注释](../../TempProject/codex/codex-rs/protocol/src/protocol.rs)。

### R26 专门的代码审查任务与结构化发现（部分）

SeekClaw 有代码审查工作流提示词，当前未找到等价的专用审查目标协议、任务执行和结构化 findings。Codex 支持未提交变更、基线分支、指定 commit 和自定义目标，并定义专门的审查任务与结果类型。[SeekClaw 审查提示词](../seekclaw_cli/prompts/workflow/code-review.txt)、[Codex ReviewTask](../../TempProject/codex/codex-rs/core/src/tasks/review.rs)、[Review 协议](../../TempProject/codex/codex-rs/app-server-protocol/src/protocol/v2/review.rs)、[review_format](../../TempProject/codex/codex-rs/protocol/src/review_format.rs)。

### R27 类型化、可生成的 Runtime 协议（部分）

SeekClaw 有本地 daemon 协议版本、请求与事件封装及前端类型定义，后台大量方法仍以 `JsonObject` 和 JSON 字符串传递参数与结果，跨层类型主要手工维护。Codex app-server-protocol 为请求、响应与通知定义明确类型，支持生成 TypeScript 与 JSON Schema，并区分实验字段。[SeekClaw DaemonServer](../seekclaw_runtime/Daemon/DaemonServer.cs)、[DaemonAdminApi](../seekclaw_runtime/Daemon/DaemonAdminApi.cs)、[前端 IPC](../seekclaw_desktop/src/shared/ipc.ts)、[Codex common protocol](../../TempProject/codex/codex-rs/app-server-protocol/src/protocol/common.rs)、[v2 类型](../../TempProject/codex/codex-rs/app-server-protocol/src/protocol/v2)、[schema 生成入口](../../TempProject/codex/codex-rs/cli/src/main.rs)。

### R28 远端 app-server 客户端（缺少，参考入口有实验性限制）

SeekClaw daemon 主要通过 Windows named pipe/Unix socket 服务本机客户端。Codex 远端 app-server 客户端允许非交互 exec 接入现有服务端，并使用其配置和凭据。当前 remote exec 仅支持 prompt、按 UUID resume 与输出选项，模型、worktree、图片和 schema 等配置由服务端范围约束。此项比较客户端接入远端 Agent 服务，R07 比较工具的远端执行环境。[SeekClaw DaemonServer](../seekclaw_runtime/Daemon/DaemonServer.cs)、[Codex exec remote](../../TempProject/codex/codex-rs/exec/src/remote.rs)、[app-server-client](../../TempProject/codex/codex-rs/app-server-client)、[CLI 实验入口](../../TempProject/codex/codex-rs/cli/src/main.rs)。

### R29 对外 TypeScript/Python SDK（缺少独立封装）

SeekClaw 的 .NET runtime API、Desktop 内部 bridge 与 CLI 已可供集成，当前未找到独立的 TypeScript/Python 客户端包。Codex 仓库提供两种语言的 SDK 源码，封装会话、事件与执行调用。[SeekClaw Runtime 组合根](../seekclaw_runtime/SeekClawRuntime.cs)、[Daemon 协议](../seekclaw_runtime/Daemon/DaemonServer.cs)、[Codex TypeScript SDK](../../TempProject/codex/sdk/typescript/src)、[Python SDK](../../TempProject/codex/sdk/python/src)。

### R30 CLI 最终输出的模型级 schema 约束（部分）

SeekClaw `--output-schema` 解析 JSON Schema，将要求加入 prompt，并对最终输出执行本地校验，模型生成不合规文本时会在该层失败。Codex exec CLI 将最终输出 schema 通过运行时传入模型请求。差异位于供应商请求层的约束方式。[SeekClaw ExecCommand](../seekclaw_cli/Commands/ExecCommand.cs)、[OpenAI-compatible 客户端](../seekclaw_runtime/Providers/OpenAiCompatibleClient.cs)、[Codex exec 参数](../../TempProject/codex/codex-rs/exec/src/cli.rs)、[exec 运行路径](../../TempProject/codex/codex-rs/exec/src/lib.rs)、[模型请求构造](../../TempProject/codex/codex-rs/core/src/client.rs)、[Responses 请求](../../TempProject/codex/codex-rs/codex-api/src/endpoint/responses.rs)。

## 实验阶段与来源边界

| 领域 | Codex 本基线的证据 | 比较范围 |
| --- | --- | --- |
| JavaScript Code Mode / 工具编排 | `CodeMode` 为 UnderDevelopment/default false。host、输出裁剪与中断等子能力分别有独立阶段。 | SeekClaw 当前未见等价的持久脚本工具环境。[功能定义](../../TempProject/codex/codex-rs/features/src/lib.rs)、[code-mode-host](../../TempProject/codex/codex-rs/code-mode-host)。 |
| Agent 共享讨论板 | `AgentMessageBoard` 与 `AgentMessageBoardMcp` 为 UnderDevelopment/default false。 | 属于实验协作能力。[功能定义](../../TempProject/codex/codex-rs/features/src/lib.rs)。 |
| 异步压缩、工具 world state、执行器发现 | 这些能力使用独立功能开关，成熟度分别定义。 | 按具体开关记录阶段。[功能定义](../../TempProject/codex/codex-rs/features/src/lib.rs)。 |
| 实时语音与 handoff | 开源仓库有实时会话、侧信道和交接代码，旧 realtime 开关已无作用。 | SeekClaw 当前未见等价双向实时语音运行时，完整可用性依赖模型、服务端与客户端集成。[realtime_conversation](../../TempProject/codex/codex-rs/core/src/realtime_conversation.rs)、[realtime_context](../../TempProject/codex/codex-rs/core/src/realtime_context.rs)。 |
| 浏览器、Computer Use、产物预览与 Desktop 自动化 | 开源仓库可见协议、能力开关与工具桥接，Desktop UI 和部分后端由其他实现提供。 | SeekClaw 已有浏览器、桌面工具与产物展示，本次参考源码覆盖范围不足以比较完整产品体验。[SeekClaw 产物工具](../seekclaw_runtime/Presentation/PresentArtifactTool.cs)、[Codex 功能定义](../../TempProject/codex/codex-rs/features/src/lib.rs)。 |

## 本轮完成范围与后续优先级

R09 已补充独立上下文检查点，R12/R16 已补充进程内 MCP 复用与按需发现，R19/R20 已补充子 Agent 的选择性背景继承与运行中指导，R21 已补充可保存、暂停和手动续跑的会话目标。这些条目继续标为“部分”，表示下列更深能力仍有边界，不应继续把本轮已实现的基础功能列为缺失。

1. **长任务的预算与服务级恢复（R21）**：补充目标级 token/耗时限制、明确的阻塞与限额状态，以及进程重启后的恢复策略。当前目标自动续步和连接重试均有限，继续原任务依赖仍在运行的轮次或用户主动恢复。
2. **MCP 资源和动态协议（R17/R18）**：资源读取、模板、分页、服务器通知及双向请求仍需独立设计；连接池与 `tool_search` 不代表这些协议能力已经完成。
3. **更完整的委派协议（R19/R20）**：按需考虑完整历史继承、多层任务树，以及独立区分仅投递消息、续派和中断。当前有界摘录、单层委派和 send/followup 合并入口已有明确用途与限制。
4. **运行环境与集成边界（R02–R07、R27–R30）**：Shell 环境快照、操作系统沙箱、远端执行、类型化协议、独立 SDK 和模型级输出 schema 继续按实际需求推进。本轮会话目标、工作目录隔离或工具发现不改变这些边界。

后续验收应分别验证“原始记录保留”“有效模型上下文”“活跃进程/连接”和“可恢复任务记录”，避免将数据已保存等同于后台进程在重启后仍会继续执行。
