# 全局与工作区配置参考 (Configuration Reference)

SeekClaw 的所有参数均支持数据驱动与 JSON 格式配置。配置分为**全局配置**与**工作区配置**，工作区配置可覆盖全局默认值。

---

## 配置文件保存路径

- **全局配置文件**：`~/.seekclaw/config.json`（首次运行程序时自动基于内嵌默认配置种子化生成）。
- **工作区覆盖文件**：`<workspace-root>/.seekclaw/config.json`。

---

## 完整 `config.json` Schema 字段详解

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "providers": {
    "openai": {
      "apiKey": "sk-proj-xxxxxxxx",
      "baseUrl": "https://api.openai.com/v1",
      "organization": "org-xxxx",
      "timeoutSeconds": 60,
      "maxRetries": 3
    },
    "anthropic": {
      "apiKey": "sk-ant-xxxxxxxx",
      "baseUrl": "https://api.anthropic.com"
    },
    "google": {
      "apiKey": "AIzaSy-xxxxxxx"
    },
    "ollama": {
      "baseUrl": "http://localhost:11434"
    }
  },

  "profiles": {
    "default": {
      "provider": "openai",
      "model": "gpt-5.5",
      "temperature": 0.2,
      "topP": 0.95
    },
    "fast-coding": {
      "provider": "openai",
      "model": "gpt-5.5-mini",
      "temperature": 0.1
    },
    "deep-reasoning": {
      "provider": "anthropic",
      "model": "claude-opus",
      "temperature": 0.3
    }
  },

  "routing": {
    "defaultStrategy": "balanced",
    "loadBalancing": "priority",
    "circuitBreaker": {
      "failureThreshold": 3,
      "cooldownSeconds": 60
    }
  },

  "agent": {
    "maxSteps": 15,
    "autoVerify": true,
    "maxRepairAttempts": 3,
    "systemPrompt": "builtin/system-default",
    "developerPrompt": "builtin/developer-default",
    "contextWindowPruningRatio": 0.85
  },

  "ui": {
    "targetFps": 60,
    "theme": "cyberpunk-dark",
    "showThinkingProcess": true,
    "showToolArguments": true
  }
}
```

---

## 热重载 (Hot Reload)

`seekclaw_runtime` 内部对配置与 Prompt 模板文件挂载了 `FileSystemWatcher` 监测。在编辑 `config.json` 或修改 `.seekclaw/prompts/` 模板文件后，运行时无需重启即可秒级实时重载最新配置生效。
