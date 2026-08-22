# Execution Modes & Interactive Planning

SeekClaw provides four execution modes designed to grant fine-grained control over model behavior, tool authorization, and user interaction across diverse coding scenarios.

---

## Mode Overview

| Mode | Command / Flag | Permissions | Best For |
| :--- | :--- | :--- | :--- |
| **Edit** | `/mode edit` | Full read/write | Everyday development, refactoring, fixing bugs, and running verifications |
| **Plan** | `/mode plan` | Read-only analysis | Architectural design, step breakdown, and multi-phase roadmaps |
| **Auto** | `/mode auto` | Autonomous loop | Clear tasks with automated verification test loops |
| **ReadOnly** | `/mode readonly` | Strict read-only | Code review, repo exploration, explanation, and security auditing |

---

## 1. Edit Mode (Default Mode)

Edit is the baseline development mode. The agent has access to all active built-in tools (`read_file`, `edit_file`, `write_file`, `bash`) and MCP tools.

### Key Characteristics
- **Surgical Edits**: Prefers `edit_file` with precise unique matching strings, preserving existing formatting and comments.
- **Automated Verification**: Automatically triggers build/test suites when configured (`AutoVerify`).
- **Transparent Execution**: Every tool call and reasoning phase is streamed in real-time.

```bash
# Switch to Edit mode in CLI
/mode edit
```

---

## 2. Plan Mode (Architecture & Step Breakdown)

When tackling complex refactors, new subsystem design, or ambiguous feature requests, editing files immediately often leads to wasted iterations. Plan mode forces the agent to investigate thoroughly and establish a concrete roadmap first.

### Workflow
1. **Tool Sandbox Downgrade**: All mutating tools (`edit_file`, `write_file`, Git commit) are disabled or rejected.
2. **Deep Repo Investigation**: The agent uses `glob`, `grep`, and `read_file` to understand structure and dependencies.
3. **Structured Plan Output**: Generates an ordered, verifiable task list.
4. **Transition to Execution**: Once reviewed, switch back to `/mode edit` to proceed.

---

## 3. Auto Mode (Autonomous Execution Loop)

Auto mode is ideal for objective-driven tasks with automated verification feedback (e.g., "Fix all 3 failing unit tests in the auth suite").

- Agent executes consecutive tool calls autonomously without requiring manual confirmation for every step.
- Automatically repairs errors when compilation or test suites fail (up to `MaxRepairAttempts`).
- Terminates automatically when all verification passes.

---

## 4. ReadOnly Mode (Security Audit & Code Review)

Used for auditing unfamiliar repositories, reviewing pull requests, or answering questions safely.

- All mutating tools are strictly prohibited.
- Safe for production environments, shared servers, and demonstrations.

---

## 5. Mid-turn Steering & Dynamic Interventions

SeekClaw features **Mid-turn Steering**. While the model is streaming its thoughts or executing a sequence of tools, users do not need to press Ctrl+C to abort!

### How it Works
1. **Non-blocking Input**: Type instructions in the input bar at any time while the agent is running.
2. **Prompt Injection**: The message is packaged as a high-priority steering prompt (`[User Steering Instruction]`) and appended to the next model step.
3. **Instant Course Correction**: The model adapts immediately after its current atomic tool step.
