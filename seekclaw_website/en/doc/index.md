# SeekClaw Documentation

Welcome to the **SeekClaw** official technical documentation!

SeekClaw is a high-performance AI Agent runtime built on **.NET 10.0**, utilizing **Clean Architecture** and an **event-driven design**. It provides a comprehensive platform for building AI-powered coding assistants, CLI terminal experiences, and intelligent developer tools.

---

## Core Principles & Design Features

| Feature | Description |
| --- | --- |
| **Runtime First Architecture** | All business logic is encapsulated in `seekclaw_runtime`. `seekclaw_cli` serves purely as the default terminal frontend, making GUI, Web, and IDE integrations easy. |
| **Multi-Provider & Smart Routing** | Native support for OpenAI, Anthropic Claude, Google Gemini, and local Ollama / LM Studio with Fast, Balanced, Quality, and Offline strategies with circuit breakers. |
| **60 FPS Terminal Renderer** | 30-60 FPS double-buffered console rendering, supporting real-time streaming tokens, live thinking processes, and status spinners. |
| **Open Tools & MCP Protocol** | Built-in file read/write/edit, regex search, and bash execution tools, fully implementing the Model Context Protocol (MCP). |
| **Auto-Build & Self-Healing** | Automatically triggers .NET, Rust, Node, Go, or Python build verifications after code edits, feeding compilation errors back to the agent for self-repair. |
| **Workspace & Session State** | Auto-detects project types, maintains isolated `.seekclaw/` workspace environments, and supports JSONL session persistence. |

---

## Documentation Navigation Index

- **Quick Start Guide**: Learn about [Quick Start](/en/doc/quickstart)
- **Architecture Design**: Understand [Runtime First & Render Loop](/en/doc/architecture)
- **Providers & Routing**: Configure [OpenAI, Anthropic, Gemini, Ollama](/en/doc/providers)
- **CLI Reference**: Master [seekclaw commands and parameters](/en/doc/cli)
- **Tools & MCP**: Explore [Built-in Tools](/en/doc/tools) and [MCP Integration](/en/doc/mcp)
- **Skills & Memory**: Configure [Skill Templates](/en/doc/skills) and [Memory System](/en/doc/workspace)
- **Verification & Repair**: Read about [BuildVerifier Mechanism](/en/doc/verification)
- **Configuration Reference**: Check [Configuration Schema](/en/doc/configuration)
- **Daemon Server**: Integrate [IPC Protocol for GUI/IDE](/en/doc/daemon)
- **FAQ & Diagnostics**: Run [Doctor & Troubleshooting](/en/doc/faq)

---

## Open Source License

SeekClaw is licensed under the open-source [MIT License](https://opensource.org/licenses/MIT).
Source code is hosted on GitHub: [umr-xiaomai/SeekClaw](https://github.com/umr-xiaomai/SeekClaw).
