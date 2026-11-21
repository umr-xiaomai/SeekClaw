# 配置参考

SeekClaw 使用全局配置保存 Provider、模型、Profile、路由和 Agent 默认值，并允许工作区提供少量覆盖项。Desktop 与 CLI 操作的是同一份配置。

## 文件位置

应用状态目录为 `~/.seekclaw`（Windows 上为 `%USERPROFILE%\.seekclaw`）。在项目中运行 `seekclaw init` 可查看其工作区状态目录。

| 文件 | 用途 |
| --- | --- |
| `~/.seekclaw/config.json` | 全局 Provider、模型、Profile、路由、Agent 和 MCP 配置 |
| `~/.seekclaw/state.json` | 上次 Session、轮询游标和禁用 Skill 等内部状态 |
| `~/.seekclaw/usage.jsonl` | 调用、Token、延迟和成本记录 |
| `~/.seekclaw/workspaces/<workspace-key>/config.json` | 当前项目的覆盖项 |

首次运行时，Runtime 使用代码内建的默认配置（`DefaultSeekClawConfig`）自动序列化生成 `~/.seekclaw/config.json`，仓库不再维护静态的默认配置文件。Provider 与模型数据随后完全由用户管理。

## 全局配置结构

下面示例使用当前实际字段：

```json
{
  "provider": "openai",
  "model": "gpt-5.5",
  "temperature": 0.2,
  "providers": [
    {
      "id": "openai",
      "name": "OpenAI",
      "kind": "openai",
      "baseUrl": "https://api.openai.com/v1",
      "apiKey": "sk-...",
      "proxy": null,
      "timeoutSeconds": 120,
      "headers": null,
      "enabled": true,
      "priority": 1,
      "models": [
        {
          "id": "gpt-5.5",
          "alias": null,
          "contextWindow": 400000,
          "maxOutput": 128000,
          "capabilities": {
            "streaming": true,
            "thinking": false,
            "vision": true,
            "image": false,
            "toolCalling": true,
            "jsonMode": true,
            "reasoning": true,
            "maxReasoningLevel": "XHigh",
            "embedding": false,
            "mcp": true
          },
          "inputPricePerMTok": 1.25,
          "outputPricePerMTok": 10,
          "tags": ["balanced", "quality"]
        }
      ]
    }
  ],
  "routing": {
    "failoverEnabled": true,
    "deepSeekOptimizationEnabled": false,
    "fallback": ["openai/gpt-5.5"],
    "loadBalance": "priority",
    "retry": {
      "maxAttempts": 3,
      "baseDelaySeconds": 1,
      "maxDelaySeconds": 20,
      "circuitBreakThreshold": 4,
      "circuitCooldownSeconds": 60
    }
  },
  "agent": {
    "maxSteps": 40,
    "autoVerify": true,
    "maxRepairAttempts": 3,
    "enableContextCompaction": true,
    "maxOutputContinuations": 6,
    "mode": "edit",
    "systemPrompt": "system/default",
    "thinkingBudgetTokens": 16384,
    "reasoningLevel": "High",
    "maxToolOutputChars": 60000,
    "bashTimeoutSeconds": 180,
    "scheduledTurnTimeoutSeconds": 1800,
    "networkEnabled": true
  },
  "mcp": {
    "servers": {}
  }
}
```

> `thinkingBudgetTokens` 是思考预算的基数，实际预算还会随思考深度档位放大（高×2、最大×4、极高×8、超级×16），并仅受模型 `maxOutput` 减去少量答案预留空间的限制——长任务不会被强限制在输出窗口的一半，避免思考到一半被强制截断。`enableContextCompaction` 开启后，当历史接近上下文上限时，Agent 会先把较早的对话总结成阶段性记忆（压缩上下文），再继续执行。当单次输出达到模型 `maxOutput` 上限（`finish_reason` 为 length/max_tokens）时，Agent 会自动继续下一轮而不是结束回合，空输出时会提示模型直接产出内容；连续截断超过 `maxOutputContinuations`（默认 6）次才会停止并给出明确警告。

