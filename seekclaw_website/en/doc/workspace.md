# Workspaces, Global Tasks, and Memory

A workspace defines the directory available to a project task, detected project kinds, session scope, and project-level overrides. Desktop also supports global tasks with no project directory.

## Project workspace detection

The Runtime walks upward for root markers such as `.git`, `package.json`, `pyproject.toml`, `Cargo.toml`, `go.mod`, `*.sln`, or `*.slnx`, then detects these project kinds:

| Kind | Typical markers |
| --- | --- |
| Git | `.git/` |
| .NET | `*.sln`, `*.slnx`, `*.csproj`, or `*.fsproj` |
| Node / Vue | `package.json`, with an additional Vue dependency check |
| Python | `pyproject.toml`, `requirements.txt`, or `setup.py` |
| Rust | `Cargo.toml` |
| Go | `go.mod` |
| Unity | `Assets/` and `ProjectSettings/` |

Desktop shows the complete workspace path in the task header and scopes open-location, terminal, Git changes, and Git history actions to that directory.

## Workspace state directory

SeekClaw stores application state under `~/.seekclaw` (`%USERPROFILE%\.seekclaw` on Windows). Each workspace has a directory under `workspaces/`. Its `<workspace-key>` is the stable SHA-256 hash of the normalized full workspace path. Moving the workspace to a different normalized path gives it a different key.

Run `seekclaw init` from the project to initialize and print its state directory. The command leaves the project directory and `.gitignore` untouched. Place project-specific settings and assets here:

```text
~/.seekclaw/workspaces/<workspace-key>/
├── config.json          # optional workspace overrides
├── prompts/             # project prompt templates
├── memory/
│   └── MEMORY.md        # durable project conventions
├── skills/              # SeekClaw workspace skills
└── mcp/
    └── servers.json     # project-level MCP servers
```

SeekClaw ignores any legacy `.seekclaw/` inside the project and leaves it in place without migrating or deleting its contents. Repository `AGENTS.md` and `.gitignore` files continue to supply agent instructions and ignore rules.

Sessions and the Desktop project list are stored in the global SQLite database at `~/.seekclaw/seekclaw.db`. The dedicated Desktop app profile is stored at `~/.seekclaw/desktop/`.

## Global tasks

Global tasks use the same SQLite database under a separate session scope. Their session headers contain no project path. The Runtime filters every tool that `RequiresWorkspace` and skips workspace prompts and automatic build verification. This scope is intended for general conversation rather than local code editing.

A project or global scope may contain no tasks. Desktop creates a Session only when the first message is sent; “Global tasks” in the sidebar simply expands that task list.

## Memory

Project Memory lives at `~/.seekclaw/workspaces/<workspace-key>/memory/MEMORY.md`. The Agent reads it while composing prompts, making it suitable for stable architecture, naming, testing, and release conventions:

```markdown
# Project conventions

- Use Dapper for data access.
- Every public API change requires integration tests.
- Run `dotnet test` and frontend type checks before release.
```

Store only durable information that should survive across Sessions. Temporary task progress belongs in the individual Session. Global tasks never inject a project's Memory.
