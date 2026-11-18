# DeepSeek 官方 Agent 对 DeepSeek 模型的优化技术分析

> 本文是对 `deepseek-harness`（`@deepseek-ai/dsh-*`，基于仓库 commit `39163dd30f` / `dsh-v0.2.2-rc.3`）如何优化 DeepSeek 模型使用的深度技术分析，目标是提炼出**可移植到其他 Agent 的具体优化技术**。每一节先说明机制，再说明收益，最后给出移植要点。
>
> 文中的 `file:line` 引用以 `deepseek-harness` 仓库内相对路径给出。数值为代码中的默认常量。

## 目录

- [1. 总览：四个优化层次与架构主线](#1-总览四个优化层次与架构主线)
- [2. 模型调用层（DeepSeek Messages wire 层）](#2-模型调用层deepseek-messages-wire-层)
- [3. 提示词工程层](#3-提示词工程层)
- [4. 上下文管理层](#4-上下文管理层)
- [5. 工具设计层](#5-工具设计层)
- [6. 跨层协同优化：Subagent Fork 与 Plan Mode](#6-跨层协同优化subagent-fork-与-plan-mode)
- [7. 可复用技术清单](#7-可复用技术清单)
- [8. 移植指南](#8-移植指南)

---

## 1. 总览：四个优化层次与架构主线

DeepSeek 官方 Agent 对模型的优化不是散落在某处的一堆技巧，而是**贯穿四个层次的一整套设计**，每一层都在为同一个目标服务：**在保持模型输出质量的前提下，最大化 KV-cache 命中率、最小化 token 与带宽开销、提升长对话与复杂工具调用的稳定性**。

| 层次 | 核心包 | 核心优化 |
| --- | --- | --- |
| 模型调用层 | `packages/llm/llm-deepseek` + `llm` + `llm-retry` + `deepseek-llm-api-extensions` | Anthropic Messages 协议接入、思考档位与完整 CoT/签名回传、互斥缓存计费、空响应重试、溢出分类、重试退避、`in-history` 增量更新、图片网格归一化 + Files API 复用与高水位淘汰、旁路扩展上传 |
| 提示词工程层 | `packages/core/system-prompt` + `agent-loop/src/runtime-context.ts` + `context/harness-dev-context` | 有序 section 组装、易变路径/URL 外移、支持路由的 `in-history` 系统提示词增量追加、结构化动态上下文 `RuntimeContextProjection`、严格插值 |
| 上下文管理层 | `packages/compaction/*` + `packages/llm/token-meter` + `packages/context/*` + `packages/spill/*` | 事件溯源与 5 类 surface 派生、预算感知的触发阈值、两段式压缩与切模型预压缩、summarizer 前缀复用、route-priced 收敛校验、工具结果裁剪、持久化图片卸载、混合 token 计量、spill 落盘 |
| 工具设计层 | `packages/core/tools` + `tool-executor-ptc` + `guard/repeat-tool-reminder` | 类型驱动 DSL 与输出契约分离、强制 JSON Schema 子集、确定性排序、白名单投影、原生/PTC 二选一执行器、动态工具增量声明与并发屏障、重复工具调用守卫 |

贯穿四层的一条主线是 **KV-cache 前缀稳定**：
- 对**不支持增量更新的路由**：把变动内容刻意隔离到前缀之后（动态上下文做成 `user` 角色快照、把源码路径/GUI URL 剥离出系统提示词、摘要指令追加为最后一条 `user` 消息、图片使用步进高水位淘汰），保护前面可复用的前缀。
- 对**显式声明增量能力的模型路由**（如 `deepseek-flash`）：进一步利用 `systemPromptUpdate: 'in-history'` 与 `toolUpdate: 'addition-only'`，在不破坏历史前缀的前提下追加新的 `system` 提示词与激活延迟工具（`defer_loading` + `tool_addition`）。

---

## 2. 模型调用层（DeepSeek Messages wire 层）

这一层的源码集中在 [`packages/llm/llm-deepseek/src/`](packages/llm/llm-deepseek/src/)，并由 [`llm-deepseek-api-key`](packages/llm/llm-deepseek-api-key/src/index.ts)（`deepseek-official`）与 [`llm-deepseek-account`](packages/llm/llm-deepseek-account/src/index.ts)（`deepseek-account`）按鉴权模式挂载。**默认底层协议已统一切换为 Anthropic Messages 兼容接口**（`PUBLIC_BASE_URL = 'https://api.deepseek.com/anthropic'`，不支持配置 `protocol`；[`config.ts:116,227-229`](packages/llm/llm-deepseek/src/config.ts#L116)）。

### 2.1 思考模式与推理强度（`thinking` / `output_config.effort`）

Messages 线协议下，DeepSeek 使用两个顶层字段控制思考（[`serialize.ts:147-156`](packages/llm/llm-deepseek/src/serialize.ts#L147-L156)、[`model-info.ts:7-32`](packages/llm/llm-deepseek/src/model-info.ts#L7-L32)）：

- `thinking: { type: 'enabled' | 'disabled' }` —— 顶层思考开关。
- `output_config: { effort: 'low' | 'high' | 'max' }` —— 推理强度档位（共四档：`off`、`low`、`high`、`max`，不再使用旧 Chat Completions 的 `reasoning_effort`）。

映射与校验规则（[`serialize.ts:147-156`](packages/llm/llm-deepseek/src/serialize.ts#L147-L156)、[`config.ts:230-234`](packages/llm/llm-deepseek/src/config.ts#L230-L234)）：

- `off` → `thinking: { type: 'disabled' }`，且**省略** `output_config`（绝不把 `off` 发到线协议）。
- `low` / `high` / `max` → `thinking: { type: 'enabled' }` + `output_config: { effort }`。
- 默认 `reasoningEffort` 为 `'high'`（未显式指定且部署未禁用时）；若部署策略 `thinking: 'disabled'`，加载阶段即拒绝任何非 `off` 配置。
- 显式 `temperature` 会原样透传（[`serialize.ts:158`](packages/llm/llm-deepseek/src/serialize.ts#L158)）；按文档说明，DeepSeek 在开启思考时接受该字段但忽略其值。

**收益**：把"用不用思考、思考多深"抽象成独立的模型能力维度；非法值在网络 I/O 前抛 `UNSUPPORTED_REASONING_EFFORT`（同时 `LlmRuntime.resolveCallWithInfo()` 在准备阶段也会先校验，[`llm/src/index.ts:934-955`](packages/llm/llm/src/index.ts#L934-L955)），避免无效参数打到服务端。

**移植要点**：在调用配置中把 `reasoningEffort` 与 `temperature` 解耦；请求前对照模型声明的 `efforts` 列表做合法性拦截。

### 2.2 思维链与签名回传（CoT & `signature` replay）

> **注意（协议演进变化）**：早期基于 OpenAI Chat Completions 的实现会在纯文本轮次丢弃 `reasoning_content`；在当前的 Anthropic Messages 适配器中，这一行为已变为**所有包含思考块的 assistant 轮次均原样回传**。

具体实现（[`serialize.ts:26-39`](packages/llm/llm-deepseek/src/serialize.ts#L26-L39)、[`replay.ts:29-64`](packages/llm/llm-deepseek/src/replay.ts#L29-L64)、[`llm-deepseek/README.md:178,186`](packages/llm/llm-deepseek/README.md#L178)）：

- **全轮次 `thinking` 回传**：无论历史 assistant turn 是否发起了工具调用，所有 `reasoning` 块都序列化为 `{ type: 'thinking', thinking: block.text, ...(signature ? { signature } : {}) }` 发回，以完整保真协议语义并维持前缀连续。
- **最小化版本封装的 `ReplayEnvelope`**：流式翻译时提取 `signature_delta`（[`translate.ts:79-82`](packages/llm/llm-deepseek/src/translate.ts#L79-L82)），仅把 `{ response: { kind: 'deepseek-messages', version: 1, model }, blocks }` 存入 `message.source.replayState`（[`replay.ts:29-31`](packages/llm/llm-deepseek/src/replay.ts#L29-L31)），**不在重放元数据中重复拷贝思考或文本正文**。
- **跨适配器/跨模型隔离与降级**：[`LlmRuntime.forAdapter()`](packages/llm/llm/src/index.ts#L1042-L1056) 在发送前剥离不属于当前适配器实例的历史 `replayState`；[`readReplay()`](packages/llm/llm-deepseek/src/replay.ts#L39-L64) 校验版本、来源模型与块对齐关系，跨模型或损坏时仅在每条路由首次出现时记告警（[`host.ts:56-61`](packages/llm/llm-deepseek/src/host.ts#L56-L61)），丢弃 `signature` 而保留持久化正文与工具历史继续工作。

**收益**：在同一个模型的持续对话与工具往返中保真传递原生思考签名；切换模型/协议或重放旧格式日志时可安全降级，不会因为签名不匹配导致整个会话卡死。

**移植要点**：若使用 Chat Completions 接入 DeepSeek 推理模型，只需在含 `tool_calls` 的轮次回传 `reasoning_content`；若使用 Anthropic Messages 接入，则需按 `thinking` + `signature` 完整回传，并把签名与业务正文分离存储、跨模型自动剥离。

### 2.3 缓存感知的 token 计费（cache accounting）

在 Messages 协议下，DeepSeek 上报的 4 个输入/输出桶**本身已是互斥（disjoint）计数**，包括缓存创建写指标（[`translate.ts:35-44,160`](packages/llm/llm-deepseek/src/translate.ts#L35-L44)）：

- `input_tokens` → `inputTokens`（未命中缓存的输入 token）
- `output_tokens` → `outputTokens`
- `cache_read_input_tokens` → `cacheReadTokens`
- `cache_creation_input_tokens` → `cacheWriteTokens`

最终汇总：`usage.totalTokens = usage.inputTokens + usage.outputTokens + (usage.cacheReadTokens ?? 0) + (usage.cacheWriteTokens ?? 0)`（[`translate.ts:160`](packages/llm/llm-deepseek/src/translate.ts#L160)）。

**收益**：上层（token 计量、UI 占用率、压缩阈值、按步账单）直接获得互斥的真实桶计数，不会将缓存命中和未命中混算，且能观测缓存写入（`cacheWriteTokens`）与读取（`cacheReadTokens`）。

**移植要点**：移植时必须先确认所用端点协议（Messages API 的 `input_tokens` 已剥离 cache，而 OpenAI 风格的 `prompt_tokens` 通常包含 `cached_tokens` 需要做减法），在 translator 层统一归一化成互斥四桶。

### 2.4 空响应重试（empty response）

当模型正常结束（`stop_reason` 为 `end_turn` / `stop_sequence`，映射到 `reason.kind === 'stop'`）但**未产生任何内容块**（`blocks.size === 0`），或 HTTP 2xx 响应 `body === null` 时，被判定为退化完成并抛出 `EMPTY_RESPONSE`（[`translate.ts:150`](packages/llm/llm-deepseek/src/translate.ts#L150)、[`adapter.ts:168`](packages/llm/llm-deepseek/src/adapter.ts#L168)；`llm-pi-ai` 适配器同样遵循此规则，[`llm-pi-ai/src/stream.ts:107-115`](packages/llm/llm-pi-ai/src/stream.ts#L107-L115)），默认纳入重试集。

**收益**：防止模型"静默成功但无内容"污染会话历史，让调用方自动重试恢复。

### 2.5 语义错误归一化与上下文溢出分类

[`providerError()`](packages/llm/llm-deepseek/src/transport.ts#L24-L46) 将 HTTP 状态码与服务端 `error.type / error.code / error.message` 统一映射为机器可路由的 `BuiltinLlmFailureCode`（[`llm/src/error.ts:17-70`](packages/llm/llm/src/error.ts#L17-L70)）：

- `401 / 403` 或 `authentication_error / permission_error` → `AUTH`（Account 路由进一步将 `401` 转为 `ACCOUNT_TOKEN_INVALID` 并作废本地凭证）
- `402` 或 [`isQuotaExceededError()`](packages/llm/llm/src/error.ts#L154-L160) → `QUOTA`（Account 路由转为 `ACCOUNT_QUOTA`）
- `429` 或 `rate_limit_error` → `RATE_LIMIT`（同时解析 `Retry-After` 秒数或 HTTP 日期到 `providerRetryAfterMs`）
- [`isContextWindowExceededError()`](packages/llm/llm/src/error.ts#L140-L146) → `CONTEXT_WINDOW_EXCEEDED`
- `400 / 413` 或 `invalid_request_error` → `INVALID_REQUEST`；`>=500` 或 `api_error / overloaded_error` → `SERVER`

**收益**：上层（重试插件、压缩引擎）只按错误码路由，不耦合具体网关的报错文案。

### 2.6 流式协议契约与截断工具容错

所有请求始终 `stream: true`（[`serialize.ts:153`](packages/llm/llm-deepseek/src/serialize.ts#L153)），`translate()` 保证严格的顺序与完整性契约（[`translate.ts:105-167`](packages/llm/llm-deepseek/src/translate.ts#L105-L167)）：

- 从 `message_start` 和 `message_delta` 累计 `usage`，但直到 `message_stop` 才在确认所有块已闭合且 `stop_reason` 存在后，严格按 **`usage` 先于 `finish`、`finish` 之后无任何 chunk** 的顺序发出；若流提前中断则抛 `STREAM_CLOSED`。
- 对已完成的 `tool-call` 校验参数是否为合法 JSON 对象（非 `max-tokens` 时若非法则抛 `MALFORMED_RESPONSE`）；但**若 `reason.kind === 'max-tokens'`，故意放行未闭合/被截断的工具 JSON** 交给上层 `BlockAssembler` 裁剪丢弃（[`translate.ts:151-159`](packages/llm/llm-deepseek/src/translate.ts#L151-L159)）。

**收益**：避免 `max_tokens` 截断工具参数时被误判为协议损坏，并确保消费端收到完整一致的终态与用量。

### 2.7 空消息/空工具结果处理与畸形参数兜底

Messages 序列化对异常边界做了严格防御（[`serialize.ts:16-24,30,118-146`](packages/llm/llm-deepseek/src/serialize.ts#L16-L24)）：

- **畸形历史工具参数兜底**：`toolInput()` 解析历史 `tool-call` 的 `arguments` 时，若遇非合法 JSON 或非对象（例如早期 `max-tokens` 残留或跨协议历史），线上转为 `{}`，同时保留 `id`、`name` 与对应的 `tool_result`，**且绝不改写日志原文**（[`serialize.ts:16-24`](packages/llm/llm-deepseek/src/serialize.ts#L16-L24)）。
- **空块与空消息规则**：跳过空 `text` 块与转换后为空的 `user`/`assistant` 消息；**空的 `tool/result` 必须保留**其 `tool_use_id` 与 `is_error`（`content: []`，而非填入字符串占位；[`serialize.ts:121-123`](packages/llm/llm-deepseek/src/serialize.ts#L121-L123)）。
- **同角色合并与块重排**：连续的 `tool` 与 `user` 消息在 wire 上同属 `role: 'user'`，会自动合并到同一消息中，并将 `tool_result` 块提升排在非 `tool_result` 块之前（[`serialize.ts:125-128,143`](packages/llm/llm-deepseek/src/serialize.ts#L125-L128)），并在发送前完成 tool call/result 配对闭环校验（[`serialize.ts:131-146`](packages/llm/llm-deepseek/src/serialize.ts#L131-L146)）。

**收益**：防止损坏的历史参数或相邻用户/工具消息触发网关 400 而 "brick 掉整个会话"。

### 2.8 可选字段省略而非发 null

序列化时所有可选字段（`system`、`temperature`、`stop_sequences`、`tools`、`output_config`、`defer_loading`）均为"缺失即省略"（[`serialize.ts:156-167`](packages/llm/llm-deepseek/src/serialize.ts#L156-L167)），绝不发送 `null` 覆盖服务端默认值。

### 2.9 空闲看门狗与放弃流的安全排空

[`adapter.ts:65-89`](packages/llm/llm-deepseek/src/adapter.ts#L65-L89) 为每个流挂载 `idleWatchdog`（默认 `DEFAULT_STREAM_IDLE_TIMEOUT_MS = 300_000` ms，即 5 分钟；[`defaults.ts:4`](packages/llm/llm-deepseek/src/defaults.ts#L4)）：

- 仅在 `await watchdog.next(iterator)` 等待服务端数据时计时，超时映射为 `TIMEOUT`，先于它的外部取消映射为 `ABORTED`。
- SSE 解析器通过 `onComment: activity` 让服务端心跳注释也能重置看门狗（[`sse.ts:14-16`](packages/llm/llm-deepseek/src/sse.ts#L14-L16)），但心跳不产生 `StreamChunk`。
- `finally` 块中先 `consumer.abort()` 断掉底层 socket，再以 `void iterator.return(undefined).catch(() => {})` 异步排空，避免被超时放弃的 pending 读取反过来永久阻塞生成器退出（[`adapter.ts:81-88`](packages/llm/llm-deepseek/src/adapter.ts#L81-L88)）。

### 2.10 重试退避策略

重试策略由 provider 声明（`retryPolicy`），由 `dsh-llm-retry` 在 `agent/request-error` 扩展点执行（[`retry-policy.ts:15-25`](packages/llm/llm/src/retry-policy.ts#L15-L25)、[`llm-retry/src/index.ts:60-239`](packages/llm/llm-retry/src/index.ts#L60-L239)）：

- **模式 `normal`（默认）**：有界指数退避 + 对称抖动。默认 `initialDelayMs = 500`、`maxDelayMs = 10_000`、`jitterRatio = 0.1`、**`maxRetries = 5`**。
- **默认重试码**：`EMPTY_RESPONSE`、`RATE_LIMIT`、`SERVER`、`TIMEOUT`、`TRANSPORT`。
- **`Retry-After` 优先**：若响应头给出有效的正数延迟，优先使用；`normal` 模式下若其超过 `maxDelayMs`，则放弃重试并向下游透传错误（[`llm-retry/src/index.ts:225-233`](packages/llm/llm-retry/src/index.ts#L225-L233)）。
- **模式 `always`**：先让下游处理器（如上下文溢出压缩或图片卸载）有机会先行修复；只要未取消且非下游直接重试，则无限退避重试。
- 每次等待前**先持久化 `llm/retry` 事件**，可取消等待结束后再持久化 `llm/retry-started`（[`llm-retry/src/index.ts:185-188`](packages/llm/llm-retry/src/index.ts#L185-L188)），保证计费尝试界限与日志可重放。

### 2.11 原子化调用绑定（`prepareCall`）与连接/凭证同快照

- **热更新与原子代际锁定**：连接配置（`baseURL`、目录、默认能力、超时与图片策略）每次操作通过 thunk 重新解析，而 [`LlmRuntime.prepareCall()`](packages/llm/llm/src/index.ts#L975-L1033) 与 [`DeepSeekAdapter.prepareCall()`](packages/llm/llm-deepseek/src/adapter.ts#L50-L54) 将同一代配置快照、模型能力、默认参数、`retryPolicy` 与最终 `stream()` 绑定为一次性句柄。重试沿用原代际，不会在热更新（HMR）时把旧代能力与新代 endpoint/凭证混在一起。
- **凭证与安全边界**：`resolveAuth(connection)` 从同一连接快照解析鉴权头（API Key 使用 `x-api-key`，账号授权使用 `x-dsh-auth-token`），所有 HTTP 请求强制 `redirect: 'error'` 防止重定向泄漏凭证（[`adapter.ts:143`](packages/llm/llm-deepseek/src/adapter.ts#L143)）。

### 2.12 `purpose` 路由与调用方控思考

`GenerateOptions.purpose` 用于向适配器传递对模型不可见的传输层意图标记：

- **`session-title`（标题生成）**：当前已**不再**在 `serialize.ts` 中硬编码关闭思考，而是由 [`session-title-llm`](packages/session/session-title-llm/src/index.ts#L258-L271) 注入 `ConfigureCall` 回调，由 [`session-title-first-prompt-llm`](packages/session/session-title-first-prompt-llm/src/index.ts#L76-L77) 选取 `model.reasoning?.efforts[0]?.id`（即路由支持的**最低思考档位**，在 DeepSeek 上即为 `off`），同时把独立输出上限设在预算内，避免思考吃掉短标题的 `maxTokens`。
- **`compaction`（上下文压缩）**：在发送给 DeepSeek 时额外附加 HTTP 头 `x-deepseek-harness-compact: 1`（[`adapter.ts:152`](packages/llm/llm-deepseek/src/adapter.ts#L152)），连同 `x-deepseek-harness-user-id` 与 `x-deepseek-harness-session-id` 一起便于服务端区分主对话与辅助流量。

### 2.13 增量协议能力：`in-history` 更新、图片网格/Files API 与旁路扩展

新版 `llm-deepseek` 针对长对话与多模态新增了三项核心 Wire 层优化：

1. **模型声明驱动的历史增量更新（`systemPromptUpdate` / `toolUpdate`）**：
   - `DEFAULT_MODELS` 中的 `deepseek-flash`（`DeepSeek-V4.1-Flash`）声明了 `systemPromptUpdate: 'in-history'` 与 `toolUpdate: 'addition-only'`（[`models.ts:10-16`](packages/llm/llm-deepseek/src/models.ts#L10-L16)）。
   - 当会话中系统提示词发生变化时，`serialize.ts` 保留最初的顶层 `system`，把后续 `system/message` 放在对应 `user/tool_result` 轮次之后作为 `role: 'system'` 消息发出（[`serialize.ts:85-115`](packages/llm/llm-deepseek/src/serialize.ts#L85-L115)）；动态工具更新则通过 `defer_loading: true` 与 `tool_addition` / `tool_removal` 块传递，并自动附加 `anthropic-beta: mid-conversation-tool-changes-2026-07-01`（[`adapter.ts:136-140`](packages/llm/llm-deepseek/src/adapter.ts#L136-L140)、[`messages-api.ts:7`](packages/llm/llm-deepseek/src/messages-api.ts#L7)）。
2. **确定性视觉网格归一化 + Files API 复用与超时指数跳过**：
   - **视觉 token 网格精确对齐**：[`image-tokens.ts`](packages/llm/llm-deepseek/src/image-tokens.ts#L17-L26) 完整移植了 DeepSeek `v41` 视觉 token 计算器（`14px` patch 网格、`3:1` 下采样、`544×544` 总像素放大底线、单图上限 `1024` tokens：例如方图上限 `1302×1302`，16:9 图按 `1708×961` 发出以匹配服务端 `1708×966` 网格），并始终统一前置应用 `4096px` 单边上限（15 图及以上限制）和 `2 MiB` 单图字节上限（透明图用 WebP effort 0、不透明图按 85/75/60 JPEG 阶梯压缩），保证随图片数量增加不会改写早期图片的历史编码与缓存前缀（[`request-pricing.ts:20-65`](packages/llm/llm-deepseek/src/request-pricing.ts#L20-L65)）。
   - **Files API 上传复用与单飞合并**：每张图优先上传 `/v1/files`（附 `anthropic-beta: files-api-2025-04-14`），本地索引按 `(endpoint, authHeadersHash)` 隔离，请求 `7` 天有效期（`fileExpiresAfterSeconds = 604800`），剩余不足 `1` 小时（`fileRefreshMarginSeconds = 3600`）主动刷新；同图并发上传通过 singleflight 合并并保留各等待者的独立取消控制；遇配额错误先批量清理最早 `100` 个 `dsh-` 前缀文件再重试一次（[`file-store.ts:133-278`](packages/llm/llm-deepseek/src/file-store.ts#L133-L278)）。
   - **上传失败整包回退内联 + 指数跳过退避**：若任一图片上传超时（默认 `20s`）或失败，当次请求整体回退为内联 base64（绝不混用 `file_id` 与 base64），同时针对该 `(endpoint, auth)` 记录**指数退避跳过计数**：连续超时使后续需要新上传的请求直接跳过 Files API 的次数按 `1, 2, 4, 8, …` 翻倍，一次成功上传即重置（[`file-store.ts:182-188,230-237`](packages/llm/llm-deepseek/src/file-store.ts#L182-L188)）。
   - **步进高水位预算**：Files 模式总图片字节上限 `128 MiB`（淘汰量子 `64 MiB`），内联回退上限 `20 MiB`（淘汰量子 `10 MiB`），张数上限 `600`（淘汰量子 `20`）。超限时抛 `IMAGE_OFFLOAD_REQUIRED`（含需卸载的最旧图片数 `offloadImages`），通过大步长量子淘汰避免每加一张新图就破坏前缀缓存（[`images.ts:78-91`](packages/llm/llm-deepseek/src/images.ts#L78-L91)、[`llm/src/content.ts:285-333`](packages/llm/llm/src/content.ts#L285-L333)）。
3. **模型不可见的旁路请求扩展（`deepseek-llm-api-extensions`）**：
   - 插件可注册与 `messages`/`tools` 同级的顶层 JSON 字段（如默认开启的增量会话日志 `dsh_session_log` 与激活插件列表 `dsh_plugin_packages`），Adapter 仅在收到 HTTP 2xx 后调用 `accept()` 推进状态游标；序列化失败或非 2xx 时自动回退发送基础请求并保留状态供后续重传，全程**零模型 token、零 KV-cache 扰动**（[`deepseek-llm-api-extensions/src/index.ts`](packages/llm/deepseek-llm-api-extensions/src/index.ts)、[`request-extensions.ts`](packages/llm/llm-deepseek/src/request-extensions.ts)）。

---

## 3. 提示词工程层

### 3.1 有序 section 组装与易变信息后置

系统提示词由多个 `PromptSection` 按 `order` 升序拼接（同 order 按 code-unit 字典序），中心分配表定义在 [`system-prompt/src/index.ts:136-173`](packages/core/system-prompt/src/index.ts#L136-L173)：

| order | 常量 / section | 归属与作用 |
| --- | --- | --- |
| `-1000` | `HARNESS_IDENTITY` (`harness:identity`) | 固定开场白 `"You are an AI agent powered by DeepSeek Harness."` |
| `0` | `DEPLOYMENT_PERSONA_PREFIX` (`deployment:persona-prefix`) | 部署人格前缀（可被作用域遮蔽） |
| `500–900` | `PLAN_POLICY` / `TEAM_POLICY` / `PTC_ONLY` (`800`) / `FILE_REFERENCE` (`900`) | 计划模式、团队、PTC 约束与 `@file` 引用规则 |
| `1000–3100` | `TOOL_BASH` (`1000`) ~ `TOOL_COMPUTER_USE` (`3000`)、`MCP_SERVERS` (`3100`) | 各工具的跨调用引导 |
| `5000–9900` | `TOOLS_SDK` (`5000`)、`DELIVERABLE_FILE_REFERENCES` (`9000`)、`STRUCTURED_OUTPUT` (`9900`) | PTC 生成的 TypeScript SDK、交付物与结构化输出要求 |
| `10000–10200` | `HARNESS_SOURCE` (`10000`)、`WEB_SURFACE` (`10100`)、`DEPLOYMENT_PERSONA_SUFFIX` (`10200`) | 静态的源码/Web 语义引导与部署人格后缀 |

关键的 KV-cache 与一致性设计：
- **易变环境值剥离出系统提示词**：早期把 `harness` 源码路径放在 `-99` 的做法已重构。[`harness-dev-context`](packages/context/harness-dev-context/README.md#L85-L110) 将**无具体路径/URL 的静态引导**放到末尾 `10000 / 10100`，而把实际随启动环境变化的源码 checkout 路径与 Web GUI URL 移入动态运行时上下文（`CONTEXT_ORDERS.HARNESS_SOURCE: 200`、`WEB_SURFACE: 210`）。这样从不同目录、端口重启服务时，系统提示词保持逐字节一致！
- **工具引导与执行/可见性同源**：例如 [`tool:read`](packages/fs/tool-fs/src/read.ts#L69-L75) 通过 `text: ({ scope }) => ctx.tools.get('read', scope) === undefined ? '' : ...` 动态判断工具是否可见；[`tool:bash`](packages/shell/tool-bash/src/index.ts#L291-L295) 直接引用其渲染器产生的 `[exit code: N]` 标记。

**收益**：把"最稳定、所有会话共享的内容"置于最前，"特定于运行实例的动态环境值"移到对话历史中，从结构上减少前缀失效。

### 3.2 系统提示词前缀稳定性与动态上下文（`SystemPromptProjection` + `RuntimeContextProjection`）

系统提示词与动态运行态上下文的持久化投影共同定义在 [`packages/core/agent-loop/src/runtime-context.ts`](packages/core/agent-loop/src/runtime-context.ts)：

1. **系统提示词作为 `system/message` 表面节点（`SystemPromptProjection`，[`runtime-context.ts:74-122`](packages/core/agent-loop/src/runtime-context.ts#L74-L122)）**：
   - 首条系统提示词占用 surface 节点 0（即使为空也占位）。
   - **当路由声明 `systemPromptUpdate: 'in-history'` 且处于同一连续请求序列（`!startsSeries`）时**：非空提示词变更**直接向历史末尾追加**新的 `system/message`，不改写节点 0，完整保住整段历史 KV-cache！
   - **无此能力的路由，或在新序列起点（如发生了 compaction、模型未支持动态工具变更导致工具集变化等）且有效文本变化时**：按节点逐一发送空替换清空后续旧系统节点，再把最新提示词归并覆写到节点 0（若新序列开始时最后一个存活系统节点已是最新文本，则零操作，连旧尾部都能保留）。若提示词被清空（`''`），则清空全部存活系统节点。
2. **动态上下文快照（`RuntimeContextProjection`，[`runtime-context.ts:125-181`](packages/core/agent-loop/src/runtime-context.ts#L125-L181)）**：
   - 诸如工作目录、Sandbox 策略、审批策略、子代理权限、Web URL 等易变上下文，注册为 `PromptContext` 并在入 step 前通过 `refreshContext()` 刷新，最终打包成带 `source: { kind: 'runtime-context', form: 'snapshot', sections }` 的 **user-role `<system-reminder>` 快照消息**。
   - **结构化防抖**：使用 `deepEqualJson(previous, sections)` 对比上一次存活快照的 `sections`（而非只看拼接文本）；未变则零追加。若先前的快照已被 compaction 替换掉，`retained` 重置为 `null`，下一次请求会自动重新注入以防模型遗忘策略。
   - **防注入与显式替代声明**：渲染正文中的 `</system-reminder>`（含大小写与空白变体）会被转义为 `<\...>`；首次快照前缀为 `"The following information describes your current session:"`，后续快照前缀改为 `"The following information describes your current session. It replaces earlier versions of this context, including any retained in conversation summaries."`（明确覆盖可能残留在压缩摘要中的旧状态！）；当非空状态清空为空时（且此前存在过快照），注入固定 `CLEARED` 标记 `<system-reminder>\nCurrent runtime context: none. Earlier runtime-context snapshots no longer apply.\n</system-reminder>`。

**收益**：无论模型是否支持 `in-history` system 更新，动态环境策略切换都不会破坏前缀缓存；即使经历了上下文压缩，最新运行态策略也能被精准恢复和覆盖。

**移植要点**：如果目标模型不支持 `in-history` system 消息（例如普通 Chat Completions 网关），务必将所有随会话变化的环境/权限状态做成"仅在变化时追加的 `<system-reminder>` user 消息快照"；如果目标模型支持（如 DeepSeek V4.1 Flash），还可以把模式切换类的 system 变更也通过追加实现缓存零失效。

### 3.3 严格插值与 `interpolate: false`

`{{variable}}` 插值引擎位于 [`system-prompt/src/index.ts:347-384`](packages/core/system-prompt/src/index.ts#L347-L384)：

- 完整 `{{...}}` 组内存在非法变量名（不匹配 `/^[a-z][a-z0-9_]*$/`）、未注册变量、或值为 `undefined` 时**直接抛错**；但前面没有配对 `}}` 的孤立 `{{` 视为普通文本。
- 使用 `Object.hasOwn(variables, name)` 查表，防止 `{{constructor}}` 等原型链污染。
- 替换值不进行二次扫描，杜绝变量注入式二次展开。
- **字面量豁免（`interpolate: false`）**：工具文档或生成 SDK（如 `tools:sdk`）可能包含任意 `{{...}}` 示例文本，设置 `interpolate: false` 可整段跳过变量解析，防止误报错（[`system-prompt/src/index.ts:299,341`](packages/core/system-prompt/src/index.ts#L299)）。

**收益**：装配错误 fail loud 暴露在请求前，同时豁免机制避免生成代码/工具文档中的花括号误伤。

### 3.4 `complete` section

`PromptSection.complete?: true` 允许某个 section（如自定义人格或 `minimal` 预设）在运行完整个 `system-prompt/assemble` waterfall（保留工具、上下文与变量解析）后，覆盖并**独占**整个 system prompt（[`system-prompt/src/index.ts:69-75,659-695`](packages/core/system-prompt/src/index.ts#L69-L75)）。若同作用域存在多个 `complete` section 则报错。

---

## 4. 上下文管理层

### 4.1 事件溯源 + 5 类 surface 派生历史

这是整个 harness 的架构根基。`Session` 是**只追加的 JSONL 事件日志**，模型消息历史通过 `deriveMessages()` 从日志增量派生（[`packages/core/session/src/index.ts:1079-1100`](packages/core/session/src/index.ts#L1079-L1100)）：

- 构成模型可见 `surface` 的共有 **5 类**事件（[`session/src/types.ts:447-452`](packages/core/session/src/types.ts#L447-L452)、[`surface.ts:50-56`](packages/core/session/src/surface.ts#L50-L56)）：`system/message`、`developer/message`、`user/message`、`assistant/message`、`tool/result`。
- 原始流式 chunk 与时间戳以紧凑格式嵌入 `assistant/message.stream`（用于 UI 回放、首字延迟统计与重试审计），失败/取消的尝试记为 log-only 的 `assistant/attempt` 不进入模型历史。
- 支持插件注册纯函数消息投影 `SessionMessageProjection`（如 `image/offload` 在不篡改原始事件的前提下把指定图片替换成 `offloaded: true` 标记），以及由 [`SessionSurface`](packages/core/session/src/surface.ts) 维护的 `replaceGeneration`（位置替换代数）和 `contentGeneration`（替换或消息投影代数）。
- 派生出的 `Message` 对象**深度冻结（deep-frozen）并在请求构造中复用冻结证据**（[`agent-loop/src/agent.ts:734-740`](packages/core/agent-loop/src/agent.ts#L734-L740)），纯尾部追加仅增量计算新节点，避免全量深拷贝。

**收益**：严格保证 **"Model-visible ⟺ logged"** —— 任何压缩替换、图片卸载、提示词更新都能从日志精确重建与审计，且内存开销极低。

### 4.2 压缩触发机制与真实可用预算计算

`compaction-basic` 在三个扩展点驱动自动压缩（[`compaction-basic/src/index.ts:172-272`](packages/compaction/compaction-basic/src/index.ts#L172-L272)）：

1. **步边界压力触发（`agent/pre-step`）**：每次 step 前测量 `totalTokens`，达到或超过 `spec.thresholdTokens` 时触发压缩。
2. **切换模型预压缩（`agent/request`）**：当切向不同 `provider/model`（尤其是更小上下文窗口的模型）时，提前按目标模型的规格执行 `compactPressure(..., requireBelowThreshold = true)`，无法压到阈值以下则阻止切换并抛 `MODEL_SWITCH_COMPACTION_FAILED`（保留原模型）。
3. **上下文溢出触发（`agent/request-error`）**：捕获到 `CONTEXT_WINDOW_EXCEEDED` 时强制压缩并重试。

**关键的真实预算公式**（[`compaction-basic/src/config.ts:20-23,75-109,153-217`](packages/compaction/compaction-basic/src/config.ts#L153-L217)）：
默认常量：`thresholdRatio = 0.8`、`retainRatio = 0.16`、`headroomTokens = 65_536`、摘要 `maxTokens` 默认复用 `headroomTokens`（**`65_536`**）、`compactionRetries = 1`、`maxOverflowRetries = 1`。

实际阈值和保留尾部**扣除了模型为输出预留的 `reservedCompletionTokens`（即请求 `maxTokens` 或适配器 `defaultMaxTokens`，如 `256,000`）与 `headroomTokens`**：

```ts
messageBudgetTokens = contextWindow - reservedCompletionTokens
pressureBudgetTokens = messageBudgetTokens - headroomTokens
thresholdTokens = Math.floor(Math.min(contextWindow * thresholdRatio, pressureBudgetTokens))
retainTokens = policy.retainTokens ?? Math.floor(messageBudgetTokens * retainRatio)
```

> **示例**：对于 `deepseek-flash`（`contextWindow = 1_048_576`，默认 `maxTokens = 256_000`），`messageBudgetTokens = 792_576`，`pressureBudgetTokens = 727_040`（小于 `1_048_576 * 0.8 = 838_860`），因此实际在 **`727_040` tokens** 即触发压力压缩，不会等到 80% 才发现加上预留输出窗口已经溢出！

**保留尾部与边界保护**（[`region.ts:118-156`](packages/compaction/compaction-basic/src/region.ts#L118-L156)）：从 surface 末尾向前累加 route-priced `tokens` 直到 `retainTokens` 得到 `keepFromIdx`；若 node 0 为 `system/message`，则强制从 `firstIdx = 1` 起算（**永远不把节点 0 划入被压缩区**）；并调用 `toolPairingBalancedBefore/After` 确保**绝不劈开 tool-call/result 配对**。

### 4.3 两段式压缩、溢出强制缩减与并发稳定性校验

压缩采用严格分层的成本策略（[`compaction-basic/src/index.ts:320-389`](packages/compaction/compaction-basic/src/index.ts#L320-L389)）：

1. **第一段（零模型成本）**：先运行确定性的 `ToolResultPruner.pruneSession()` 裁剪超长工具输出，并通过单例 `TokenMeter` 重新计量；若已降到阈值之下，直接结束，**省下一次 LLM 摘要调用**。
2. **第二段（LLM 摘要）**：仍超阈值时，按 `selectCompactableRange` 最多尝试 `compactionRetries + 1` 轮摘要压缩；摘要过程中若遇到 `IMAGE_OFFLOAD_REQUIRED`，会通过 `compaction/summary-error` 事件触发 `compaction-image-offload` 卸载选定区间内的最旧图片并重算继续（[`region.ts:397-410`](packages/compaction/compaction-basic/src/region.ts#L397-L410)、[`compaction-image-offload/src/index.ts:33-40`](packages/compaction/compaction-image-offload/src/index.ts#L33-L40)）。
3. **溢出恢复**：遇 `CONTEXT_WINDOW_EXCEEDED` 时，先执行工具结果裁剪，再以 `retainTokens = 0` 强制压缩尽可能多的安全前缀；且只要 `replaceGeneration` 前进过（哪怕第一段裁剪落地后第二段摘要失败），仍返回 `{ kind: 'retry' }` 尝试重发请求（[`index.ts:250-257`](packages/compaction/compaction-basic/src/index.ts#L250-L257)）。
4. **锁标记与一致性校验**：通过 `compaction/start` / `compaction/end` 持久化锁防止重入，并在异步摘要结束后验证所选 span 或整个 surface 未在期间被外部修改（`assertWholeSurfaceUnchanged` / `assertSelectedSpanStable`），才原子落地替换（[`region.ts:174-277`](packages/compaction/compaction-basic/src/region.ts#L174-L277)）。

### 4.4 summarizer 复用 KV-cache 前缀与安全选路

摘要调用通过 [`summarizeWithLlm()`](packages/compaction/compaction-basic/src/summarizer.ts#L124-L179) 与 [`buildSummarizationInput()`](packages/compaction/compaction-basic/src/region.ts#L545-L564) 构造：

- **前缀逐字节复用**：`input` 按顺序重放：表面节点 0 的 `system/message`、当前请求头的 `tools`（及 `toolHistory`）、待压缩区间的派生消息，随后把 `COMPACTION_INSTRUCTION` 作为**最后一条 `user` 消息**追加在末尾（而非另起独立 system prompt），使辅助摘要调用成为主对话的严格前缀延续，直接命中服务端 KV cache！
- **安全选路（`conversationTarget`，[`index.ts:80-85`](packages/compaction/compaction-basic/src/index.ts#L80-L85)）**：未显式指定 `summarizationProvider/Model` 时，优先选用**最近一次成功响应的路由（`answeredRoute`）**，其次才是最新 `requestHeader`、最后是 `agent.options`——防止在切向小窗口模型或已溢出模型后，让新路由去总结它本身装不下的历史。
- **8 段结构化 Markdown checkpoint**：强制按顺序输出 Primary Request and Intent / Key Technical Concepts / Files and Code / Errors and Fixes / Pending Jobs / Current Work / Next Step / Critical Context（空段写 `(none)`，禁止删段）；若历史中已包含 `<compacted-summary>`，要求**合并并剔除陈旧信息**而非原文照抄（[`summarizer.ts:35-70`](packages/compaction/compaction-basic/src/summarizer.ts#L35-L70)）。
- 落地时由 `frameSummary()` 包裹为 `CHECKPOINT_PREAMBLE` + `<compacted-summary>...</compacted-summary>`，通过 `surfaceOp: { op: 'replace', startSeq, endSeq }` 原子替换被遮蔽范围。

### 4.5 route-priced 收敛校验

落地摘要前强制执行严格收缩检查（[`region.ts:415-423`](packages/compaction/compaction-basic/src/region.ts#L415-L423)）：

- 比较**包裹后的纯文本 checkpoint 估算 token 数（`framedSummaryTokenCount`）** 与 **被遮蔽区间的路由实际定价 token 数（`shadowedRouteTokenCount`，含图片视觉 token 与文件 handle 文本）**。
- 必须满足 `framedSummaryTokenCount < shadowedRouteTokenCount`，否则拒绝落地并报错。注意：O(1) 影子价统计（`shadowedTokenCount`）仍用统一启发式，而是否能降低真实请求压力的收敛校验则看路由真实定价！

**收益**：保证每次压缩都真实减小下一轮请求压力（包括把多张高代价图片浓缩为纯文本摘要的收益），杜绝压缩越压越大或死循环。

### 4.6 工具结果裁剪（`ToolResultPruner`）

`ToolResultPruner` 是**零模型成本、确定性**的事后/预压缩裁剪器（[`compaction-tool-result-pruner/src/index.ts:83-182`](packages/compaction/compaction-tool-result-pruner/src/index.ts#L83-L182)、[`config.ts:7-14`](packages/compaction/compaction-tool-result-pruner/src/config.ts#L7-L14)）：

- 默认阈值：`thresholdChars = 8192`、`headChars = 4096`、`tailChars = 1024`。
- 按 **Unicode code point**（`Array.from(text)`）切分而非 UTF-16 code unit，绝不截断代理对（emoji/生僻字）；保留非文本块原顺序，中间插入一次 `PRUNE_MARKER = '\n\n[... tool result middle pruned ...]\n\n'`。
- 每次替换前紧邻追加一条 `compaction/prune` 影子价事件（记录被遮蔽节点的 `estimateMessage(original)`），让下游轻量投影消费者无需保存全表每节点状态也能以 O(1) 精确扣减。

### 4.7 混合 token 计量与分离式投影（`TokenMeter`）

[`TokenMeter`](packages/llm/token-meter/src/index.ts) 不依赖原生 tokenizer，采用"确定性启发式 + 路由精确图片定价 + Provider 用量锚定"的混合设计：

- **固定密度启发式**（[`estimate.ts:13-19`](packages/llm/token-meter/src/estimate.ts#L13-L19)）：`CHARS_PER_TOKEN = 4`、`BLOCK_OVERHEAD = 4`、`ROLE_OVERHEAD = 4`（system-role 文本按纯文本 + `ROLE_OVERHEAD` 计价）。文档明确指出 4 字符/token **系统性低估 CJK 文本与 JSON Schema**，因此仅作相对启发式基准。
- **路由视觉定价与文件投影同步**：`measure()` 调起当前路由的 `imageRequestPricing`（对 DeepSeek 路由直接按 14px/3:1 网格算出准确视觉 token，已 `offloaded` 的图片按占位符文本计价）以及 `fileRequestText`（[`index.ts:151-196`](packages/llm/token-meter/src/index.ts#L151-L196)），确保压缩评估在多模态和附件场景下足够准确。
- **混合锚定（Hybrid Anchoring）**：仅当最近一次成功请求的规范请求信封（`headerEquals`）与当前一致、且 provider 上报 `usage` 总量 ≥ 完整路由定价锚点时，才以该 `usage` 作为 `baseline`（从而把 CJK/Schema 的 tokenizer 误差一并吸收进去！），之后通过"带符号 surface 增量"计算后续追加或压缩删减；一旦工具列表/模型变化或 provider 用量缺失，自动回退全量估算（[`index.ts:163-186`](packages/llm/token-meter/src/index.ts#L163-L186)）。
- **状态分离**：持久化的投影单元（`tokenMeasurement`、`tokenUsage`、`contextPressure`、`contextBreakdown`）只存固定大小的游标与摘要（不存消息数组），而位置节点价格缓存在进程内可随时从日志重建。

### 4.8 持久化图片高水位卸载（`compaction-image-offload`）

为了解决多轮视觉对话累积图片字节/张数超限（默认 `128 MiB` / `600` 张，或内联回退 `20 MiB`）导致无法请求的问题，[`compaction-image-offload`](packages/compaction/compaction-image-offload/src/index.ts) 与适配器协同：

- 适配器只负责检出超限数量 `offloadImages` 并抛 `IMAGE_OFFLOAD_REQUIRED`，**不在适配器内部私自删图**（否则会破坏 `Model-visible ⟺ logged` 不变量，或在恢复 Files API 时图片又复活导致前缀失效）。
- `compaction-image-offload` 在 `agent/request-error` 与 `compaction/summary-error` 中捕获该错误码，按当前 surface 从旧到新选出 `offloadImages` 个尚未卸载的输入图片位置，追加一条 `image/offload` 事件并重试（**不消耗 `llm-retry` 配额**）。
- 所有路由通过 `SessionMessageProjection` 将其派生为 `offloaded: true`，发送包含唯一可读路径（若执行环境可访问）的占位文本，模型仍可用 `read_image` 按需重新查看，而历史视觉 token 与请求带宽立即释放。

### 4.9 动态上下文注入（`packages/context/*`）

均以带 `source` 归属的 `user/message` 注入，不污染 system prompt 前缀：

1. **`agent-instructions`**（`dsh-base` 默认启用，`maxBytes: 65536`）：
   - 按**用户全局链**（`$DSH_HOME/AGENTS.md`、`<agentsHome>/AGENTS.md`）与**项目链**（项目根至 `cwd` 的 `AGENTS.md`/`CLAUDE.md` 及 `.local` 覆盖）**两条链分别独立预算 `maxBytes`**（整条消息至多 `2 × maxBytes`），单源文件读取上限 `maxSourceBytes = 1_048_576`。同目录下内容经 trim 后一致的候选文件自动去重（避免同时有 `AGENTS.md` 和 `CLAUDE.md` 时重复注入）；超预算优先丢弃更宽泛的父级文件、再截断最具体文件；仅在 `read`/`write`/`edit` 工具首次触达更深子目录或文件变更/删除时增量注入差异，包裹 `<system-reminder>` 并转义闭合标签（[`agent-instructions/README.md`](packages/context/agent-instructions/README.md)）。
2. **`time-context`**（可选）：在符合 `refreshIntervalMs`（默认 `600_000` ms 即 10 分钟；设 `0` 则每步注入）时注入当前时间戳、距上条消息/上一步耗时，并结合当前 turn 浏览器时区提示模型对无限定时间的解释策略。
3. **`tmux-context`**（可选）：仅在 turn 第 1 步且进程控制终端确实匹配 `#{pane_tty}`（排除 VS Code 等仅继承环境变量的终端）并且 layout 变化时，注入紧凑 pane 树布局，刻意省略像素尺寸与其他 pane 内容。
4. **`session-reference`**（可选）：跨会话 `@label` 引用快照。每条消息最多 `3` 个引用；每源字节预算默认 `max(65536, floor(contextWindow × 4 × 0.2))`（可通过 `maxReferenceBytes` 覆写），剔除工具调用/思考过程后按"保留最新 + 丢弃中间非 checkpoint + 截断最长文本"裁剪；若发生截断且挂载了 `spillStore`，自动把完整投影按每行最多 64 个 code point 的 JSON 片段格式落盘，供 `read`/`grep` 分页精确检索；内嵌 `<` 转义为 `\u003c` 并附不可信背景提示。
5. **`file-reference`**（`@file` 补全）：只把路径文本放入 prompt 并配静态规则（`FILE_REFERENCE_PROMPT`），**禁止自动把文件内容塞进上下文**，由模型按需调用 `read` 查看。

### 4.10 工具输出落盘与图文共享预算（`spill-policy` + `spill-local`）

`dsh-base` 默认挂载 `spill-local` 与 `spill-policy`（[`cordis.patch.yml:421-427`](packages/bundle/base/cordis.patch.yml#L421-L427)，`maxInlineTokens: 12500`）：

- **图文混排共享 token 预算**（[`spill-policy/src/index.ts:57-120`](packages/spill/spill-policy/src/index.ts#L57-L120)）：不仅管纯文本，还联合路由的 `imageRequestPricing` 一起把图片视觉 token + 描述符算进 `maxInlineTokens`；超预算时保存完整全文（含图片可读附件路径提示）到会话私有的 spill 文件，向模型返回"头尾保留 + `[...]` + `(Omitted N bytes. Full formatted result stored at: ...)`"指引。
- **反循环跳过**：明确跳过 `read` 工具自身的结果（避免模型去读 spill 文件时再次触发 spill 死循环），并对 PTC 子调用日志异步限流而不阻塞主程序变量传递。

---

## 5. 工具设计层

### 5.1 类型驱动 DSL 与三层输出分离（单一事实来源）

工具定义采用 `defineTool()` DSL（[`packages/core/tools/src/schema.ts:555-633`](packages/core/tools/src/schema.ts#L555-L633)、[`contracts.ts:16-40`](packages/core/tools/src/contracts.ts#L16-L40)），从一份声明同时生成：

- `InferArgs` / `InferValue` → 静态 TypeScript 入参与返回值类型。
- `parameterSchemaSpecToJsonSchema` / `valueSchemaSpecToJsonSchema` → wire 入参/出参 JSON Schema。
- `validateJsonSchemaValue` → 栈安全迭代式运行时校验器。

并且**强制要求每个工具显式声明 `output` 契约**（`{ schema, render, presentationMeta? }`）：`execute()` 只返回符合 `output.schema` 的规范 lossless JSON 值（直接供 PTC 程序调用与校验）；`output.render(args, value)` 把结果纯函数投影为给模型看的 `ContentBlock[]`；`output.presentationMeta` 提取仅给 UI 渲染用的结构化元数据（不进模型输入）；另有 `projectContent`（post-execute 前）与 `finalizeContent`（最终兜底变换）钩子。

**收益**：消除类型/schema/校验三处漂移，同时彻底隔离"程序返回值"、"模型阅读文本"和"UI 展示元数据"。

### 5.2 强制 JSON Schema 子集 + 显式 reject

[`assertSupportedJsonSchema()`](packages/core/tools/src/json-schema.ts#L385-L405) 对任何工具输入/输出与外部 schema（MCP、subagent、workflow）做白名单检查（[`json-schema.ts:76-88`](packages/core/tools/src/json-schema.ts#L76-L88)）：

- 仅允许约束关键字 `type` / `oneOf` / `properties` / `required` / `additionalProperties` / `items` / `enum` / `const`，以及注解关键字 `description` / `title` / `default` / `examples`。
- **注解与约束严格分离**：`default`/`examples` 只传给模型和类型生成，不参与运行时校验；任何未实现校验的 JSON Schema 关键字或错位写法**直接报错拒绝**（[`json-schema.ts:8-10`](packages/core/tools/src/json-schema.ts#L8-L10)），避免模型依赖了一条运行时根本没执行的伪约束。

### 5.3 确定性排序

工具 schema 排序位于 [`system-prompt/src/index.ts:215-262`](packages/core/system-prompt/src/index.ts#L215-L262)：

- 默认使用与 locale 无关的 code-unit 字典序比较（`a < b ? -1 : a > b ? 1 : 0`），保证不同操作系统/语言环境下输出字节完全一致。
- 支持配置 `toolOrder`（必须包含且仅包含一次 `<unlisted-tools>` 占位符），未列举工具在占位符位置按确定性字典序展开。

**收益**：杜绝因为插件加载时序或系统 locale 不同导致工具 schema 顺序漂移而击穿 KV-cache。

### 5.4 白名单投影（防内部实现泄漏）

[`projectToolSchema()`](packages/core/tools/src/schema.ts#L641-L646) 与 `ToolsService.schemas()`（[`tools/src/index.ts:316-318`](packages/core/tools/src/index.ts#L316-L318)）只投影模型真正需要的 4 个字段：`name`、`description`、`parameters`，以及（仅当为 `true` 时）`deferLoading`。`output`、`execute`、`projectContent`、`finalizeContent`、`timeoutMs`、`isConcurrencySafe`、`presentCall`、`presentResult` **绝不透出到 wire**。

### 5.5 呈现意图与模型契约正交

[`presentation.ts`](packages/core/tools/src/presentation.ts) 定义了跨端卡片词汇（`ToolCallView`：`generic/terminal/diff`；`ToolResultView`：`generic/terminal/diff/search/read/web`）：

- `presentCall` / `presentResult` 由 `defineTool` 包装为纯函数软校验——若遇到旧版会话历史中不符合当前 schema 的参数，返回 `undefined` 回退为通用卡片，**绝不抛异常破坏回放**（[`schema.ts:610-625`](packages/core/tools/src/schema.ts#L610-L625)）。
- Web 客户端本身更是直接从持久化事件中的原始参数、文本结果和 `output.presentationMeta` 独立派生 UI 卡片（[`tools/README.md:96-98`](packages/core/tools/README.md#L96-L98)），进一步与模型可见内容解耦。

### 5.6 PTC 模式（Programmatic Tool Calling）

> **注意（架构演进）**：旧版 `agent-tool-presentation` 的 `'native' | 'code' | 'both'`（以及 Python SDK 生成）已被废弃并移除 `'both'` 与 Python 支持，现由 [`@deepseek-ai/dsh-tool-executor-native`](packages/core/tool-executor-native/README.md) 与 [`@deepseek-ai/dsh-tool-executor-ptc`](packages/core/tool-executor-ptc/src/index.ts) 直接接管（每个 Agent 二选一）。

在 PTC 模式下（[`tool-executor-ptc/src/index.ts:17-50`](packages/core/tool-executor-ptc/src/index.ts#L17-L50)、[`ts-types.ts:13-39`](packages/core/tool-executor-ptc/src/ts-types.ts#L13-L39)）：

- **极简暴露面**：只向模型暴露唯一的直接工具 `run_code`，并在系统提示词中注册 `tools:ptc-only`（order `800`）与基于当前可见工具集生成的 **TypeScript SDK**（`tools:sdk`，order `5000`，`interpolate: false`）。
- **代码生成健壮性**：使用 `tool.output.schema` 直接生成强类型返回声明；非合法 TS 标识符的工具名（如含连字符的 MCP 工具名）生成引号键并用 `tools["my-tool"](args)` 下标访问；description 折叠空白并转义 `*/` 防止逃逸 JSDoc 注释块。
- **执行本地化与并发**：程序内部通过 `await tools.name(args)` 调用真实工具（共享同一套 `pre-execute`/guards/审批/超时/日志管道），支持 `Promise.all` 自动重叠执行标有 `isConcurrencySafe` 的只读调用（默认 `maxParallelSubCalls: 10`）；**中间数据全部留在沙盒执行上下文中，只有显式调用 `inspect(...values)` 的结果和有限外层输出进入对话历史**，大幅削减多步筛选/聚合任务中的历史 token 膨胀！
- **单次上限保护**：按 `floor(timeoutMs / 60000 × maxSubCallsPerMinute)`（默认 `maxSubCallsPerMinute = 1024`，即默认 2 分钟超时上限 2048 次子调用）限制总子调用次数，硬阻断无限循环。

### 5.7 动态工具增量激活、并发调度池与防死循环守卫

1. **会话中动态工具增量投影（`projectToolUpdates`，[`llm/src/content.ts:397-510`](packages/llm/llm/src/content.ts#L397-L510)、[`agent-loop/src/agent.ts:698-712`](packages/core/agent-loop/src/agent.ts#L698-L712)）**：
   - 工具支持 `when` 步级激活条件，每次变更仅在 `developer/message` 中记录 `tool-addition`/`tool-removal`（逻辑日志与能力无关）。
   - 发送前若路由声明 `toolUpdate: 'addition-only'`（如 `deepseek-flash`）或 `'in-history'`，历史新增工具在顶层 `tools` 声明中标记 `defer_loading: true` 并保留初始位置，再按 `developer/message` 时序以 `tool_addition` 激活；当 `addition-only` 路由遇到工具移除或从空工具集首次新增时，自动 rebase 基线（使已激活工具变为即时声明）保证语义正确同时尽可能延长前缀复用；不支持该能力的路由则在变化时重置序列并剔除 `developer` 消息。
2. **并发池与独占屏障调度（[`tools/src/batch.ts:50-96`](packages/core/tools/src/batch.ts#L50-L96)、[`agent-loop/src/constants.ts:6`](packages/core/agent-loop/src/constants.ts#L6)）**：
   - 模型单步发出多个工具调用时，依赖纯同步谓词 `isConcurrencySafe(args)`：连续的并发安全调用进入默认上限 `10`（`DEFAULT_MAX_PARALLEL_TOOL_CALLS`）的滚动并发池同时执行，写/变异类独占调用形成序列化屏障，结果严格按模型发出的原始顺序提交，缩短端到端墙钟耗时。
3. **重复工具调用守卫（`repeat-tool-reminder`，[`guard/repeat-tool-reminder/src/index.ts`](packages/guard/repeat-tool-reminder/src/index.ts)）**：
   - `dsh-base` 默认启用，在 `tools/post-execute` 统计每个 Agent 对同一 `(toolName, 深排键名规范化后的 arguments)` 的连续相同调用（即便被 `pre-execute` 拒绝也计数；支持 `exclude` 使记账类工具透明不打断循环链；收到新用户消息时清零）。
   - 默认阈值 `[3, 5, 8]`：第 3 次通过 `additionalContexts` 注入温和提示（不阻断执行、不污染 `tool/result` 原文），第 5、8 次注入包含工具名、次数和截断参数预览（默认 `500` 字符防大参数撑爆上下文）的详细纠偏提示，有效帮模型跳出空转死循环。
4. **文件读后改守卫（`fs-observation-policy`，[`packages/fs/fs-observation-policy/README.md`](packages/fs/fs-observation-policy/README.md)）**：
   - 通过监听 `fs/*` 意图事件强制"先读后写/改"与文件版本新鲜度校验（拦截未读覆写 `FS_NOT_OBSERVED` 与过时修改 `FS_STALE_VERSION`）；注：在当前 `dsh-base` 默认配置中以 `disabled: true` 关闭（[`cordis.patch.yml:292-294`](packages/bundle/base/cordis.patch.yml#L292-L294)），需强一致保护时可配置开启。

---

## 6. 跨层协同优化：Subagent Fork 与 Plan Mode

除上述四层自身机制外，`deepseek-harness` 在多 Agent 编排和工作模式切换上也围绕 **KV-cache 前缀复用** 做了专门约束：

1. **缓存友好的子代理 Fork（[`subagent-fork-in-process`](packages/subagent/subagent-fork-in-process/README.md#L118-L145) & [`cordis.patch.yml:397-405`](packages/bundle/base/cordis.patch.yml#L397-L405)）**：
   - Fork 子代理直接继承父会话截至上一个已闭合 turn 的平衡历史前缀。
   - **刻意禁用子代理侧的 `provider/model` 选择参数**，强制与父代理共用模型与路由；**不向系统提示词追加子代理专属 section，也不增删消息工具 schema**（父子 ID 和回复指引统一放在初始用户任务中跟在继承前缀之后），使子代理首次请求即直接命中父会话的 KV cache。
2. **Plan Mode 缓存稳定策略（[`plan-mode`](packages/plan/plan-mode/README.md) & [`cordis.patch.yml:336-351`](packages/bundle/base/cordis.patch.yml#L336-L351)）**：
   - 进入 Plan Mode 时不从工具目录中移除写文件/执行命令工具（仅通过 `order: 500` 的 `plan:policy` 提示词软约束），防止进出规划阶段反复改变工具 schema 破坏前缀。
   - `exit_plan_mode` 采用单调渐进披露：首次进入 Plan Mode 时加入后，在该会话剩余生命周期内保持可见，这样后续再次开关 Plan Mode 时**工具集零变动**，仅在支持 `in-history` 的模型上追加一条系统节点即可完成模式切换！

---

## 7. 可复用技术清单

按**性价比与通用性**排序（标注适用范围）：

| # | 技术 | 层次 | 一句话总结与适用范围 |
| --- | --- | --- | --- |
| 1 | 思维链与签名规范回传 | 模型调用 | Messages 协议全轮次回传 `thinking` + 版本化 `signature`（跨模型自动剥离）；Chat Completions 仅工具轮次回传 `reasoning_content` |
| 2 | 缓存友好的动态上下文分离 | 提示词 | 易变运行态（目录/权限/路径/URL）移出 system prompt，做成仅变化时追加的 `<system-reminder>` user 快照 |
| 3 | `in-history` 提示词与工具增量更新 | 调用/提示词 | 支持该能力的模型将中途变更的 `system` 提示词和 `defer_loading` 工具（`tool_addition`）后置追加，保活前缀 KV cache |
| 4 | 空响应与溢出错误码归一化 | 模型调用 | 空 `stop` 完成转 `EMPTY_RESPONSE` 重试；上下文超长/限流/鉴权归类为稳定错误码供上层路由 |
| 5 | 缓存感知的互斥 token 计费 | 模型调用 | 明确区分 `inputTokens`（未缓存输入）、`cacheReadTokens`、`cacheWriteTokens`、`outputTokens` 互斥四桶 |
| 6 | 预算感知的压缩触发与两段式压缩 | 上下文 | 扣除预留输出与 headroom 计算触发阈值；先零成本裁剪工具结果，再 LLM 摘要；切小模型前预压缩 |
| 7 | summarizer 前缀复用 + route 校验 | 上下文 | 摘要沿用主会话系统提示、工具和待压区间，最后追加 `user` 指令复用缓存；并用路由真定价做收敛校验 |
| 8 | 工具结果 head/tail 裁剪 + 落盘 | 上下文 | 确定性按 code-point 保头尾插占位；超大图文结果按共享 token 预算 `spill` 到文件并返回检索路径 |
| 9 | 图片网格归一化 + Files 复用与降级 | 模型调用/上下文 | 按模型视觉网格缩放锁死历史字节；Files 上传单飞复用 + 超时指数跳过回退内联 + 高水位步进持久化卸载 |
| 10 | 混合 token 计量 | 上下文 | 4 字符启发式 + 路由视觉定价，信封一致时锚定 provider 真实 `usage` 消除 CJK/Schema 偏差 |
| 11 | 工具输出契约分离与白名单投影 | 工具 | `execute` 返规范 JSON、`render` 产模型文本、`presentationMeta` 供 UI；wire 只透出 `name/description/parameters/deferLoading` |
| 12 | 确定性排序与严格 Schema 子集 | 提示词/工具 | code-unit 字典序保证跨机字节一致；Schema 关键字白名单且未实现即 reject |
| 13 | PTC（`run_code` + TypeScript SDK） | 工具 | 将多步循环与大中间数据留在执行沙盒，只把 `inspect` 提取的精简结果放回对话历史 |
| 14 | 重复调用守卫与并发安全池 | 工具/护栏 | 连续同参调用第 3/5/8 次注入纠偏提醒不阻断；`isConcurrencySafe` 自动并发只读工具、屏障隔离写工具 |
| 15 | 畸形/空内容 wire 兜底 | 模型调用 | 历史坏参数转 `{}` 保留配对，保留空 `tool_result`、合并邻接 `user`/`tool` 角色消息、绝不发 `null` |
| 16 | 事件溯源 + 5 类 surface 派生 | 上下文 | `Model-visible ⟺ logged`，所有裁剪、摘要、图片卸载与工具新增均为日志上的可重建投影 |
| 17 | 缓存友好的 Subagent Fork | 编排 | Fork 子代理锁定相同路由、工具与前缀，把任务与身份差异置于继承前缀之后直接命中 KV cache |
| 18 | 重试退避 + 看门狗排空 | 模型调用 | 每 provider 独立退避（默认 5 次/500ms~10s/0.1 抖动/`Retry-After`）；流看门狗配合心跳保活与异步排空 |
| 19 | `purpose` 分级控思考 | 模型调用 | 通过 `ConfigureCall` 让标题生成等短输出辅助调用自动选取最低思考档位（`off`），保住 `maxTokens` |
| 20 | 不劈开工具调用配对 | 上下文 | 压缩、Fork 与截断边界始终对齐工具调用/结果平衡点，并做崩溃/取消后的合成错误结果修复 |

---

## 8. 移植指南

把上述优化移植到**你自己的 Agent**，建议按依赖关系和 ROI 分四步落地：

1. **第一步（模型调用层——改动最小、收益最直接）**：
   - 明确区分接入协议：Messages API 全轮次回传 `thinking` + `signature`（跨模型剥离）并直接读取互斥缓存四桶；Chat Completions 仅工具轮次回传 `reasoning_content` 并对 `prompt_tokens` 减去缓存命中。
   - 对空响应报错重试，对上下文溢出/限流归一化为稳定错误码，补齐 `Retry-After` + 有界指数退避与空闲看门狗。
   - 序列化层做好畸形工具参数 `{}` 兜底、空字段省略（不发 `null`）与相邻角色消息合并；辅助调用（如标题生成）通过策略回调自动切到最低/关闭思考档位。
   - 若支持多模态：尽早按目标模型视觉网格前置缩放图片，并按高水位淘汰旧图避免每次新增图片重写前缀。

2. **第二步（提示词与工具静态前缀稳定——最大化 KV-cache 命中）**：
   - 固定 system prompt 各 section 顺序与工具字典序（locale 无关），把 cwd、权限、服务 URL、源码路径等易变信息抽离到后置的 `user` 角色 `<system-reminder>` 快照中，且仅在结构化内容真正变化时追加。
   - 确保 Schema 只保留实际校验过的关键字子集，隔离 `execute`/UI 元数据不进 wire；切模式（如 Plan Mode）尽量不增删工具列表，优先用提示词软约束。
   - 如果底层模型（如 DeepSeek V4.1 Flash）支持 `in-history` system 与 `addition-only` tool 更新，再引入对话中追加 `system` 节点与 `defer_loading` 增量工具。

3. **第三步（工具执行护栏与上下文控膨）**：
   - 为工具加上连续重复调用提醒（对规范化参数计数、在第 3/5 次注入非阻断引导）和只读并发标记（`isConcurrencySafe`）。
   - 对超大工具输出实施头尾保留裁剪 + `spill` 落盘，返回可分页读取的文件路径（记得对 `read` 工具自身豁免防循环）。
   - 对重数据过滤、批量聚合类任务可考虑引入 PTC（单 `run_code` + 类型化 SDK），把中间数据留在沙盒外而不进上下文。

4. **第四步（上下文压缩与架构底座）**：
   - 建议先将消息列表重构为 append-only 事件日志 + 派生 surface（至少确保替换操作可追溯、不可改写原记录，且裁剪/压缩边界不拆散 tool-call/result 对）。
   - 计算压缩阈值时务必先从 `contextWindow` 扣除预留输出 `maxTokens` 与安全 headroom；实施"先零成本工具裁剪、再保前缀 LLM 摘要"的两段式压缩，摘要调用复用主会话前缀并将指令追加为最后一条 `user` 消息，落地前执行路由定价下的严格收敛校验。
   - 若实现基于事件日志的 Fork 子代理，保持子代理模型、系统前缀与工具集与父代完全一致以继承缓存命中。

> **注**：文中涉及的默认目录模型（`deepseek-flash` 即 `DeepSeek-V4.1-Flash`、`deepseek-v4-pro` 即 `DeepSeek-V4-Pro`、官方 1,048,576 上下文窗口、默认 `defaultContextWindow = 1,000,000`、默认 `maxTokens = 256,000`）均核自 `deepseek-harness` commit `39163dd30f` 源码默认值；移植时请结合目标部署环境的实际模型规格调整阈值与能力声明。