### Provider Key

- `kind` 只能是 `openai` 或 `anthropic`。
- `apiKey` 直接保存凭据；Desktop 会直接显示和编辑该值。Runtime 只读取配置文件中的 `apiKey`，不会从环境变量读取 API Key。
- `organization`、`proxy`、`headers`、`timeoutSeconds` 可按服务需要设置。
- `promptCaching` 默认启用；Anthropic 协议会发送原生缓存检查点，不兼容的第三方网关可关闭。
- `reasoningEffortMap` 可覆盖统一档位到 Provider 参数的映射，例如 `{ "ultra": "xhigh" }`。
- `agent.reasoningLevel` 是 CLI/旧客户端的默认档位；Desktop 会把档位作为 Session 元数据独立保存。
- `capabilities` 可手动声明模型的 `thinking`、`reasoning` 与 `maxReasoningLevel`；未声明时 Runtime 会依据模型列表元数据（如 OpenRouter 的 `supported_parameters`）和内置家族规则（OpenAI o 系列与 GPT-5、Claude 3.7+/4、DeepSeek、Gemini 2.5/3、Grok 3-mini/4）自动推断，未知模型按“不支持推理”处理。超过声明上限的档位会自动降级；DeepSeek 的 `XHigh` 与 `Ultra` 固定降级为 `Max`。

### 路由

`loadBalance` 支持 `priority`、`roundRobin`、`leastUsed`、`lowestCost`、`fastest` 与 `sticky`。`fallback` 中的每个项目必须是 `provider/model` 引用。

`failoverEnabled`（默认 `true`）控制失败转移：开启时激活模型请求失败后会自动按候选链尝试其他候选模型；关闭时只使用激活模型，失败即停止并直接返回该模型的真实错误，不会静默切换到其他 Provider/模型（适合只使用本地模型的场景）。

### Agent 模式

`mode` 支持 `edit`、`plan`、`readonly` 与 `auto`。工作区配置可覆盖全局 Agent 默认值。

## 工作区覆盖

`~/.seekclaw/workspaces/<workspace-key>/config.json` 使用独立的精简结构，而不是复制完整全局配置：

```json
{
  "provider": "openai",
  "model": "gpt-5.5",
  "strategy": "quality",
  "temperature": 0.2,
  "mode": "edit",
  "systemPrompt": "system/default",
  "disabledSkills": ["legacy-migration"],
  "disabledTools": [],
  "autoVerify": true,
  "verifyCommand": "dotnet test",
  "mcp": { "servers": {} }
}
```

## 重新加载行为

Desktop 通过 Daemon 管理接口保存时，会立即更新该 Daemon 持有的配置。CLI 命令会持久化文件，后续 CLI 进程会读取新值；已经在运行的另一个 Daemon 不会自动感知这次外部修改。若在 CLI 或编辑器中改动 JSON，请重新连接 / 重启已有 Daemon，或通过对应管理接口更新。Prompt 文件的热加载与配置文件是不同机制。

配置 JSON 损坏时，Runtime 会保留原文件用于检查，并回退到种子默认配置；先修复原文件再重启，避免无意保存回退结果。

## 模型自动参数与多模态输入

Desktop 新建或编辑供应商时，可直接使用未保存的地址、密钥、代理与超时设置获取模型列表，获取本身不会保存配置。每个模型的上下文和最大输出分别支持“自动”：`contextWindowAuto` / `maxOutputAuto` 为 true 时，优先使用模型列表接口返回的 `detectedContextWindow` / `detectedMaxOutput`；缺少信息时回退到 1,000,000 / 128,000 Tokens。只有模型 ID 的标准 `/models` 接口无法提供精确限制，自动不会凭模型名字猜测。

`contextWindow` / `maxOutput` 保存手动值，切回手动模式后恢复；旧配置未指定自动标记时继续使用原有数字。运行时的请求和上下文预算使用解析后的实际限制。图像输入默认始终启用，旧的 `vision: false` 兼容读取但不再关闭图像输入；供应商 API 仍需实际支持收到的图像格式。
