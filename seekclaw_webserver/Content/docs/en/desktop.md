# Desktop Client

SeekClaw Desktop is a high-performance Electron and Vue 3 client for the .NET Runtime. It connects through the local Daemon IPC protocol (JSONL) and provides a rich graphical interface for projects, tasks, models, tools, and runtime diagnostics. Desktop and CLI share the same global configuration, MCP, Skills, and SQLite session database.

![SeekClaw Desktop AI chat and project management](/screenshots/desktop/chat-and-projects.png)

## Install and launch

### Use a packaged release

<LatestRelease />

The current Desktop release target is **Windows x64**. Keep the complete `SeekClaw-win-x64` directory and run `SeekClaw.exe`; copying the EXE by itself is not supported.

The package contains a self-contained Runtime:

```text
SeekClaw-win-x64/
├── SeekClaw.exe
└── resources/
    └── runtime/
        └── seekclaw.exe
```

End users do not need to install .NET. On launch, Desktop first tries to connect to an existing Daemon and starts the bundled Runtime when none is available. On exit it stops only the Runtime instance it started, leaving independently started Daemons alone.

### Build a release from source

Building requires Windows x64, the .NET 10 SDK, Node.js, pnpm, and Python 3. After cloning the repository, double-click `build.cmd` in the repository root:

```text
build.cmd
```

The command wrapper launches the cross-platform `build.py` entry point. It installs dependencies, runs .NET and Desktop tests, publishes the self-contained Runtime, builds Electron, and assembles the final folder at:

```text
publish\SeekClaw-win-x64\SeekClaw.exe
```

Optional terminal flags include:

```powershell
build.cmd --skip-tests
build.cmd --skip-install
```

Distribute the whole `publish\SeekClaw-win-x64` folder. Packaging downloads Electron binaries; the script configures download mirrors and retries transient packaging failures.

## Project and global tasks

Desktop supports two task scopes:

- A **project task** is bound to a local directory and can use file, terminal, Git changes, and Git history features.
- A **global task** has no project directory and is intended for general conversation; local file, terminal, and Git tools are unavailable.

Creating a task does not immediately create a Runtime Session. Desktop creates and persists the session when the first message is sent. The “Global tasks” entry expands the global task list instead of forcing a switch to a particular task.

Titles are derived from the first prompt. Tasks can be archived, restored, or deleted, including bulk operations within a project or the global scope. Archived tasks are read-only. Deleting a project permanently deletes every Session under that project, including archived Sessions, but does not delete local project files.

## Start a conversation & Approval Modes

1. Select “New task,” then choose a project or use the directory-free global scope.
2. Select an **Approval Mode** and `provider/model` at the bottom of the composer:
   - **Ask for approval (`manual`)**: Always ask before modifying external files or querying the internet;
   - **Approve for me (`guardrail`)**: Default mode; only asks for confirmation on detected high-risk/destructive operations;
   - **Full access (`full`)**: Unrestricted access without confirmation prompts.
3. Enter a request and send it, or click a starter-prompt card. A card fills the composer but **does not send automatically**.
4. Follow streaming text, reasoning status, tool activity, and complete error details.

Vision-capable models expose an image button in the composer for selecting multiple PNG, JPEG, WebP, or GIF files. After taking a Windows screenshot, press `Ctrl+V` in the composer to attach the clipboard image directly. Images can be previewed or removed before sending and are persisted with the Session. As each image enters the model request, the assistant message shows a “Viewed” row with its file name and thumbnail; click it to preview the image again. The button is disabled when the selected model does not declare the `vision` capability.

You can keep typing while the Agent is streaming. Pressing “Send” adds the message to a queue; queued messages are sent in order after the current turn finishes, and can be stacked or removed. Pressing “Steer” sends a queued message as additional guidance to the active turn (Mid-turn Steering) without cancelling its in-flight request.

Project tasks show the complete workspace path and shortcuts for opening the directory, terminal, Git changes, Git history, and task settings. Global tasks omit the path and project-only tools.

## Key Features

- **Structured Task Planner**: When long turns or architectural refactors trigger the `update_plan` tool, Desktop unfolds an interactive checklist tracking running, done, error, and pending sub-steps.
- **Computer Use & Ambient Halo**: During `computer` or `computer_inspect` UI operations, Desktop activates a volumetric screen-edge ambient halo animation to clearly signify active desktop control and provides global hotkeys for immediate cancellation.
- **Scheduled Tasks Dialog**: Visual management for Cron-based scheduled background tasks, allowing setup, inspection, toggling, and immediate execution triggers.
- **Official Skills Hub**: Built-in marketplace dialog for browsing, downloading, and hot-reloading official and community skills.
- **Message Editing & Branching**: Allows editing historical user prompts and truncating subsequent turns for regeneration.
- **Config Anomaly Recovery**: Automatically detects corrupted configuration files and prompts one-click restoration from timestamped backups.

## Models, Providers, and API keys

Open “Settings → Models & Providers” to:

- create, edit, test, enable, or remove Providers;
- view and edit API keys stored explicitly in the configuration;
- manage models, Base URL, proxy, timeout, and priority;
- switch the active model and toggle the failover chain.

![SeekClaw Desktop model and Provider management](/screenshots/desktop/providers-and-models.png)

::: tip API key visibility
The `apiKey` value is stored directly in `~/.seekclaw/config.json` and is read, displayed, and edited by Desktop. Runtime does not read API keys from environment variables.
:::

Use “Test” immediately after saving. Failed model requests include the Provider, HTTP status, and complete server response instead of only a generic `LLM request failed` message.

## MCP, Skills, diagnostics, and usage

The settings workbench also provides:

- **MCP**: configure workspace or global stdio / SSE / Streamable HTTP servers and reload their tools after saving;
- **Skills**: inspect discovered skills and enable or disable them;
- **Diagnostics & Usage**: inspect workspace, configuration, and Provider health plus interactive charts for calls, tokens, latency, and cost trends.

![Configure an MCP Server in Desktop](/screenshots/desktop/mcp-servers.png)

![Desktop Runtime diagnostics and usage](/screenshots/desktop/diagnostics-and-usage.png)

## Runtime connection behavior

Desktop connects automatically on startup and performs a bounded reconnect sequence when the connection is lost. If reconnection still fails, it displays the concrete error and offers another retry or exit.

Check the following first:

1. Verify that `resources\runtime\seekclaw.exe` exists in the release folder.
2. Check that an incompatible or stale Daemon is not occupying `\\.\pipe\seekclaw`.
3. Run the checks again from “Diagnostics & Usage.”
4. For a source checkout, run `build.cmd` to stage the Runtime required by Desktop.

See [Daemon and IPC Protocol](/en/doc/daemon) for the lower-level integration contract and [FAQ & Diagnostics](/en/doc/faq) for more troubleshooting help.
