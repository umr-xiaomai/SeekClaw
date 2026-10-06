# Provider、模型与智能路由

SeekClaw 把“服务地址与凭据”和“模型能力”都作为可编辑配置。默认配置提供常用服务模板，你可以在 Desktop、CLI 或 `~/.seekclaw/config.json` 中增加任何兼容服务。

## 协议与预置 Provider

Runtime 当前实现两种线协议：

- `anthropic`：Anthropic Messages API；
- `openai`：OpenAI Chat Completions API，也用于所有 OpenAI-compatible 服务。

首次初始化的模板包括：

| Provider ID | 线协议 | 默认地址 | 默认状态 |
| --- | --- | --- | --- |
| `anthropic` | Anthropic | `https://api.anthropic.com` | 启用 |
| `openai` | OpenAI | `https://api.openai.com/v1` | 启用 |
| `google` | OpenAI compatible | `https://generativelanguage.googleapis.com/v1beta/openai` | 启用 |
| `mimo` | OpenAI compatible | `https://token-plan-cn.xiaomimimo.com/v1` | 启用 |
| `openrouter` | OpenAI compatible | `https://openrouter.ai/api/v1` | 默认禁用 |
| `ollama` | OpenAI compatible | `http://localhost:11434/v1` | 默认禁用 |
| `lmstudio` | OpenAI compatible | `http://localhost:1234/v1` | 默认禁用 |

DeepSeek、Azure OpenAI、企业网关或其他兼容服务可以通过新增 `kind: "openai"` 的 Provider 接入。模型 ID、上下文窗口、最大输出、能力标签与价格均属于用户配置，不被 Runtime 写死。

## 在 Desktop 中配置

进入“设置 → 模型与 Provider”，可以管理 Provider 与模型目录：

![Desktop 模型与 Provider 管理](/screenshots/desktop/providers-and-models.png)

1. 点击“+ Provider”或编辑现有项。
2. 选择 `openai` 或 `anthropic` 协议，填写 ID、名称、Base URL 和模型 ID。
3. 直接填写 API Key；该 Key 会保存到配置文件中。
4. 根据需要配置代理、超时、优先级、提示词缓存和启用状态。
5. 保存后点击“测试”；确认可用后切换活动 Provider 或模型。

`apiKey` 直接写入配置文件，由 Desktop 读取、显示和修改。Runtime 不会从环境变量读取 API Key。

请求失败时，Runtime 会保留 Provider 名称、HTTP 状态和响应正文。例如协议不兼容、工具调用消息缺少对应结果、模型 ID 错误等 400 响应会完整展示，方便按服务端提示修复。

## 配置文件结构

Provider 是数组，模型属于对应 Provider：

```json
{
  "provider": "deepseek",
  "model": "deepseek-chat",
  "providers": [
    {
      "id": "deepseek",
      "name": "DeepSeek",
      "kind": "openai",
      "baseUrl": "https://api.deepseek.com/v1",
      "apiKey": "your-api-key",
      "promptCaching": true,
      "enabled": true,
      "priority": 0,
      "timeoutSeconds": 120,
      "models": [
        {
          "id": "deepseek-chat",
          "contextWindow": 128000,
          "maxOutput": 8192,
          "capabilities": { "streaming": true, "toolCalling": true }
        }
      ]
    }
  ]
}
```

需要直接保存 Key 时可使用 `"apiKey": "sk-..."`，但不要把包含凭据的个人全局配置提交到仓库。

`promptCaching` 默认启用。OpenAI-compatible 服务使用服务端的自动前缀缓存；Anthropic 协议还会在稳定的 System Prompt 和工具定义后写入 `cache_control` 检查点。若某个旧的 Anthropic-compatible 网关不接受该字段，可在 Desktop 中关闭此项或设置 `"promptCaching": false`。默认 Prompt 不包含动态时间，工具定义也会按名称稳定排序，避免每轮请求改变缓存前缀。

## 候选链与故障转移（Failover）

Runtime 自动构建有序的候选模型链（Candidates）：
1. **工作区覆盖**：若当前项目配置了 `provider` 或 `model`，优先作为首选；
2. **全局活动模型**：使用配置中的 `provider` 与 `model`；
3. **全局 Fallback 链**：依次尝试 `routing.fallback` 中指定的候选模型。

当开启 `routing.failoverEnabled`（默认开启）时，首选模型若出现网络超时或服务不可用（如 5xx、熔断或超限），系统会自动切换至 Fallback 候选链中的下一个模型继续重试，避免中断任务；若关闭该开关，模型请求失败时会立即停止并抛出真实错误。首个流式 Token 到达后，Runtime 绝不在同一回答中途切换 Provider，以避免拼接两个不同模型的输出。

## CLI 管理

```bash
# 查看 Provider
seekclaw provider list

# 添加 OpenAI-compatible Provider
seekclaw provider add --id deepseek --kind openai \
  --api-key "sk-..." \
  --base-url "https://api.deepseek.com/v1" \
  --model "deepseek-chat"

# 检查连通性
seekclaw provider test deepseek

# 查看模型与累计用量
seekclaw model list
seekclaw usage
```

完整字段见 [配置参考](/doc/configuration)，连接问题见 [常见问题与排错](/doc/faq)。
