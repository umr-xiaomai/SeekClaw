# AGENTS.md & Prompt Engineering

SeekClaw employs a layered prompt and specification injection architecture, allowing engineering teams to align AI agent behavior, coding conventions, and architectural boundaries through repository-level `AGENTS.md` files and persistent `MEMORY.md` knowledge bases.

---

## 1. The AGENTS.md Specification

`AGENTS.md` is the standard specification mechanism for human engineers to provide guidelines, architectural boundaries, and testing commands to AI agents.

### Scoping and Precedence
- **Proximity Rule**: `AGENTS.md` files can be placed anywhere in the repository directory hierarchy.
- **Hierarchical Inheritance**: Subdirectory `AGENTS.md` files inherit rules from ancestor directories. When rules conflict, **more deeply nested files take precedence**.
- **User Instructions Override**: Direct user prompts and system instructions always override `AGENTS.md` guidelines.

### Recommended Structure

```markdown
# AGENTS.md — Core Architecture & Style Guide

## Tech Stack
- Runtime: .NET 10.0 (C# 13)
- Database: SQLite with Dapper
- Unit Testing: xUnit + Moq

## Coding Standards & Invariants
- Never write inline SQL in API controllers; always route data access via repository interfaces.
- All public async methods must accept `CancellationToken ct = default` and pass it down.
- New public domain models must include XML doc comments.

## Verification Commands
- Run unit tests: `dotnet test seekclaw_tests`
- Format check: `dotnet format --verify-no-changes`
```

---

## 2. Long-term Workspace Memory (MEMORY.md)

For long-running codebases, agents need to retain cross-session context, past architectural decisions, and key lessons.

- **Location**: `<workspace>/.seekclaw/MEMORY.md`.
- **Automatic Context Fitting**: Loaded and bounded automatically into the system prompt.
- **Evolution**: The agent can autonomously append key findings to `MEMORY.md` upon resolving major issues.

---

## 3. Dynamic Template Variables

When crafting custom prompt templates, the following variables are available:

| Variable | Description | Example |
| :--- | :--- | :--- |
| `{{workspace}}` | Absolute workspace root | `/home/user/project` |
| `{{project}}` | Project name | `seekclaw` |
| `{{language}}` | Detected programming languages | `dotnet, csharp` |
| `{{os}}` | Operating system platform | `Linux (linux-x64)` |
| `{{tool}}` | Active tools list | `read_file, edit_file, bash` |
| `{{mode}}` | Current execution mode | `edit` / `plan` |
| `{{agents_md}}` | Extracted AGENTS.md content | *(content string)* |
| `{{memory}}` | Extracted MEMORY.md content | *(content string)* |

---

## 4. Best Practices for Engineering Teams

1. **Be Concrete**: Specify exact libraries and patterns (e.g. "Use System.Text.Json rather than Newtonsoft.Json").
2. **Include Few-Shot Examples**: Showing one positive pattern and one anti-pattern drastically improves model compliance.
3. **Keep Rules Modular**: Place repo-wide policies at the root, and component-specific guides inside package subfolders.
