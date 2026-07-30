# SeekClaw 文档中心

SeekClaw 是基于 **.NET 10**、本地优先且可扩展的通用 AI Agent Runtime，采用 Runtime First 与事件驱动架构。它能围绕目标持续思考，使用本地工具、网页、MCP 与 Skills 完成真实任务。你可以通过 Windows Desktop 或 CLI 使用同一套 Runtime、配置、工具与会话数据。

![SeekClaw Desktop 主界面](/screenshots/desktop/chat-and-projects.png)

## 现在可以做什么

| 能力 | 说明 |
| --- | --- |
| **Desktop 与 CLI 双前端** | Desktop 提供项目侧栏、全局任务、归档、模型设置、Git 与终端入口；CLI 保留完整的终端交互和管理命令。 |
| **项目任务与全局任务** | 项目任务可连接文件、终端、Git 与专属 Memory；不绑定目录的全局任务适合调研、写作、知识整理及日常工作。 |
| **多 Provider 与路由** | 支持 Anthropic 与 OpenAI 两种线协议，并可接入 OpenAI-compatible 服务，包括 Google、OpenRouter、Ollama、MiMo 和 LM Studio。 |
| **Tools、Skills 与 MCP** | 内置文件、搜索、Shell 和网络工具，可通过 Skills 与 stdio / SSE MCP Server 扩展。 |
| **会话与工作区持久化** | 会话按工作区保存为 JSONL，支持恢复、归档和删除；项目元数据隔离在 `.seekclaw/`。 |
| **自动验证与修复** | 文件修改后可触发 .NET、Node、Rust、Go 或 Python 等项目的构建检查，并把错误反馈给 Agent 修复。 |
| **诊断与用量** | Desktop 和 CLI 都能检查 Runtime / Provider 状态；Desktop 汇总调用、Token、延迟和成本。 |

## 选择使用方式

### Desktop

适合日常图形化使用。发布包已包含自包含 Runtime，启动 `SeekClaw.exe` 后会自动连接或启动 Daemon，终端用户无需安装 .NET。

- [Desktop 使用、设置与发布](/doc/desktop)
- [5 分钟快速开始](/doc/quickstart)

### CLI

适合终端工作流、自动化管理和调试 Runtime。源码运行需要 .NET 10 SDK。

- [CLI 命令参考](/doc/cli)
- [配置参考](/doc/configuration)

## 深入阅读

- [Runtime First 架构与 Agent 生命周期](/doc/architecture)
- [Provider、模型与智能路由](/doc/providers)
- [内置工具](/doc/tools)、[Skills](/doc/skills) 与 [MCP](/doc/mcp)
- [工作区与 Memory](/doc/workspace)
- [构建验证与自动修复](/doc/verification)
- [Daemon 与 IPC 2.0 协议](/doc/daemon)
- [常见问题与排错](/doc/faq)

## 开源许可

SeekClaw 使用 [MIT License](https://opensource.org/licenses/MIT)，源码托管于 [GitHub](https://github.com/umr-xiaomai/SeekClaw)。
