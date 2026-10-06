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

## 3. Managing Scheduled Tasks

Scheduled tasks are executed automatically in the background by the Daemon process. You can manage them through the **Desktop graphical UI** or the **Daemon IPC protocol**:

### Desktop GUI Management (Recommended)

1. Open the **"Scheduled Tasks"** dialog from the top header or sidebar in SeekClaw Desktop;
2. **Create Task**: Set a descriptive name, 5-part cron expression, target workspace directory, and prompt;
3. **Toggle Status**: Instantly enable or pause any task;
4. **Trigger Immediately**: Click "Run Now" to immediately create a dedicated session and execute the task in the background without waiting for the next cron tick;
5. **Execution Logs**: View timestamps, duration, status (success, error, timeout), and truncated output summaries.

### Daemon IPC Protocol

Automation pipelines and external tools can send JSONL IPC requests directly to the Daemon:

```json
{"id":1,"method":"schedule.list","params":{}}
{"id":2,"method":"schedule.create","params":{"name":"Nightly Check","prompt":"Run unit tests","cron":"0 2 * * *","workspace":"/var/projects/seekclaw"}}
{"id":3,"method":"schedule.toggle","params":{"id":"task-123","enabled":false}}
{"id":4,"method":"schedule.run","params":{"id":"task-123"}}
{"id":5,"method":"schedule.delete","params":{"id":"task-123"}}
```

---

## 4. Safety & Concurrency Guarantees

- **Non-overlapping Execution**: If a previous run is still active when the next trigger fires, the engine skips overlapping execution to avoid workspace thrashing.
- **FileLockCoordinator Integration**: Scheduled turns acquire process locks automatically before modifying workspace assets.
- **EventBus Notifications**: Start, progress, and completion events are broadcast in real time across connected Desktop clients.
