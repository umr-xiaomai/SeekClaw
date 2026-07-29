# 多提供商与智能路由 (Providers & Routing)

SeekClaw 内置对全球主流云端大模型 API 以及本地离线 LLM 的全量接入支持。本文详细介绍提供商配置、模型注册表、智能路由策略与自动熔断退避机制。

---

## 支持的 LLM 提供商列表

| 提供商标识 | 支持模型示例 | 协议规范 | 场景特点 |
| --- | --- | --- | --- |
| `openai` | `gpt-5.5`, `gpt-5.5-mini`, `deepseek-v3`, `qwen2.5` | OpenAI REST API | 云端高性能、函数调用规范 |
| `anthropic` | `claude-opus`, `claude-sonnet`, `claude-haiku` | Anthropic Messages API | 深度逻辑推理、长文本理解 |
| `google` | `gemini-pro`, `gemini-flash` | Gemini REST API | 海量 Context Window、极高吞吐 |
| `ollama` | `llama3.3`, `qwen2.5-coder`, `deepseek-r1:14b` | Ollama Local API | 100% 离线隐私安全、零 Token 资费 |
| `lmstudio` | 自定义本地 GGUF / ExLlama 模型 | OpenAI 兼容接口 | 本地 GPU 部署调试 |

---

## 智能路由策略 (Routing Strategies)

SeekClaw 拒绝死板硬编码模型。运行时包含 **智能候选链构建器**，根据任务场景策略与模型能力位（ModelCapabilities）自动选择最佳模型：

```json
"routing": {
  "defaultStrategy": "balanced",
  "strategies": {
    "fast": {
      "candidates": ["openai/gpt-5.5-mini", "anthropic/claude-haiku", "google/gemini-flash"]
    },
    "balanced": {
      "candidates": ["openai/gpt-5.5", "anthropic/claude-sonnet"]
    },
    "quality": {
      "candidates": ["anthropic/claude-opus", "openai/gpt-5.5"]
    },
    "offline": {
      "candidates": ["ollama/qwen2.5-coder", "ollama/deepseek-r1:14b"]
    }
  }
}
```

### 负载均衡算法：
- **priority**：按候选链优先级从高到低顺序尝试。
- **roundRobin**：在同级候选健康节点间轮询分流。
- **leastUsed**：优先调度当前请求并发数最小的提供商。
- **fastest**：基于历史响应延迟记录选出首 Token 最快节点。

---

## 故障转移与熔断机制 (Circuit Breaker)

为应对云端 API 偶尔出现的超时、速率限制（Rate Limit 429）或服务不可用（5xx），SeekClaw 内置了电信级熔断退避防护：

```
                             +-------------------+
                             |   Closed (正常)    |
                             +---------+---------+
                                       |
                                连续失败超过 N 次
                                       |
                                       v
                             +-------------------+
                             |    Open (熔断)    | <--- 自动切至候选链下一个 Candidate
                             +---------+---------+
                                       |
                                冷却时间过后 (如 60s)
                                       |
                                       v
                             +-------------------+
                             |  HalfOpen (半开)  |
                             +-------------------+
```

### 运行特征：
- **指数退避与抖动**：重试等待时间按 1s, 2s, 4s, 8s (带有 random jitter) 自动递增。
- **首 Token 到达提交**：一旦模型开始流式返回 Token（First Byte Arrived），任务即视为被提供商成功受理，后续不再中途更换候选节点。
- **健康检查器 (HealthChecker)**：后台定期对各个 Provider 进行存活心跳探测。

---

## 命令行配置管理示例

```bash
# 查看所有已注册提供商
seekclaw provider list

# 添加新的 OpenAI 兼容服务商 (例如 DeepSeek)
seekclaw provider add deepseek \
  --api-key "sk-xxxxxxxx" \
  --base-url "https://api.deepseek.com/v1"

# 测试特定提供商的连通性
seekclaw provider test deepseek

# 实时查询已使用 Token 与费用统计
seekclaw usage
```
