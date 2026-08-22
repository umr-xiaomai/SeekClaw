# Security Architecture & Permission Isolation

As an industrial-grade AI Agent Runtime, SeekClaw treats safety and defense-in-depth as core architecture pillars. Multi-layered boundaries govern tool execution, file system operations, process concurrency, and network egress.

---

## 1. Tool Approval Policies

SeekClaw offers configurable approval policies to prevent unintended destructive actions:

| Policy | Behavior | Best For |
| :--- | :--- | :--- |
| `never` | Fully automated execution within safe workspace scope | Daily local development, CI/CD pipelines |
| `ask_destructive` | Auto-executes standard tools; prompts for approval before destructive operations | Production codebases, team repos (Recommended) |
| `always` | Prompts for manual user confirmation before every tool call | High-security environments, demonstrations |

---

## 2. Destructive Action Interception

The runtime enforces safety gates against high-risk commands:

- **Destructive Git Actions**: Intercepts commands like `git reset --hard`, `git clean -fdx`, and `git push --force` that could destroy uncommitted work without user intent.
- **System Directory Protection**: Prohibits file modifications to critical system paths (`/etc`, `C:\Windows`) and user credentials (`~/.ssh`, `~/.aws`).
- **Execution Timeouts**: Subprocesses spawned via `bash` or system tools carry hard timeouts to prevent runaway processes or CPU exhaustion.

---

## 3. Multi-Process FileLockCoordinator

When Desktop, CLI sessions, and background cron schedules run concurrently on the same machine, multiple agent turns might target the same files.

SeekClaw integrates a built-in cross-process lock coordinator:
1. **Exclusive Write Locks**: Modifying tools (`edit_file`, `write_file`) acquire exclusive write leases before touching files.
2. **Safe Shared Reads**: `read_file` allows concurrent readers while blocking conflicting writers.
3. **Lease Self-Healing**: If an agent process crashes unexpectedly, locks release automatically upon lease expiration, preventing deadlocks.

---

## 4. Network Isolation & Air-gapped Environments

- **Session Network Toggles**: Disabling network access immediately strips web tools (`web_search`, `web_fetch`) from the prompt prefix and rejects network calls.
- **100% Air-Gapped Operation**: Paired with local Ollama or vLLM backends, SeekClaw operates reliably in isolated enterprise networks with zero external data transmission.
