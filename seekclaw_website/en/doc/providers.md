# Providers & Smart Routing

SeekClaw supports cloud LLM APIs (OpenAI, Anthropic, Gemini) and local models (Ollama, LM Studio) with intelligent candidate routing and circuit breakers.

---

## Supported Providers

| Identifier | Models Example | Protocol | Highlights |
| --- | --- | --- | --- |
| `openai` | `gpt-5.5`, `gpt-5.5-mini`, `deepseek-v3` | OpenAI REST | High performance, Function calling |
| `anthropic` | `claude-opus`, `claude-sonnet`, `claude-haiku` | Anthropic Messages | Complex reasoning, Long context |
| `google` | `gemini-pro`, `gemini-flash` | Gemini REST | Ultra-long Context Window |
| `ollama` | `llama3.3`, `qwen2.5-coder` | Ollama Local API | 100% Offline privacy, zero token cost |

---

## Routing Strategies

Supported strategies: `fast`, `balanced`, `quality`, `offline`.
Load balancing algorithms: `priority`, `roundRobin`, `leastUsed`, `fastest`.
