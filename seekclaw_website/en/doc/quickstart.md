# Quick Start

The fastest way to use SeekClaw is the packaged Windows Desktop client. Choose the CLI or a source build when you need a terminal workflow or want to develop SeekClaw itself.

## Option 1: Desktop (recommended)

### 1. Launch

Obtain the `SeekClaw-win-x64` release directory, keep every file in it, and run:

```text
SeekClaw-win-x64\SeekClaw.exe
```

The release contains a self-contained .NET Runtime. Desktop connects to an existing Daemon or starts `resources\runtime\seekclaw.exe` automatically; users do not start the Runtime themselves.

### 2. Configure a model

1. Open Settings from the lower-left controls.
2. Select “Models & Providers.”
3. Edit a Provider and enter its API key, Base URL, and model list.
4. Save, run “Test,” and then use the Provider or select an active model.

An explicitly stored API key is shown and editable in Desktop. Values sourced through environment variables are not copied back into the UI.

### 3. Create a task

- Select “New task” and choose a project directory to make file, terminal, and Git features available to the Agent.
- Expand “Global tasks” and create a directory-free task for general conversation.
- Clicking a starter prompt fills the composer but does not send it. Review the text and send it manually.

See the [Desktop guide](/en/doc/desktop) for the complete UI and release workflow.

## Option 2: Build the Desktop release from source

The build machine requires:

- Windows x64;
- the .NET 10 SDK;
- Node.js and pnpm;
- Python 3;
- network access to an Electron binary mirror.

```powershell
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw
build.cmd
```

Windows users can double-click `build.cmd`. It launches `build.py`, tests and builds the latest Runtime and Desktop, and produces:

```text
publish\SeekClaw-win-x64\SeekClaw.exe
```

Distribute the complete `SeekClaw-win-x64` directory.

## Option 3: Run the CLI

Running the CLI from source requires the .NET 10 SDK. Git enables repository-aware project features.

```bash
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw
dotnet build

# Configure and test a Provider
dotnet run --project seekclaw_cli -- provider add --id openai --kind openai --base-url "https://api.openai.com/v1" --api-key "sk-..." --model "gpt-5.5"
dotnet run --project seekclaw_cli -- provider test openai

# Start interactive mode
dotnet run --project seekclaw_cli
```

Run a single task directly:

```bash
dotnet run --project seekclaw_cli -- "Analyze this project's architecture and dependencies"
```

Resume sessions or override the model:

```bash
dotnet run --project seekclaw_cli -- --continue
dotnet run --project seekclaw_cli -- --resume <session-id>
dotnet run --project seekclaw_cli -- --model "anthropic/claude-sonnet-5" -- "Inspect and fix the tests"
```

## Diagnostics

Desktop users can open “Settings → Diagnostics & Usage.” CLI users can run:

```bash
dotnet run --project seekclaw_cli -- doctor
```

Diagnostics inspect the workspace, configuration, Provider connectivity, Memory, and Runtime directories. Current versions preserve the HTTP status and complete Provider response on failed requests, making model-protocol and message-shape problems easier to identify.
