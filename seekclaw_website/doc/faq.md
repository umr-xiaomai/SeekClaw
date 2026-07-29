# 常见问题与排错指南 (FAQ & Diagnostics)

本文解答使用 SeekClaw AI Agent 运行时过程中常见的疑问，并提供排查网络或配置故障的实用技能。

---

## 常见问题解答 (FAQ)

### Q1: SeekClaw 与传统的 LLM Chat UI 有什么本质区别？

**A**: SeekClaw 不仅仅是一个聊天界面，而是一个**具有自主工具执行能力与编译闭环的 Agent 运行时**。
- 它能自主在你的本地代码库中搜索、读取、精准切片编辑文件、调用 Shell 命令行。
- 在修改完成后，它能自动触发代码构建与单元测试，捕获报错并自己修复代码，直到构建通过。

---

### Q2: 使用 SeekClaw 会泄露我的商业代码隐私吗？

**A**: 
1. SeekClaw 支持指定 **Offline 离线模式**，将请求完全路由到本地运行的 Ollama 或 LM Studio 模型（如 DeepSeek R1 本地版 / Qwen 2.5 Coder），代码数据 100% 不离开你的局域网。
2. 当使用云端 Provider 时，数据仅发送给指定的 API 服务商，SeekClaw 本身不上传或保存任何用户的代码与提示词。

---

### Q3: 运行时提示 `Provider network timeout` 或 `API key invalid` 怎么办？

**A**:
1. 检查环境变量或 `~/.seekclaw/config.json` 中配置的 `apiKey` 是否正确。
2. 运行诊断命令：
   ```bash
   seekclaw doctor
   ```
3. 若所在地访问 OpenAI / Anthropic 存在网络限制，可在 `config.json` 中配置代理服务器 `proxy`:
   ```json
   "providers": {
     "openai": {
       "apiKey": "sk-...",
       "baseUrl": "https://api.openai.com/v1",
       "proxy": "http://127.0.0.1:7890"
     }
   }
   ```

---

### Q4: 如何在项目团队中统一 SeekClaw 的 Agent 编程规范？

**A**:
在团队代码仓库根目录创建 `.seekclaw/memory/MEMORY.md` 并在 Git 中提交此文件。所有使用 SeekClaw 的团队成员在启动项目时，Agent 均会自动吸纳并遵守该记忆文件中的设计规范。

---

### Q5: 支持 Native AOT 零依赖独立二进制打包吗？

**A**:
是的！`seekclaw_runtime` 遵循 Native AOT 约束规范编写，移除了不兼容反射的强依赖，使用 C# Source Generators（源生成器）处理 JSON 序列化。
构建 AOT 独立文件命令：
```bash
dotnet publish seekclaw_cli -c Release -r win-x64 --self-contained
```

---

## 系统诊断工具 (`seekclaw doctor`)

当遇到意料之外的错误时，`doctor` 命令是您的最佳排错途径：

```bash
seekclaw doctor
```

输出示例：
```
[+] .NET 10.0 SDK Environment .................... [ OK ] (v10.0.100)
[+] Git Command Line Tools ......................... [ OK ] (git version 2.43)
[+] Global Config (~/.seekclaw/config.json) ......... [ OK ]
[+] Active Provider Connection (OpenAI) ........... [ OK ] (Latency: 240ms)
[+] Fallback Provider Connection (Anthropic) ....... [ OK ] (Latency: 310ms)
[+] Builtin Prompts Verification .................... [ OK ] (12 templates active)
[+] Workspace Initialized (.seekclaw/) .............. [ OK ]

Status: 7/7 Health Checks Passed. SeekClaw is ready!
```
