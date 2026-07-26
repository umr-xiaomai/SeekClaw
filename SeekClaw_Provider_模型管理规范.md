# SeekClaw CLI - Provider & Model 管理规范（补充）

## 设计目标

SeekClaw Runtime 必须**原生内置完整的模型管理系统**，无需依赖 CC Switch
等外部工具。

目标是实现与 **Claude Code + CC Switch**
相同甚至更强的能力，但这些能力属于 Runtime 的 Provider
Layer，而不是独立工具。

------------------------------------------------------------------------

## Provider Management

设计统一接口：

``` csharp
IProviderManager
```

负责：

-   Provider 生命周期
-   Model 管理
-   API Key 管理
-   BaseUrl 管理
-   路由
-   故障转移（Failover）
-   自动切换
-   能力发现（Capability Discovery）
-   健康检查
-   成本统计

------------------------------------------------------------------------

## Provider 与 Model 解耦

Provider ≠ Model。

例如：

-   OpenAI
    -   GPT-5.5
    -   GPT-5.5-mini
-   Anthropic
    -   Claude Opus
    -   Claude Sonnet
-   Google
    -   Gemini Pro
    -   Gemini Flash
-   OpenRouter
-   Azure OpenAI
-   Ollama
-   LM Studio

运行时动态注册，禁止写死。

------------------------------------------------------------------------

## Model Registry

统一：

``` csharp
IModelRegistry
```

维护：

-   Provider
-   Model
-   Context Window
-   Max Output
-   Vision
-   Thinking
-   Tool Calling
-   JSON Mode
-   Streaming
-   Embedding
-   MCP 支持

------------------------------------------------------------------------

## 配置

统一配置：

    ~/.seekclaw/config.json

支持：

-   Provider
-   BaseUrl
-   API Key
-   Organization
-   Proxy
-   Timeout
-   Headers
-   Models

兼容所有 OpenAI Compatible API。

------------------------------------------------------------------------

## 命令

### Provider

    seekclaw provider list
    seekclaw provider add
    seekclaw provider remove
    seekclaw provider edit
    seekclaw provider test
    seekclaw provider use

### Model

    seekclaw model list
    seekclaw model use
    seekclaw model info
    seekclaw model search
    seekclaw model test

### Profile

    seekclaw profile list
    seekclaw profile create
    seekclaw profile use
    seekclaw profile delete

例如：

-   work
-   home
-   local

可一键切换整个运行环境。

------------------------------------------------------------------------

## Workspace Override

Workspace 可以覆盖：

-   Provider
-   Model
-   Temperature
-   Context
-   Permission
-   Skill
-   MCP

不同项目互不影响。

------------------------------------------------------------------------

## Context Adaptation

Runtime 根据模型自动调整：

-   Context Size
-   Chunk Size
-   Summary 长度
-   Memory 长度
-   Search 数量

禁止写死 Token 限制。

------------------------------------------------------------------------

## Capability Discovery

统一能力模型：

``` csharp
SupportsStreaming
SupportsThinking
SupportsVision
SupportsImage
SupportsToolCalling
SupportsJsonMode
SupportsReasoning
SupportsEmbedding
SupportsMcp
```

业务层禁止：

``` text
if(model=="Claude")
```

------------------------------------------------------------------------

## Routing

支持：

-   Fast
-   Balanced
-   Quality
-   Cheap
-   Offline

Runtime 自动根据策略选择模型。

------------------------------------------------------------------------

## Retry & Failover

支持：

-   Retry
-   Exponential Backoff
-   Jitter
-   Circuit Breaker

Fallback 示例：

Claude

↓

GPT-5.5

↓

Gemini

↓

OpenRouter

↓

Ollama

自动切换，无需用户干预。

------------------------------------------------------------------------

## Load Balance

支持：

-   Round Robin
-   Priority
-   Least Used
-   Lowest Cost
-   Fastest
-   Sticky

------------------------------------------------------------------------

## Cost & Usage

自动统计：

-   Prompt Tokens
-   Completion Tokens
-   Total Tokens
-   Cost
-   Response Time
-   Success Rate

支持：

    seekclaw usage
    seekclaw model stats

------------------------------------------------------------------------

## Health Check

后台持续检测：

-   Provider
-   API
-   Model

维护：

-   Online
-   Offline
-   Latency
-   Failure Rate

支持：

    seekclaw doctor

------------------------------------------------------------------------

## Interactive Switch

提供交互式模型切换：

    seekclaw switch

允许选择：

-   Provider
-   Model
-   Context
-   Thinking
-   Tool Calling
-   Strategy

无需编辑 JSON。

------------------------------------------------------------------------

## 最终要求

SeekClaw Runtime 必须原生实现类似 **Claude Code + CC Switch**
的全部模型管理能力，并进一步扩展：

-   多 Provider
-   多模型
-   多 Profile
-   Workspace 覆盖
-   自动 Context 适配
-   智能路由
-   自动容错
-   自动健康检查
-   成本统计
-   交互式切换

所有能力均由 Runtime 内部统一管理，而非依赖外部工具。
