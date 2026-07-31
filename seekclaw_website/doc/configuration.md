# 配置参考

SeekClaw 使用全局配置保存 Provider、模型、Profile、路由和 Agent 默认值，并允许工作区提供少量覆盖项。Desktop 与 CLI 操作的是同一份配置。

## 文件位置

| 文件 | 用途 |
| --- | --- |
| `~/.seekclaw/config.json` | 全局 Provider、模型、Profile、路由、Agent 和 MCP 配置 |
| `~/.seekclaw/state.json` | 上次 Session、轮询游标和禁用 Skill 等内部状态 |
| `~/.seekclaw/usage.jsonl` | 调用、Token、延迟和成本记录 |
| `<workspace>/.seekclaw/config.json` | 当前项目的覆盖项 |

首次运行时，Runtime 从发布包内的 `defaults/config.default.json` 创建全局配置。Provider 与模型数据随后完全由用户管理。

## 全局配置结构

下面示例使用当前实际字段：

```json
{
  "activeProfile": "default",
  "profiles": {
    "default": {
      "provider": "openai",
      "model": "gpt-5.5",
      "strategy": "balanced",
      "temperature": 0.2,
      "mode": "edit"
    }
  },
  "providers": [
    {
      "id": "openai",
      "name": "OpenAI",
      "kind": "openai",
      "baseUrl": "https://api.openai.com/v1",
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
    "strategies": {
      "balanced": ["openai/gpt-5.5"]
    },
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
    "mode": "edit",
    "systemPrompt": "system/default",
    "thinkingBudgetTokens": 4096,
    "reasoningLevel": "High",
    "maxToolOutputChars": 60000,
    "bashTimeoutSeconds": 180
  },
  "mcp": {
    "servers": {}
  }
}
```

### Provider Key

- `kind` 只能是 `openai` 或 `anthropic`。
- `apiKey` 直接保存凭据；Desktop 会直接显示和编辑该值。Runtime 只读取配置文件中的 `apiKey`，不会从环境变量读取 API Key。
- `organization`、`proxy`、`headers`、`timeoutSeconds` 可按服务需要设置。
- `promptCaching` 默认启用；Anthropic 协议会发送原生缓存检查点，不兼容的第三方网关可关闭。
- `reasoningEffortMap` 可覆盖统一档位到 Provider 参数的映射，例如 `{ "ultra": "xhigh" }`。
- `agent.reasoningLevel` 是 CLI/旧客户端的默认档位；Desktop 会把档位作为 Session 元数据独立保存。
- `capabilities.maxReasoningLevel` 声明模型支持的最高档位。默认是 `Max`，超过后自动降级；DeepSeek 的 `XHigh` 与 `Ultra` 固定降级为 `Max`。

### 路由

`loadBalance` 支持 `priority`、`roundRobin`、`leastUsed`、`lowestCost`、`fastest` 与 `sticky`。`strategies` 和 `fallback` 中的每个项目必须是 `provider/model` 引用。

### Agent 模式

`mode` 支持 `edit`、`plan`、`readonly` 与 `auto`。Profile 中的值可以覆盖 Agent 默认值，工作区还可以再次覆盖。

## 工作区覆盖

`<workspace>/.seekclaw/config.json` 使用独立的精简结构，而不是复制完整全局配置：

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
