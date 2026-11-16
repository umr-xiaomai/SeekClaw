# 设计精简审查（2026-11-16）

审查范围为 Desktop 会话、设置、配置与 Agent 执行链。以下为尚未改动的建议，按优先级排列；“没有消费逻辑”指本仓库中仅有定义、默认赋值或展示，未发现运行路径读取。外部配置兼容仍需迁移，不能只删字段。

| 优先级 | 机制或设计 | 现有证据与影响 | 建议 |
| --- | --- | --- | --- |
| 高 | 配置异常处理绑定“重建数据库” | `DaemonAdminApi.RebuildConfigAndDatabase` 同时调用 `ConfigStore.Reset` 和 `Database.Rebuild`；`ConfigAnomalyDialog` 的恢复按钮直接连接 `App.handleRebuildConfig`。修复 JSON 配置不应连带清空无关会话数据。 | 分离“恢复配置”和“重置全部数据”；异常默认操作保留数据库，把全量重置收进高级危险操作区。 |
| 高 | 把静态展示项当成已配置插件 | `CustomizationPage.PUBLIC_PLUGINS` 将 Webhook Alerts 等标为 `isSystem`；`isPluginConfigured` 对系统项无条件返回 true，`addPublicPlugin` 直接返回。本次未在 Runtime/CLI 找到 Webhook 实现。 | 市场模板、内置工具与实际已连接 MCP 分开表达；状态从 Runtime 能力和连接结果派生，尚未实现的项目移除或明确标注。 |
| 中 | 暴露不生效的配置与遗留状态 | `RoutingConfig.LoadBalance` 只在定义和默认配置中出现；声明的轮询、最低成本等策略没有执行分支。`ModelCapabilities.Streaming / JsonMode / Embedding` 仅用于定义或展示；`RuntimeState.RoundRobinCursors / LastSessionId` 未发现消费者。 | 移除无效设置和宣传，或实现完整执行语义；旧 JSON 字段可兼容读取，但不要继续作为有效能力展示。不要把 CLI 的 `exec --json` 与模型 `JsonMode` 混为一谈。 |
| 中 | 扩展管理重复维护 | `SettingsDialog` 管理 MCP 与技能，`CustomizationPage` 再次请求同样的管理接口，另有 `OfficialSkillsDialog` 市场。两套列表、刷新与错误处理容易出现状态不一致。 | 保留一个扩展工作台和共享数据层；其他入口直接定位对应标签，市场保留为工作台内的发现入口。 |
| 中 | 靠中英文句式强制续跑 | `Agent.IsStandaloneActionPreamble` 用正则、150 字限制与关键词猜测“承诺执行”，随后向会话追加伪装成 User 的 System Directive 并再次调用模型。自然语言轻微变化即可误判，也增加一次调用与历史噪声。 | 用明确的回合状态、工具调用与停止原因判断是否完成；中断恢复针对真实传输错误，避免通过句式猜测用户意图。 |
| 中 | 任意修改都触发项目构建 | `Agent` 只要收到成功的 Mutated 工具结果，就可能在模型结束时进入 `RunVerificationAsync`；`ShouldVerify` 仅判断是否全局任务与开关。文档变更或模型已经验证过的同一版本仍可能再构建。 | 保留验证能力，按变更类型和显式命令选择检查；以工作树版本与验证命令去重，未验证的新代码仍执行检查。 |
| 中 | 全局任务显示“无工作目录” | `App.vue` 展示“任务 · 无工作目录”，但 `WorkspaceInfo.WorkingDirectory` 对全局任务返回 `Directory.GetCurrentDirectory()`；Desktop 启动 Daemon 时工作目录又随开发版/打包版变化。实际执行仍会落到某个目录。 | 显示真实默认执行目录，或给每个全局任务分配明确的临时工作目录，并让 Agent 与用户都能看到。 |
| 低 | 文档继续宣传已移除机制 | 中英文配置文档示例仍包含 `activeProfile`、`profiles`、`routing.strategies` 与工作区 `strategy`，当前配置类型没有这些字段；还列举并未运行的负载均衡方式。用户照抄时字段可能被静默忽略。 | 从真实配置模型生成示例与字段表；对旧字段说明迁移方式，避免以无效参数增加表面功能数量。 |

## 本轮已处理

已移除 Desktop 的“初始化工作区元数据”入口与管理端点、常规设置的 Runtime 行及无用的重连弹框组件、多模态开关，以及模态框的上下分色。重连保留有界重试，失败后退出进程。

会话工具结果现使用摘要行与按需展开；供应商模型列表可以用未保存表单获取，自动参数保留手动值；浏览器使用会话隔离的真实页面视图。另修复了供应商校验失败仍修改内存配置，以及浏览器初始化等待调试命令造成的死锁。

Git worktree、命令会话、MCP 认证、真实 `update_plan`、有界重试和跨回合上下文压缩都有明确用途，不应仅为减少代码量而删除。原生浏览器绘制与 Renderer 弹框避让也有明确职责。
