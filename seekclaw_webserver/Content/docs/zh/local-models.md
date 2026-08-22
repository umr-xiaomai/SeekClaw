# 本地大模型接入（Ollama / vLLM / LM Studio）

SeekClaw 采用完全标准化的 OpenAI-Compatible 协议层，支持零门槛接入本地私有化部署的大语言模型。无论是内网隔离环境、离线开发，还是出于数据隐私合规要求，你都可以在自己的 GPU / CPU 工作站上无缝运行 SeekClaw。

---

## 1. 推荐本地编程模型

对于 AI Agent 场景（需要强工具调用、结构化 JSON 输出和代码编辑能力），推荐选用经过代码或指令强化的开源模型：

| 模型家族 | 推荐参数量 | 显存需求 | 特点与优势 |
| :--- | :--- | :--- | :--- |
| **Qwen2.5-Coder** | 7B / 14B / 32B | 6GB ~ 24GB | 编程综合能力极强，Tool Calling 与 JSON 遵循度优秀 |
| **DeepSeek-R1-Distill-Qwen** | 14B / 32B | 10GB ~ 24GB | 具备深度推理思考链（Reasoning），擅长复杂算法与架构设计 |
| **CodeLlama / Starcoder2** | 7B / 13B | 6GB ~ 12GB | 纯代码补全与单文件精准重构 |

---

## 2. 接入 Ollama 本地服务

Ollama 是最流行的本地模型运行工具。

### 第一步：启动并拉取模型
```bash
ollama run qwen2.5-coder:14b
```

### 第二步：在 SeekClaw 中配置 Provider
打开 `~/.seekclaw/config.json`（或在 Desktop 设置界面的 Provider 管理中添加）：

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
  ],
  "profiles": {
    "default": {
      "strategy": "custom",
      "temperature": 0.2
    }
  }
}
```

---

## 3. 接入 vLLM 高并发推理引擎

对于企业内网服务器或多卡工作站，推荐使用 vLLM 提供生产级高吞吐推理。

### 第一步：使用 vLLM 启动兼容服务
```bash
vllm serve Qwen/Qwen2.5-Coder-32B-Instruct \
  --port 8000 \
  --enable-auto-tool-choice \
  --tool-call-parser hermes \
  --max-model-len 32768
```

### 第二步：配置 SeekClaw
将 `baseUrl` 指向 `http://<server-ip>:8000/v1` 即可。

---

## 4. 接入 LM Studio 可视化环境

1. 在 LM Studio 中下载并加载支持 Tool Calling 的 GGUF 模型。
2. 切换至 **Developer** 标签页，开启 **Local Server**（默认端口 `http://127.0.0.1:1234`）。
3. 在 SeekClaw 中将 Provider `baseUrl` 配置为 `http://127.0.0.1:1234/v1`。

---

## 5. 本地模型关键调优建议

1. **上下文窗口配置（ContextWindow）**：必须如实填写本地实际分配的上下文大小（如 `32768` 或 `16384`）。SeekClaw 的上下文预算管理器（ContextPlanner）会据此自适应调整工具输出上限与历史截断，防止发生 OOM。
2. **温度（Temperature）控制**：本地代码模型建议将 `temperature` 设置为 `0.0` ~ `0.2`，以最大化指令遵循与代码生成确定性。
3. **思考链开关**：对于带有蒸馏思考链的模型（如 DeepSeek-R1-Distill），可在模型配置中将 `capabilities.thinking` 设置为 `true`。
