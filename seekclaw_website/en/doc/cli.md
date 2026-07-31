# CLI Command Reference

`seekclaw_cli` is SeekClaw's terminal client and also provides the `seekclaw.exe daemon` entry point used by the packaged Runtime. Desktop users normally do not run these commands manually, but CLI and Desktop use the same global configuration and session format.

For a source checkout, replace `seekclaw` in the examples with `dotnet run --project seekclaw_cli --`.

## Conversation

```bash
# Interactive mode (equivalent forms)
seekclaw
seekclaw chat

# One-shot task
seekclaw "Analyze this project and fix the tests"

# Continue the newest Session in this workspace
seekclaw --continue
seekclaw chat --continue

# Resume a specific Session
seekclaw --resume <session-id>

# Override the model for this run without changing the stored Profile
seekclaw --model "anthropic/claude-sonnet-5" "Review the authentication code"
```

During interactive use, the first `Ctrl+C` cancels the active turn. Use it again while idle to exit.

## Sessions

```bash
# List the newest 30 Sessions in the active workspace
seekclaw sessions

# Resume using an ID from the list
seekclaw chat --resume <session-id>
```

Archiving, archive restoration, and deletion are currently exposed through Desktop or the Daemon IPC `session.*` administration methods.

## Providers

```bash
seekclaw provider list

# Start the interactive add workflow
seekclaw provider add

# Add non-interactively
seekclaw provider add --id deepseek --kind openai \
  --base-url "https://api.deepseek.com/v1" \
  --api-key "your-api-key" \
  --model "deepseek-chat"

seekclaw provider edit deepseek --timeout 120 --priority 1
seekclaw provider test deepseek
seekclaw provider use deepseek
seekclaw provider remove deepseek
```

Omit the ID from `provider test` to probe all enabled Providers.

## Models and Profiles

```bash
seekclaw model list
seekclaw model info "anthropic/claude-opus-5"
seekclaw model search quality
seekclaw model test "openai/gpt-5.5"
seekclaw model use "openai/gpt-5.5"
seekclaw model stats

seekclaw profile list
seekclaw profile create work --provider openai --model gpt-5.5 --strategy quality --temperature 0.2
seekclaw profile use work
seekclaw profile delete work

# Interactively select Provider, model, and route
seekclaw switch
```

## Usage and diagnostics

```bash
seekclaw usage
seekclaw usage --days 7
seekclaw doctor
```

`usage` aggregates calls, success rate, input and output tokens, cost, and average latency by Provider and model. `doctor` checks configuration, workspace, prompts, Providers, and the active model.

## Workspace, Skills, and MCP

```bash
# Initialize .seekclaw directories and .gitignore entries
seekclaw init

seekclaw skill list
seekclaw skill enable code-review
seekclaw skill disable code-review

seekclaw mcp list
seekclaw mcp test
```

`mcp test` connects to every enabled server and reports the number of discovered tools.

## Daemon

```bash
seekclaw daemon
```

The Windows endpoint is fixed at `\\.\pipe\seekclaw`; Linux and macOS use `~/.seekclaw/daemon.sock`. The Daemon has no custom `--pipe` option. Desktop starts and stops its managed Daemon automatically, so manual execution is mainly useful for protocol development and debugging.

See [Daemon and IPC 2.1](/en/doc/daemon) for the method contract.
