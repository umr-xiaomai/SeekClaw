# Configuration Reference

SeekClaw stores Providers, models, Profiles, routing, and Agent defaults in a global configuration and supports a smaller set of workspace overrides. Desktop and CLI operate on the same data.

## File locations

| File | Purpose |
| --- | --- |
| `~/.seekclaw/config.json` | Global Providers, models, Profiles, routing, Agent, and MCP configuration |
| `~/.seekclaw/state.json` | Internal state such as the last Session, routing cursors, and disabled Skills |
| `~/.seekclaw/usage.jsonl` | Calls, tokens, latency, and cost records |
| `<workspace>/.seekclaw/config.json` | Overrides for one project |

On first run, the Runtime generates the global configuration by serializing the code-defined defaults (`DefaultSeekClawConfig`) to `~/.seekclaw/config.json`; no static default configuration file is shipped anymore. Provider and model data are user-managed after that point.

## Global configuration shape

This example uses the current field names:

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
    "strategies": { "balanced": ["openai/gpt-5.5"] },
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
    "mode": "edit",
    "systemPrompt": "system/default",
    "thinkingBudgetTokens": 16384,
    "reasoningLevel": "High",
    "maxToolOutputChars": 60000,
    "bashTimeoutSeconds": 180
  },
  "mcp": { "servers": {} }
}
```

> `thinkingBudgetTokens` is the base thinking budget; the effective budget scales with the reasoning depth level (high ×2, max ×4, xhigh ×8, ultra ×16) and is capped only by the model's `maxOutput` minus a small answer reserve — long tasks are no longer forced to stop thinking at half the output window. With `enableContextCompaction` enabled (default), when the history approaches the context limit the Agent first summarizes the earlier conversation into compact memory before continuing, so a single turn is never interrupted by context or thinking length; a failed compaction falls back to plain trimming and never aborts the turn.


### Provider keys

- `kind` must be `openai` or `anthropic`.
- `apiKey` stores the credential directly, and Desktop displays and edits it. Runtime reads API keys only from this configuration field and never from environment variables.
- `organization`, `proxy`, `headers`, and `timeoutSeconds` can be set when required by the service.
- `promptCaching` defaults to enabled; Anthropic requests emit native cache checkpoints and incompatible third-party gateways can opt out.
- `reasoningEffortMap` can override neutral levels with Provider wire values, for example `{ "ultra": "xhigh" }`.
- `agent.reasoningLevel` is the default for CLI and legacy clients; Desktop persists the selected level independently in each Session.
- `capabilities.maxReasoningLevel` declares the highest supported model level. It defaults to `Max`; higher requests are clamped, and DeepSeek always maps `XHigh`/`Ultra` to `Max`.

### Routing

`loadBalance` supports `priority`, `roundRobin`, `leastUsed`, `lowestCost`, `fastest`, and `sticky`. Every item in `strategies` and `fallback` must be a `provider/model` reference.

### Agent modes

`mode` supports `edit`, `plan`, `readonly`, and `auto`. A Profile can override the Agent default, and a workspace can override it again.

## Workspace overrides

`<workspace>/.seekclaw/config.json` uses a smaller dedicated shape rather than copying the complete global configuration:

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

## Reload behavior

Saving through Desktop's Daemon administration methods updates that Daemon immediately. CLI commands persist the file for subsequent CLI processes, but a separate Daemon already in memory does not automatically observe the external write. After editing with CLI or an editor, reconnect or restart an existing Daemon, or use the appropriate administration method. Prompt-file hot reload is a separate mechanism.

If the global JSON is invalid, the Runtime preserves the file for inspection and falls back to the seed defaults. Repair the original file before restarting to avoid accidentally saving the fallback state.
