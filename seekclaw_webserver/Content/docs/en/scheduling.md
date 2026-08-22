# Scheduled Tasks & Automation Workflows

The SeekClaw Runtime includes a built-in SQLite-backed scheduling engine (`ScheduleService` and `ScheduleStore`), enabling cron-based and one-shot background task execution for automated code inspections, continuous health checks, and scheduled maintenance.

---

## 1. Key Use Cases

- **Nightly CI/CD & Build Diagnostics**: Run test suites at midnight, generating automatic summaries or fixing broken snapshots.
- **Dependency & Security Audits**: Periodically scan dependencies for vulnerabilities and draft update roadmaps in read-only mode.
- **Automated Summary Reports**: Aggregate weekly code commits and test coverage stats.

---

## 2. Schedule Task Schema

Scheduled tasks are persisted in the database with the following fields:

```json
{
  "id": "task-nightly-check",
  "name": "Nightly Build Verification",
  "prompt": "Run full unit tests and fix any compiler warnings",
  "cron": "0 2 * * *",
  "workspaceRoot": "/var/projects/seekclaw",
  "enabled": true,
  "maxIterations": 0,
  "mode": "auto"
}
```

### Schema Parameters
- `cron`: Standard 5-field cron expression (`minute hour day month weekday`), e.g., `0 * * * *` (hourly).
- `workspaceRoot`: Working directory mounted for the agent, inheriting its local `AGENTS.md`.
- `mode`: Recommended to use `auto` (autonomous execution & repair) or `readonly` (audit report only).

---

## 3. CLI Schedule Commands

```bash
# List all registered schedules and next run timestamps
seekclaw schedule list

# Register a recurring hourly formatting task
seekclaw schedule add --name "Format Check" --cron "0 * * * *" --prompt "Verify code formatting"

# Toggle a schedule on/off
seekclaw schedule toggle <task-id>

# View execution history logs
seekclaw schedule logs <task-id>
```

---

## 4. Safety & Concurrency Guarantees

- **Non-overlapping Execution**: If a previous run is still active when the next trigger fires, the engine skips overlapping execution to avoid workspace thrashing.
- **FileLockCoordinator Integration**: Scheduled turns acquire process locks automatically before modifying workspace assets.
- **EventBus Notifications**: Start, progress, and completion events are broadcast in real time across connected Desktop clients.
