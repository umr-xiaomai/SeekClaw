# Architecture & Runtime First Principles

SeekClaw's core design follows **Runtime First** principles and **Clean Architecture**.

---

## Overall Architecture Diagram

```
+-----------------------------------------------------------------------+
|                            Frontends Layer                            |
|   +---------------------------------+   +-------------------------+   |
|   | seekclaw_cli (System.CommandLine)|   | GUI / Web / IDE Plugin  |   |
|   +---------------------------------+   +-------------------------+   |
+------------------------------------|----------------------------------+
                                     |  (Daemon IPC / Direct Facade)
+------------------------------------v----------------------------------+
|                      SeekClaw Core Runtime (seekclaw_runtime)         |
|                                                                       |
|   +---------------------------------------------------------------+   |
|   | SeekClawRuntime (Facade / Composition Root)                   |   |
|   +---------------------------------------------------------------+   |
|                               |                                       |
|   +---------------------------v-----------------------------------+   |
|   |                       Agent Loop                              |   |
|   +---------------------------------------------------------------+   |
|         |                     |                    |                  |
|   +-----v-------+     +-------v------+     +-------v------+           |
|   | EventBus    |     | Provider     |     | ToolRegistry |           |
|   | (Channel)   |     | Manager      |     | (Plugins)    |           |
|   +-------------+     +--------------+     +--------------+           |
|         |                     |                    |                  |
|   +-----v-------+     +-------v------+     +-------v------+           |
|   | Terminal    |     | MCP Manager  |     | Build        |           |
|   | Renderer    |     | (stdio/SSE)  |     | Verifier     |           |
|   +-------------+     +--------------+     +--------------+           |
+-----------------------------------------------------------------------+
```

---

## Runtime First Principles

1. **Separation of Concerns**: Business logic, LLM routing, context planning, tool execution, session storage, and build verifications are encapsulated strictly inside `seekclaw_runtime`.
2. **Zero Console Intrusion**: Classes inside `seekclaw_runtime` never invoke `Console.WriteLine`. State changes are emitted exclusively via `IEventBus`.
3. **Multi-Client Support**: `seekclaw_cli` is the default frontend; future GUI, Web, or IDE extensions connect through the same Facade or Daemon IPC protocol.
