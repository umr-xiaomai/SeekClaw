# SeekClaw Documentation

SeekClaw is a local-first, extensible general-purpose AI Agent Runtime built on **.NET 10** with a Runtime First, event-driven architecture. It can reason toward a goal and use local tools, the web, MCP, and Skills to finish real work. Windows Desktop and CLI share the same Runtime, configuration, tools, and session data.

![SeekClaw Desktop main window](/screenshots/desktop/chat-and-projects.png)

## What SeekClaw provides today

| Capability | Description |
| --- | --- |
| **Desktop and CLI clients** | Desktop provides project navigation, global tasks, archives, model settings, Git, and terminal shortcuts. CLI retains the full terminal and administration workflow. |
| **Project and global tasks** | Project tasks connect files, terminals, Git, and dedicated Memory. Directory-free global tasks support research, writing, knowledge work, and everyday tasks. |
| **Providers and routing** | Supports Anthropic and OpenAI wire protocols plus OpenAI-compatible services such as Google, OpenRouter, Ollama, MiMo, and LM Studio. |
| **Tools, Skills, and MCP** | Includes file, search, shell, and web tools and can be extended with Skills and stdio / SSE MCP servers. |
| **Session and workspace state** | Sessions are stored per workspace as JSONL and can be resumed, archived, or removed. Project metadata is isolated under `.seekclaw/`. |
| **Verification and repair** | Code changes can trigger checks for .NET, Node, Rust, Go, Python, and other projects, with failures returned to the Agent for repair. |
| **Diagnostics and usage** | Desktop and CLI inspect Runtime and Provider health; Desktop also aggregates calls, tokens, latency, and cost. |

## Choose a client

### Desktop

Best for everyday graphical use. The release folder includes a self-contained Runtime. Launching `SeekClaw.exe` connects to or starts the Daemon automatically, and end users do not need .NET installed.

- [Desktop usage, settings, and release builds](/en/doc/desktop)
- [Five-minute quick start](/en/doc/quickstart)

### CLI

Best for terminal workflows, scripted administration, and Runtime debugging. The published
`seekclaw-cli` npm package can be installed with `npm install -g seekclaw-cli`; running from source requires the .NET 10 SDK.

- [CLI command reference](/en/doc/cli)
- [Configuration reference](/en/doc/configuration)

## Explore the Runtime

- [Runtime First architecture and Agent lifecycle](/en/doc/architecture)
- [Providers, models, and smart routing](/en/doc/providers)
- [Built-in tools](/en/doc/tools), [Skills](/en/doc/skills), and [MCP](/en/doc/mcp)
- [Workspace and Memory](/en/doc/workspace)
- [Build verification and automatic repair](/en/doc/verification)
- [Daemon and IPC 2.1 protocol](/en/doc/daemon)
- [FAQ and troubleshooting](/en/doc/faq)

## License

SeekClaw is available under the [MIT License](https://opensource.org/licenses/MIT). Source code is hosted on [GitHub](https://github.com/umr-xiaomai/SeekClaw).
