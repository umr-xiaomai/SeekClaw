# SeekClaw 文档中心

欢迎来到 **SeekClaw** 官方技术文档中心！

SeekClaw 是基于 **.NET 10.0** 构建的高性能 AI Agent 运行时，采用**清洁架构（Clean Architecture）**与**事件驱动设计**。它为构建 AI 驱动的自动编码助手、CLI 终端体验和智能化开发工具提供了完整平台。

---

## 核心特色与设计原则

| 特性 | 描述 |
| --- | --- |
| **Runtime First 架构** | 核心业务完全置于 `seekclaw_runtime` 库，`seekclaw_cli` 仅作为默认前端终端展示，方便扩展 GUI / Web / IDE 插件。 |
| **多模型与智能路由** | 原生兼容 OpenAI、Anthropic Claude、Google Gemini，以及本地 Ollama / LM Studio 模型。支持 Fast / Balanced / Quality / Offline 等策略与自动熔断降级。 |
| **游戏级终端渲染** | 30-60 FPS 双缓冲控制台渲染，支持 Token 流式增量显示、实时思维链（Thinking Process）及动态 Spinner。 |
| **开放插件与 MCP 协议** | 内置文本读取/编辑/正则搜索/Bash 工具，支持规范的 Model Context Protocol (MCP)，实现外接 MCP 服务器扩展。 |
| **自动构建与修复自愈** | 修改项目代码后自动触发 .NET / Rust / Node / Go / Python 构建验证，编译失败自动再注入 Agent 循环自我修复。 |
| **工作区与 Session 持久化** | 自动识别 Git、.NET、Vue、Python 等项目架构，独立 `.seekclaw/` 目录隔离，支持 JSONL 格式会话恢复。 |

---

## 文档导航索引

快捷导航至您感兴趣的技术主题：

- **快速开始与环境要求**：查看 [快速开始指南](/doc/quickstart)
- **深入架构设计**：了解 [Runtime First 原则与渲染循环](/doc/architecture)
- **提供商配置与路由**：配置 [OpenAI / Anthropic / Gemini / Ollama](/doc/providers)
- **CLI 命令行大全**：掌握 [seekclaw 命令与参数说明](/doc/cli)
- **工具与 MCP 生态**：学习 [内置工具使用与自定义 Tool 开发](/doc/tools) 和 [MCP 整合](/doc/mcp)
- **技能与工作区内存**：配置 [Skill 模板](/doc/skills) 与 [Memory 体系](/doc/workspace)
- **构建验证与修复**：了解 [BuildVerifier 机制](/doc/verification)
- **全局配置参数**：参考 [Configuration schema](/doc/configuration)
- **Daemon 守护进程**：集成 [IPC Protocol 接入 GUI/IDE](/doc/daemon)
- **常见诊断与 FAQ**：运行 [Doctor 与排错](/doc/faq)

---

## 项目开源许可

SeekClaw 采用自由开放的 [MIT License](https://opensource.org/licenses/MIT) 开源许可。
仓库代码托管于 GitHub: [umr-xiaomai/SeekClaw](https://github.com/umr-xiaomai/SeekClaw)。
