# Local Models & Private Deployment (Ollama / vLLM / LM Studio)

SeekClaw features a standardized OpenAI-compatible protocol layer, allowing seamless connection to privately hosted, offline, or on-premise Large Language Models without third-party cloud dependencies.

---

## 1. Recommended Local Models for Coding Agents

Autonomous agents require robust tool-calling capabilities, structured JSON compliance, and precise code generation:

| Model Family | Recommended Sizes | VRAM Required | Strengths |
| :--- | :--- | :--- | :--- |
| **Qwen2.5-Coder** | 7B / 14B / 32B | 6GB ~ 24GB | Exceptional coding performance, strict tool-calling adherence |
| **DeepSeek-R1-Distill-Qwen** | 14B / 32B | 10GB ~ 24GB | Deep chain-of-thought reasoning, great for complex architecture |
| **CodeLlama / StarCoder2** | 7B / 13B | 6GB ~ 12GB | Precise single-file refactoring and completion |

---

## 2. Connecting Ollama

Ollama is the easiest solution for local workstation setups.

### Step 1: Pull and Run the Model
```bash
ollama run qwen2.5-coder:14b
```

### Step 2: Configure Provider in SeekClaw
Add the following to `~/.seekclaw/config.json` (or via Desktop UI Settings):

```json
{
  "providers": [
    {
      "id": "ollama",
      "kind": "openai",
      "baseUrl": "http://127.0.0.1:11434/v1",
      "apiKey": "ollama",
      "priority": 1,
      "models": [
        {
          "id": "qwen2.5-coder:14b",
          "alias": "qwen-local",
          "contextWindow": 32768,
          "maxOutput": 4096,
          "capabilities": {
            "toolCalling": true,
            "streaming": true
          }
        }
      ]
    }
  ]
}
```

---

## 3. High-Throughput Inference with vLLM

For enterprise on-premise GPU clusters, vLLM offers production-grade concurrency:

```bash
vllm serve Qwen/Qwen2.5-Coder-32B-Instruct \
  --port 8000 \
  --enable-auto-tool-choice \
  --tool-call-parser hermes \
  --max-model-len 32768
```

Then configure SeekClaw `baseUrl` to `http://<cluster-ip>:8000/v1`.

---

## 4. Connecting LM Studio

1. Download a GGUF model with Tool Calling support inside LM Studio.
2. In the **Developer** tab, start the **Local Server** (default `http://127.0.0.1:1234`).
3. Set SeekClaw Provider `baseUrl` to `http://127.0.0.1:1234/v1`.

---

## 5. Local Tuning Tips

1. **Accurate `contextWindow`**: Configure the exact context size allocated in your local backend (e.g. `32768`). SeekClaw's `ContextPlanner` dynamically scales tool output limits and trimming thresholds accordingly.
2. **Lower Temperature**: Keep `temperature` around `0.0` - `0.2` for maximum code stability and instruction adherence.
