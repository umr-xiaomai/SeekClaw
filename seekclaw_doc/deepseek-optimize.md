# DeepSeek 官方 Agent 对 DeepSeek 模型的优化技术分析

> 本文是对 `deepseek-harness`（`@deepseek-ai/dsh-*`）如何优化 DeepSeek 模型使用的深度技术分析，目标是提炼出**可移植到其他 Agent 的具体优化技术**。每一节先说明机制，再说明收益，最后给出移植要点。
>
> 文中的 `file:line` 引用以仓库内相对路径给出，可点击跳转。数值为代码中的默认常量。

## 目录

- [1. 总览：四个优化层次](#1-总览四个优化层次)
- [2. 模型调用层（DeepSeek wire 层）](#2-模型调用层deepseek-wire-层)
- [3. 提示词工程层](#3-提示词工程层)
- [4. 上下文管理层](#4-上下文管理层)
- [5. 工具设计层](#5-工具设计层)
- [6. 可复用技术清单](#6-可复用技术清单)
- [7. 移植指南](#7-移植指南)

---

## 1. 总览：四个优化层次

DeepSeek 官方 Agent 对模型的优化不是散落在某处的一堆技巧，而是**贯穿四个层次的一整套设计**，每一层都在为同一个目标服务：**在保持模型输出质量的前提下，最大化 KV-cache 命中率、最小化 token 开销、提升长对话与工具调用的稳定性**。

| 层次 | 归属包 | 核心优化 |
| --- | --- | --- |
| 模型调用层 | `packages/llm/llm-deepseek` | 思考模式、CoT 回传、缓存计费、空响应重试、上下文溢出分类、重试退避 |
| 提示词工程层 | `packages/core/system-prompt` | 有序 section 组装、动态上下文 cache-safe、严格插值、确定性排序 |
| 上下文管理层 | `packages/compaction` + `packages/llm/token-meter` | 两段式压缩、summarizer 复用前缀、收敛校验、工具结果裁剪、混合 token 计量 |
| 工具设计层 | `packages/core/tools` | 类型驱动 DSL、强制 schema 子集、白名单投影、呈现意图正交 |

贯穿四层的一条主线是 **KV-cache 前缀稳定**：任何会进入模型请求的内容，要么保持逐字节稳定（system prompt、工具 schema、历史前缀），要么被刻意隔离到前缀之后（动态上下文做成 user-role 快照、摘要指令追加为最后一条 user 消息），从而不破坏可复用的缓存前缀。

---

## 2. 模型调用层（DeepSeek wire 层）

这一层的源码集中在 [`packages/llm/llm-deepseek/src/`](packages/llm/llm-deepseek/src/)。适配器是"纯传输层"：连接事实（baseURL、key、默认值）每次操作通过 thunk 解析一次，不在加载时冻结。

### 2.1 思考模式与推理强度（thinking / reasoning_effort）

DeepSeek 有两个相关的 wire 字段（[`serialize.ts:171-186`](packages/llm/llm-deepseek/src/serialize.ts#L171-L186)）：

- `thinking: { type: 'enabled' | 'disabled' }` —— 顶层开关（**不是** `extra_body`）。
- `reasoning_effort: 'high' | 'max'` —— 官方只支持 `off/high/max` 三档；`low`/`medium` 服务端映射到 `high`。

Adapter 把 harness 的 `reasoningEffort` 解析成 wire 字段（[`serialize.ts:26-53`](packages/llm/llm-deepseek/src/serialize.ts#L26-L53)）：

- `off` → `thinking: {type:'disabled'}`，且**不发** `reasoning_effort`（绝不把 `off` 作为 wire 值发出）。
- `high` / `max` → `thinking: {type:'enabled'}` + `reasoning_effort`。
- 未显式指定时回退到部署默认，省略则 provider 默认。

**收益**：把"用不用思考、思考多深"做成显式的、可配置的模型能力，而不是隐藏在提示词里。未支持的 effort 值在发请求**之前**抛 `UNSUPPORTED_REASONING_EFFORT`，避免把无效参数打到线上。

**移植要点**：为模型抽象出"推理强度"这一独立维度，让调用方显式选择，adapter 负责映射到 provider 的具体字段；非法值在 I/O 前拒绝，而不是发出去等 400。

### 2.2 思维链回传的 token 优化（CoT passback）

这是最值得注意的一个**直接省 token** 的设计。DeepSeek 思考模式下，`reasoning_content`（思维链）的回传规则（[`serialize.ts:71-102`](packages/llm/llm-deepseek/src/serialize.ts#L71-L102)、[`types.ts:66-72`](packages/llm/llm-deepseek/src/types.ts#L66-L72)）：

- **带工具调用的 assistant turn**：必须把 `reasoning_content` 回传到历史里（API 要求，否则报错）。
- **无工具调用的纯文本 turn**：**丢弃** `reasoning_content`，不回传——因为服务端会忽略它，回传纯粹是浪费 token。

**收益**：工具往返的轮次必须携带思维链（保正确性），普通轮次不携带（省 token）。README 明确说明这是"conditional reasoning passback increases tool-round-trip context, while dropping other reasoning avoids paying those tokens again"。

**移植要点**：区分"含工具调用的 assistant 消息"和"纯文本消息"，只在前者回传思维链。这是 DeepSeek 特有的省 token 点，大多数通用 Agent 会无条件回传思维链或无条件丢弃。

### 2.3 缓存感知的 token 计费（cache accounting）

DeepSeek 的 `prompt_tokens` **包含**缓存命中（`prompt_tokens = prompt_cache_hit_tokens + prompt_cache_miss_tokens`）。harness 约定所有 token 计数是**互斥（disjoint）**的，于是 [`mapUsage`](packages/llm/llm-deepseek/src/translate.ts#L53-L62) 做减法：

```ts
inputTokens = prompt_tokens - cacheReadTokens
cacheReadTokens = prompt_tokens_details.cached_tokens ?? prompt_cache_hit_tokens
```

**收益**：让上层（token 计量、成本展示、压缩决策）拿到"未缓存的真实输入 token"，而不是被缓存命中虚高的总数。DeepSeek 不报 cache-write 指标，所以没有 `cacheWriteTokens`。

**移植要点**：如果你的 Agent 要按 token 计费或做成本优化，务必理解 provider 的 usage 字段语义（是否把缓存命中折叠进 `prompt_tokens`），并归一化成互斥计数，否则成本估算和压缩阈值会系统性偏大。

### 2.4 空响应重试（empty response）

一个 `stop`（或缺失）finish 但**没有产生任何 content block** 的完成，被判定为退化输出，映射为 `finish {kind:'error'}` + `EMPTY_RESPONSE` 码（[`translate.ts:107-116`](packages/llm/llm-deepseek/src/translate.ts#L107-L116)），并默认进入重试。

**收益**：把"模型静默成功但实际没输出"从无声成功变成可重试的失败——否则一个空完成会被当成正常消息记录，后续所有轮次都可能因此出错。

**移植要点**：在流结束时检查"是否真的产出了内容"，空完成当可重试错误处理，不要当正常成功。

### 2.5 上下文溢出分类（context window overflow）

DeepSeek 用 400 + 错误正文表达"上下文超长"。adapter 通过 `isContextWindowExceededError()` 检查 provider 的 code/type/message，归一化为规范的 `CONTEXT_WINDOW_EXCEEDED` 码（[`adapter.ts:138-149`](packages/llm/llm-deepseek/src/adapter.ts#L138-L149)），而不是依赖具体的错误文本。

**收益**：上层（压缩插件）**只路由错误码，不解析 provider 文本**——这是能对溢出做出自动压缩 + 重试响应的前提（见 [4.3](#43-上下文溢出的强制缩减)）。

**移植要点**：把"上下文超长"这类语义错误，从 provider 的文本细节中抽出来，映射成稳定的机器可路由错误码。

### 2.6 流式 + usage

始终 `stream: true` 且 `stream_options: { include_usage: true }`（[`serialize.ts:174-176`](packages/llm/llm-deepseek/src/serialize.ts#L174-L176)）。usage 可能附在 finish chunk 上，也可能作为 trailing usage-only chunk 出现，translator 把两者都推迟到 `[DONE]` 哨兵之后、`finish` 之前输出，保证 `usage` 永远先于 `finish`、`finish` 之后无内容（[`translate.ts:101-117`](packages/llm/llm-deepseek/src/translate.ts#L101-L117)）。

**收益**：流式输出同时拿到精确 token usage，且遵守"usage 先于 finish"的顺序契约，避免下游拿到不完整或乱序的 usage。

### 2.7 空工具输出兜底与 content 规则

- 空工具结果：`flattenText(result.content) || '(no output)'`（[`serialize.ts:132-138`](packages/llm/llm-deepseek/src/serialize.ts#L132-L138)）——wire 上 `role:'tool'` 消息不能空。
- 纯工具调用 turn：`content: ""` **永远不是 `null`**（[`serialize.ts:86-95`](packages/llm/llm-deepseek/src/serialize.ts#L86-L95)）——某些网关对 null 直接 400。

**收益**：这些是 DeepSeek 网关的**边界约束**，违反会导致 400 甚至"brick 整个 session"（因为 null 消息已持久化在日志里，后续每一轮都会复现 400）。

**移植要点**：对接任何模型网关前，先摸清它对空 content、空 tool 结果的容忍度，并在序列化层兜底，而不是让空值进入历史。

### 2.8 可选字段省略而非发 null

序列化时所有可选字段（`temperature`、`max_tokens`、`stop`、`tools`、thinking 字段）都是"缺失即省略"，绝不发 `null`（[`serialize.ts:182-185`](packages/llm/llm-deepseek/src/serialize.ts#L182-L185)）。

**收益**：让 provider 的默认值生效，避免用显式 null 覆盖服务端默认、引发意外行为。

### 2.9 空闲看门狗（idle watchdog）

每个流的读操作有 `streamIdleTimeoutMs`（默认 5 分钟）的看门狗，只在 `iterator.next()` 挂起时计时，超时映射为 `TIMEOUT`，早于它的调用方取消映射为 `ABORTED`（[`adapter.ts:227-235`](packages/llm/llm-deepseek/src/adapter.ts#L227-L235)）。SSE 注释会重新武装看门狗（作为传输活动），但永不成为 `StreamChunk` 或日志事件。

**收益**：有界化 provider 挂起，防止一个卡住的流无限占用资源。

### 2.10 重试退避策略

重试由 provider 侧的 `retryPolicy` 声明、`dsh-llm-retry` 在 agent 的 `agent/request-error` 扩展点执行。默认（[`retry-policy.ts:14-24`](packages/llm/llm/src/retry-policy.ts#L14-L24)）：

- 模式 `normal`：有界指数退避 + 对称抖动。初始 500ms、上限 10s、抖动比 0.1、最多 2 次重试。
- 重试码：`EMPTY_RESPONSE`、`RATE_LIMIT`、`SERVER`、`TIMEOUT`、`TRANSPORT`。
- **provider `Retry-After` 头优先**：如果 provider 给了 `Retry-After`（秒或日期），用它；普通模式下若 provider 延迟超过 `maxDelayMs`，直接放弃重试（[`llm-retry/src/index.ts:194-205`](packages/llm/llm-retry/src/index.ts#L194-L205)）。
- 模式 `always`：无上限重试，直到成功、取消或销毁。

每次重试在等待前**先把 `llm/retry` 事件持久化**，可重放（[`llm-retry/src/index.ts:150-153`](packages/llm/llm-retry/src/index.ts#L150-L153)）。

**收益**：区分"可重试的瞬时错误"和"不可重试的语义错误"，退避 + 抖动避免重试风暴，尊重 provider 的限流提示。

**移植要点**：把重试策略做成**每个 provider 独立声明**的配置，而不是全局硬编码；重试前尊重 `Retry-After`；退避指数封顶 + 对称抖动。

### 2.11 每请求配置解析 + 连接/凭证同快照

连接事实（baseURL、catalog、默认值、空闲预算）**每次操作重新解析**，配置变更在下一个请求即生效，无需重启；而一个 in-flight 流保持它开始时的事实不变。凭证 key 从**同一个已解析快照**里解析，保证"endpoint 和发给它的 key 永远来自同一代配置"，不会出现"新 key 配旧 endpoint"（[`adapter.ts:214-222`](packages/llm/llm-deepseek/src/adapter.ts#L214-L222)）。

**收益**：热更新配置 + 杜绝配置代际错配。

### 2.12 purpose 路由（session-title 关闭思考）

`GenerateOptions.purpose` 有 `'compaction' | 'session-title'` 两个辅助调用分类。`session-title` 会**强制 `thinking: disabled`** 并省略已解析的 effort（[`serialize.ts:37-38`](packages/llm/llm-deepseek/src/serialize.ts#L37-L38)）。`compaction` 额外携带 `x-deepseek-harness-compact: 1` 头（[`adapter.ts:292-294`](packages/llm/llm-deepseek/src/adapter.ts#L292-L294)）。

**收益**：给标题生成这类"有明确短输出预算"的辅助调用关闭思考，把有限的 `maxTokens` 留给可见文本，而不是被思维链吃掉。同时让主机能区分压缩流量和对话流量。

---

## 3. 提示词工程层

### 3.1 有序 section 组装

系统提示词由多个 `PromptSection` 按 `order` 升序拼接（[`system-prompt/src/index.ts:56-60`](packages/core/system-prompt/src/index.ts#L56-L60)）。约定：

| order | 归属 |
| --- | --- |
| `-100` | harness identity（固定开场白 `"You are an AI agent powered by DeepSeek Harness."`） |
| `-99` | harness 源码路径声明 |
| `0` | `deployment:persona`（部署人格，可被 scope 遮蔽） |
| `99` | `tools:code-only`（code 模式规则） |
| `100–199` | 各工具的跨调用引导 |

工具的引导文案是**与执行器同源的谓词生成的**——例如 `tool:read` 的 `"Use the read tool — not shell commands like cat — to inspect text files..."`、`tool:bash` 的 `"Check the [exit code: N] marker on every bash result..."`。关键约束：**提示词不会声明 registry 未强制执行的规则**（[`tools/src/index.ts:859-861`](packages/core/tools/src/index.ts#L859-L861)）。

**收益**：section 化让提示词可组合、可遮蔽、可排序；prompt 文案与执行器同源，避免"prompt 承诺了但代码没做"的漂移。

### 3.2 动态上下文 cache-safe（KV-cache 前缀稳定）

这是提示词层最重要的一个设计。动态运行时上下文（sandbox 文件策略、审批策略、子代理权限声明等）**不是**写进 system prompt，而是做成 **user-role 快照**，放在 system prompt 之外、历史消息之后（[`runtime-context.ts`](packages/core/agent-loop/src/runtime-context.ts)）。

原因（源码注释直接点出）：**政策切换不应该重写稳定的 system-prompt 缓存前缀**。因为 system prompt 是 KV-cache 前缀的最前面一段，任何改动都会使整个前缀从第一个变化 token 起失效。把易变的上下文放到前缀之后的 user 消息里，就保护了前缀。

配套的 `RuntimeContextProjection` 只在快照**文本真正变化时**才投影一条新 user 消息；不变则零追加；为空时投影固定的 `CLEARED` 标记（`"Current runtime context: none..."`），保证"清空"状态也被记录。快照自带 `"This snapshot supersedes earlier runtime-context snapshots."` 前缀。

**收益**：KV-cache 前缀稳定 → 缓存命中率大幅提升 → 长对话成本降低；同时动态状态只在变化时占用 token，不变时零开销。

**移植要点**：把"稳定不变的指令"和"随会话变化的动态状态"分离到不同的消息角色/位置，前者固定在前缀，后者追加在后缀且只在变化时更新。这是对任何带 KV-cache 的模型都通用的省成本手段。

### 3.3 严格插值（fail loud）

`{{variable}}` 插值（[`system-prompt/src/index.ts:258-295`](packages/core/system-prompt/src/index.ts#L258-L295)）：

- 未知变量、注册了但本 assembly 无值（`undefined`）、malformed 完整 `{{…}}` 组都**抛错**。
- 用 `Object.hasOwn` 查表，堵住 `{{constructor}}` 之类原型链污染。
- 替换值不二次扫描，防止注入式二次展开。

**收益**：宁可装配失败，也不把坏提示词发给模型（坏提示词会产生难以排查的模型行为问题）。

### 3.4 complete section

`PromptSection.complete?: true` 让一个 section（通常是 persona）**独占整个 system prompt**，其它 section（identity、tool guidance）全部被压制，但 assembly 仍会跑完整 waterfall 解析 tools/contexts/variables（[`system-prompt/src/index.ts:67-75`](packages/core/system-prompt/src/index.ts#L67-L75)）。

**收益**：特定 agent 可以完全控制它的 system prompt，移除所有不想要的 token。

---

## 4. 上下文管理层

### 4.1 事件溯源 + 派生历史

这是 harness 的架构根基。`Session` 是**只追加的事件日志**，LLM 消息历史是**派生**的（`deriveMessages()`），不是独立存储的（[`packages/core/session/src/index.ts:726-747`](packages/core/session/src/index.ts#L726-L747)）。日志之上有一个 `surface` 视图——只有三类事件能产生模型消息：`user/message`、`assistant/message`、`tool/result`（[`session/src/types.ts:343-346`](packages/core/session/src/types.ts#L343-L346)）。

每个 surface 节点只在首次出现时投影一次，返回的 `Message` 对象是 deep-frozen 且**共享**的（复用日志里已冻结的数据，无需二次深拷贝）；`replaceGeneration` 变化时整体重建。

**收益**：

- **模型可见 ⟺ 已记录**：任何进入模型请求的内容都能从日志重建，这是"请求可重建"和"压缩可替换"的基础。
- 原始 chunk 保留 replay 和 UI 保真度，而派生历史保证模型看到的是干净的语义序列。
- 深冻结共享避免重复克隆的内存/CPU 开销。

### 4.2 压缩触发与阈值

自动压缩由 `compaction-basic` 在两个扩展点触发（[`compaction-basic/src/index.ts`](packages/compaction/compaction-basic/src/index.ts)）：

- **压力触发**（`agent/pre-step`，每个 step 边界）：阈值 = `floor(contextWindow × 0.8)`。`totalTokens` 超过阈值才进入压缩。
- **上下文溢出触发**（`agent/request-error`）：错误码为 `CONTEXT_WINDOW_EXCEEDED` 时强制压缩。

默认常量（[`compaction-basic/src/config.ts`](packages/compaction/compaction-basic/src/config.ts)）：`thresholdRatio=0.8`、`retainRatio=0.16`、`maxTokens=8192`（摘要生成上限）、`compactionRetries=1`、`maxOverflowRetries=1`。

保留尾部策略（[`region.ts:98-134`](packages/compaction/compaction-basic/src/region.ts#L98-L134)）：从 surface 末尾向前累加 token 直到 `retainTokens = floor(contextWindow × 0.16)`，得到 `keepFromIdx`，压缩区间是 `[第一个节点, keepFromIdx-1]`。**绝不劈开 tool-call/result 对**——若边界落在配对中间，向前回退直到配对平衡。

### 4.3 两段式压缩 + 溢出强制缩减

压缩分两段（[`compaction-basic/src/index.ts:308-312`](packages/compaction/compaction-basic/src/index.ts#L308-L312)）：

1. **先做无模型、确定性的工具结果裁剪**（便宜、可耐久落地）。
2. 重新计量后**仍超阈值**，才花一次 LLM 调用做摘要压缩。

上下文溢出时（[`index.ts:283-291`](packages/compaction/compaction-basic/src/index.ts#L283-L291)）：**绕过阈值与保留尾部策略**，先裁剪工具结果，再以 `retainTokens=0` 强制压缩一段"有用"历史；只要 surface 有进展就返回 `{kind:'retry'}` 重试原请求，`maxOverflowRetries` 防死循环。

**收益**：便宜的确定性裁剪优先，昂贵的 LLM 摘要靠后——这是"先花小成本，不够再花大成本"的分层策略。溢出时能自动恢复，而不是直接失败给用户。

### 4.4 summarizer 复用 KV-cache 前缀

摘要指令（`COMPACTION_INSTRUCTION`）**不是**独立的 system prompt，而是作为**最后一条 user 消息**追加在被重放的历史之后（[`summarizer.ts:24-30`](packages/compaction/compaction-basic/src/summarizer.ts#L24-L30)）。这样辅助调用是上一次请求的**真正前缀**，可复用 provider 的 KV cache。

提示词要求模型以 `compaction engine` 身份，把对话浓缩成固定 8 段结构化 Markdown checkpoint：Primary Request and Intent / Key Technical Concepts / Files and Code / Errors and Fixes / Pending Jobs / Current Work / Next Step / Critical Context（缺省写 `(none)`，不得删段）。硬规则包括：保留精确路径/命令/报错串/标识符/数值/函数签名；若历史里已有 `<compacted-summary>` 块，不得原文照抄，而要**合并去陈旧**（[`summarizer.ts:60-65`](packages/compaction/compaction-basic/src/summarizer.ts#L60-L65)）。

摘要落地时被 `frameSummary` 包裹成 `<compacted-summary>...</compacted-summary>` + `CHECKPOINT_PREAMBLE`（声明这是自动生成的 checkpoint，让模型把捕获内容当既成背景继续，不再复述），作为一条 `user/message` 通过 `surfaceOp: {op:'replace', start, end}` 原子替换被遮蔽区间。

**收益**：结构化摘要比自由文本摘要更能保留"后续推理真正需要"的细节（路径、报错串、约束、待办）；前缀复用让摘要调用本身也享受缓存。

### 4.5 收敛校验

落地摘要前强制校验：**新摘要的估算 token 数必须严格小于被遮蔽内容的 token 数**，否则抛错拒绝落地（[`region.ts:373-378`](packages/compaction/compaction-basic/src/region.ts#L373-L378)）。

**收益**：保证压缩永远"缩小"上下文，不会用一个更长的摘要替换更短的历史，导致死循环或无意义的 token 增长。

### 4.6 工具结果裁剪（head/middle/tail）

`ToolResultPruner` 是**无模型、确定性**的裁剪（[`compaction-tool-result-pruner/src/index.ts`](packages/compaction/compaction-tool-result-pruner/src/index.ts)）：默认 `thresholdChars=8192`、`headChars=4096`、`tailChars=1024`（Unicode code point，非 UTF-16 code unit，不会切断 surrogate 对）。只在文本总长超阈值时裁剪，保留头尾，中间插入一次 `'[... tool result middle pruned ...]'` 标记，非文本块原样保留顺序。

每次替换前追加一条 `compaction/prune` shadow-price 事件，用 token 计量对被遮蔽节点定价，纯消费者无需逐节点状态即可正确递减总量（shadow-price 协议）。

**收益**：把超大工具输出（如 `grep` 整个仓库、`cat` 大文件）的头尾保留、中间裁剪，保住最有信息量的部分，同时确定性、零模型成本。

### 4.7 混合 token 计量

`TokenMeter` **不使用真实 tokenizer**，而是固定密度启发式（[`estimate.ts:13-19`](packages/llm/token-meter/src/estimate.ts#L13-L19)）：`CHARS_PER_TOKEN=4`（每 4 字符 ≈ 1 token）、`BLOCK_OVERHEAD=4`、`ROLE_OVERHEAD=4`。

关键是**混合锚定**（[`index.ts:116-147`](packages/llm/token-meter/src/index.ts#L116-L147)）：仅当最近一次成功请求的 canonical request envelope 与当前完全一致、且 provider 上报总量 >= 完整启发式锚点时，才复用 provider 的 `usage` 作为 baseline；否则退回全量启发式估算。用"带符号 surface 增量"衔接估算与真实用量。

README 明确承认该启发式**系统性低估 CJK 文本与 JSON schema**，因此只作近似，压缩决策读 `measure()` 而非投影值。

**收益**：日常计量零成本（不跑 tokenizer），有精确 provider 用量时自动锚定校准，兼顾准确与性能。

**移植要点**：如果不想引入 tokenizer 依赖，可以用"字符数启发式 + provider 真实 usage 锚定"的混合方案；但要清楚启发式的偏差方向（CJK 低估），并让决策层知道哪些字段是近似的。

### 4.8 动态上下文注入

`packages/context/` 提供四类注入（都通过 `agent/pre-step` 注入为 user-role 消息）：

1. **agent-instructions**（默认捆绑）：注入工作区 `AGENTS.md`/`CLAUDE.md` 及 `.local` overlay，发现链为全局 → 项目根 → root-to-cwd 各级目录。字节预算 `maxBytes`（必填），单文件读取上限 `maxSourceBytes` 默认 1MB；超预算用二分截断最具体文件，包裹 `<system-reminder>`。
2. **time-context**（可选）：当前时间戳、时区、距上条消息耗时。
3. **tmux-context**（可选）：tmux session/window/pane 布局。
4. **session-reference**（可选）：其它 session 的只读快照，每消息最多 3 个引用，单快照 JSON 预算 65536 字节，超预算按"保留最新 + 丢弃非 checkpoint 中间 + 二分截断最长文本"裁剪。

### 4.9 spill 落盘

`spill`（可选能力）把超大工具输出落盘为私有文件，仅向模型返回 **locator + 检索提示**（提示模型用 `read`/`grep` 读该路径），与裁剪互补。

**收益**：把"上下文里放不下的大对象"变成"一个路径 + 按需检索"，而不是硬塞进上下文或直接丢弃。

---

## 5. 工具设计层

### 5.1 类型驱动 DSL（单一事实来源）

工具 schema 用 `defineTool()` 的 DSL 书写，**一份声明同时产生三样东西**（[`schema.ts`](packages/core/tools/src/schema.ts)）：

- `InferArgs`/`InferValue` → 静态 TypeScript 类型（`execute` 拿到窄化类型）。
- `parameterSchemaSpecToJsonSchema` → wire JSON Schema。
- `validateJsonSchemaValue` → 运行时值校验。

**收益**：消除"类型、schema、校验"三处漂移；作者写一次，其余自动推导。

### 5.2 强制 JSON Schema 子集 + 显式 reject

`assertSupportedJsonSchema()` 对任何外部 schema（MCP、subagent、workflow）做白名单校验：只允许 `type/oneOf/properties/required/additionalProperties/items/enum/const` + 四个 annotation，**不支持的/misplaced 关键字直接 reject**，而不是静默接受（[`json-schema.ts:1-12`](packages/core/tools/src/json-schema.ts#L1-L12)）。

annotation（`description/title/default/examples`）与约束分离：annotation 只进模型和类型，**不参与运行时校验**。

**收益**：绝不接受"写进了 schema 但没实现校验"的字段——否则模型会依赖一个根本没有被强制执行的约束。

### 5.3 确定性排序

工具 schema 排序（[`system-prompt/src/index.ts:164-183`](packages/core/system-prompt/src/index.ts#L164-L183)）：

- 默认 **lexicographic（字典序）**，用 **locale 无关的 code-unit 比较**，保证所有机器上顺序一致。
- 可配置 `toolOrder`（必须恰好包含一次 `<unlisted-tools>` rest 标记），未列出工具在 rest 位置按字典序插入。

**收益**：同一工具集在任意机器产生逐字节一致的提示词 → 对 KV-cache 复用和快照测试都至关重要。

### 5.4 白名单投影（防泄漏）

`ToolDefinition` 含 `name/description/parameters/execute/finalizeContent/timeoutMs/isConcurrencySafe/presentCall/presentResult` 等字段，但 `schemas()` **只透出 `name/description/parameters` 三个字段**到模型请求（[`index.ts:1256-1267`](packages/core/tools/src/index.ts#L1256-L1267)）。执行函数、超时、并发标志、呈现回调**绝不泄露**到 wire。

**收益**：模型只看到"它需要知道的"，不暴露实现细节、不影响缓存前缀（执行回调不是纯文本，一旦进 schema 会破坏前缀稳定性）。

### 5.5 呈现意图与模型契约正交

`presentation.ts` 定义 provider 中立的 card 词汇：`ToolCallView`（generic/terminal/diff）和 `ToolResultView`（generic/terminal/diff/search/read/web）。这是**纯展示层**，与模型可见 schema 正交。

关键工程约束：`presentCall`/`presentResult` 是**纯函数**（live 流式 + session 回放都会调用，不能读 session 状态/时钟/随机数），参数不合法时返回 `undefined` 回退到 generic 卡、**绝不 throw**（[`schema.ts:594-609`](packages/core/tools/src/schema.ts#L594-L609)）。UI 专属格式（fenced console、diff、相对路径）不得进入 canonical value 或模型 content。

**收益**：模型的输入（`output.render` 的 prose）和人的 UI（presentation card）分离，互不污染——模型不会看到 UI 噪音，UI 也不会因为旧参数回放而崩溃。

### 5.6 Code Mode（native/code/both）

`ToolPresentationMode = 'native'|'code'|'both'`（[`agent-tool-presentation/src/index.ts:37-72`](packages/core/agent-tool-presentation/src/index.ts#L37-L72)）：`native` 发全部可见 schema，`code` 只发 `run_code` + 生成的 SDK，`both` 两者都发。

Code Mode 下 schema 被二次投影为**可编译的 SDK**（TS/Python）：description 折叠转义、保留字/非法标识符降级为下标访问，保证 `mode:'code'` 下模型唯一的工具声明依然可解析。

**收益**：代码模式把"调用 N 个工具"压缩成"写一段代码"，显著减少工具 schema 的 token 开销（尤其工具多时）。

---

## 6. 可复用技术清单

按"最值得移植"排序：

| # | 技术 | 层次 | 一句话 |
| --- | --- | --- | --- |
| 1 | CoT 选择性回传 | 模型调用 | 工具轮次回传思维链，纯文本轮次丢弃省 token |
| 2 | 动态上下文 cache-safe | 提示词 | 易变上下文做成 user-role 后缀快照，保护 KV-cache 前缀 |
| 3 | 空响应重试 | 模型调用 | 空完成当可重试错误，不当正常成功 |
| 4 | 上下文溢出分类 + 自动恢复 | 模型调用/上下文 | 溢出映射成稳定错误码 → 自动压缩 + 重试 |
| 5 | 两段式压缩 | 上下文 | 先确定性裁剪，不够再 LLM 摘要 |
| 6 | summarizer 前缀复用 | 上下文 | 摘要指令追加为最后 user 消息，复用缓存 |
| 7 | 收敛校验 | 上下文 | 新摘要必须比被遮蔽内容小，否则拒绝 |
| 8 | 工具结果 head/tail 裁剪 | 上下文 | 确定性保留头尾，无模型成本 |
| 9 | 混合 token 计量 | 上下文 | 字符启发式 + provider usage 锚定 |
| 10 | 缓存感知计费 | 模型调用 | prompt_tokens 减缓存命中，归一化互斥计数 |
| 11 | 重试退避 + Retry-After | 模型调用 | 有界指数退避 + 对称抖动，尊重限流头 |
| 12 | 类型驱动 DSL | 工具 | 一份 schema 同时产生类型/wire/校验 |
| 13 | 强制 schema 子集 | 工具 | 白名单关键字，未实现即 reject |
| 14 | 确定性排序 | 工具/提示词 | locale 无关 code-unit 比较，跨机器一致 |
| 15 | 白名单投影 | 工具 | 只透出 name/description/parameters |
| 16 | 事件溯源 + 派生历史 | 上下文 | 模型可见 ⟺ 已记录，历史是日志的纯函数 |
| 17 | 工具结果 wire 兜底 | 模型调用 | `(no output)`、`content:""` 而非 null |
| 18 | 不劈开工具调用对 | 上下文 | 压缩/裁剪边界永远配对平衡 |
| 19 | purpose 路由 | 模型调用 | 辅助调用（标题）关闭思考省 token |
| 20 | 空闲看门狗 | 模型调用 | 有界化 provider 挂起 |

---

## 7. 移植指南

把上述优化移植到**你自己的 Agent**，建议按依赖关系分层推进：

**第一步（模型调用层，收益最直接、改动最小）**：

- 在序列化层加 CoT 选择性回传（区分工具轮次/纯文本轮次）。
- 空响应当可重试错误；上下文超长映射成稳定错误码。
- 归一化 token usage（减去缓存命中）。
- 重试策略独立声明，退避 + `Retry-After`。
- 空工具结果 / 空 content 兜底成 `(no output)` / `""`。

**第二步（提示词工程层）**：

- 系统提示词拆成有序 section，可组合、可遮蔽、可排序。
- 把"稳定指令"和"动态状态"分离，动态状态做成后缀快照、只在变化时更新。
- 工具 schema 确定性排序（locale 无关）。

**第三步（上下文管理层，需要日志/会话架构支撑）**：

- 若要压缩：先确定性裁剪（工具结果 head/tail），再 LLM 摘要；摘要指令追加为最后 user 消息复用缓存；落地前做收敛校验。
- 混合 token 计量（字符启发式 + provider usage 锚定）。

**第四步（工具设计层）**：

- 类型驱动 DSL（单一事实来源）。
- 强制 schema 子集 + 白名单投影。
- 呈现意图与模型契约正交。

**依赖关系提示**：第 4 层的"事件溯源 + 派生历史"是第 3 层压缩的基础——没有"模型可见 ⟺ 已记录"这一不变量，就无法安全地做"用摘要替换一段历史"。如果你的 Agent 现在是"直接维护一个 messages 数组"，要移植压缩，需要先引入一个 append-only 的日志/事件源，再从中派生历史。

**注意**：本文档中 `deepseek-v4-flash` / `deepseek-v4-pro`、1M 上下文、256k 输出上限等数值来自本仓库代码的默认配置（截至本文撰写时）。移植时以你的目标模型实际能力为准。
