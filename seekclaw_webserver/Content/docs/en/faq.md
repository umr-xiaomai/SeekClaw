# FAQ and Troubleshooting

## Desktop

### Do Desktop users need .NET installed?

No. The Windows x64 release folder includes a self-contained Runtime. Keep the complete `SeekClaw-win-x64` directory and launch the top-level `SeekClaw.exe`. Only developers building from source or running the CLI directly need the .NET 10 SDK.

### Why does Desktop report that the Runtime is offline?

Check these items in order:

1. Confirm that `resources\runtime\seekclaw.exe` exists.
2. Make sure the release directory was not split and security software did not quarantine the Runtime.
3. Check whether an incompatible old Daemon owns `\\.\pipe\seekclaw`.
4. Run the checks again under “Settings → Diagnostics & Usage” and read the complete error.

Desktop connects and reconnects automatically; users do not start the Runtime manually. For a source checkout, run `build.cmd` in the repository root to create a complete release.

### Why is the API key field empty?

- When the configuration stores `apiKey` directly, Desktop reads and displays its actual value.
- API keys are read only from the `apiKey` field in `~/.seekclaw/config.json`; environment-variable injection is not supported.

If a directly stored key disappears after one turn, verify that Desktop and the Daemon run under the same user account and read the same `~/.seekclaw/config.json`. Check the active Profile and Provider in diagnostics, and avoid running an older Runtime that rewrites the same configuration simultaneously.

### How are projects and tasks related?

A project may contain zero or multiple persistent tasks. A Session is created only when the first message is sent. Global tasks have no project directory, do not show a project path, and cannot use local file, terminal, or Git tools.

## Providers and model requests

### What should I check for timeouts, 401, or 404 responses?

1. Confirm the wire protocol, Base URL, model ID, and key under “Models & Providers.”
2. Use the Provider or model “Test” action.
3. Verify that proxy and environment variables are visible to the Runtime started by Desktop.
4. Inspect the specific Provider result under “Diagnostics & Usage.”

Current versions show the Provider, HTTP status, and response body. Details after messages such as `DeepSeek returned HTTP 400: ...` are preserved in full.

### What does `tool_use ids were found without tool_result blocks immediately after` mean?

The message history sent to the model contains a tool call that is not immediately followed by its corresponding result. Typical causes include an interrupted old Session, switching to a compatible endpoint with different message rules, or an older version persisting an incomplete tool turn.

Upgrade to the latest Runtime and try a new task first. If only an old task fails, retain it for diagnosis and continue in a new task. If a new task also fails, record the complete HTTP error, Provider protocol, model ID, and Session ID.

### How can I keep code private?

With Ollama or LM Studio and the `offline` strategy, model traffic can remain on the local machine. Cloud Providers receive the prompts and tool results required for the selected request; SeekClaw does not additionally upload code to its own service.

## Build and release

### How do I build Desktop and Runtime in one step?

Double-click `build.cmd` in the repository root on Windows. It locates `py.exe` or `python.exe` and launches `build.py`. After a successful build, run:

```text
publish\SeekClaw-win-x64\SeekClaw.exe
```

Distribute the complete `SeekClaw-win-x64` folder.

### What if Electron packaging cannot download its binaries?

The build script configures mirrors for Electron and electron-builder and retries packaging up to three times. If it still fails:

1. verify that the network or proxy can reach `npmmirror.com`;
2. run `build.cmd` again so existing download caches can be reused;
3. check system time and TLS certificates;
4. set `ELECTRON_MIRROR` and `ELECTRON_BUILDER_BINARIES_MIRROR` before launching if a custom mirror is required.

`build.cmd --skip-tests` skips tests but still compiles both Runtime and Desktop.

## Diagnostics

Use “Settings → Diagnostics & Usage” in Desktop or run this from the CLI:

```bash
seekclaw doctor
```

Checks cover the workspace, metadata directory, Provider configuration, Memory, and Provider connectivity. Provider 401/404 results remain available in the details instead of being reduced to a generic failure.

## Release notes {#release-notes}

### Desktop 2.0.x / CLI 1.3.x

- **Desktop Client Upgrade**: Built on Electron + Vite + Vue 3, bundling a self-contained Windows x64 .NET 10 Runtime requiring zero SDK pre-requisites.
- **Three-Tier Approval Modes**: Integrates Ask for approval (`manual`), Approve for me (`guardrail`), and Full access (`full`) safety tiers.
- **Computer Use & Ambient Halo**: Adds OS automation tools (`computer`, `computer_inspect`) with volumetric screen-edge breathing halo lighting.
- **Structured Task Planner**: Unfolds interactive real-time subtask checklists (`update_plan`).
- **Scheduled Tasks & Background Automation**: Built-in Cron scheduling engine with dedicated Desktop dialog for task management.
- **Official Skills Hub**: Direct marketplace integration for discovering, downloading, and hot-reloading official and community skills via UI or `seekclaw skill install`.
- **Consolidated SQLite Storage**: Migrated conversation history and project metadata to centralized `seekclaw.db` with automatic legacy JSONL migration.
- **Mid-turn Steering**: Allows queuing supplemental instructions mid-stream without interrupting the current tool turn.
- **Multi-Provider Failover**: Automatic retries, circuit breaking, and candidate chains across OpenAI- and Anthropic-compatible providers.

The Runtime IPC protocol version is `2.1`. See the [Daemon documentation](/en/doc/daemon) for the full method contract.
