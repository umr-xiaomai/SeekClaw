# Workspaces, Global Tasks, and Memory

A workspace defines the directory available to a project task, detected project kinds, session location, and project-level overrides. Desktop also supports directory-free global tasks, with a separate Session store for each scope.

## Project workspace detection

The Runtime walks upward for root markers such as `.git`, `.seekclaw`, `package.json`, `pyproject.toml`, `Cargo.toml`, `go.mod`, `*.sln`, or `*.slnx`, then detects these project kinds:

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

## The `.seekclaw` directory

After `seekclaw init` or Desktop workspace initialization, the default layout is:

```text
<workspace>/
└── .seekclaw/
    ├── config.json          # optional workspace overrides
    ├── prompts/             # project prompts
    ├── memory/
    │   └── MEMORY.md        # durable project conventions
    ├── cache/
    ├── sessions/            # JSONL Sessions
    ├── logs/
    ├── skills/
    ├── mcp/
    │   └── servers.json
    └── docs/
```

For compatibility with older workspaces, the Runtime continues to use root-level `.session/`, `skills/`, `mcp/`, or `docs/` directories when they already exist. Initialization also adds SeekClaw state entries to `.gitignore`.

## Global tasks

Global tasks use a global Session store under `~/.seekclaw`, and their Session headers contain no project path. The Runtime filters every tool that `RequiresWorkspace` and skips workspace prompts and automatic build verification. This scope is intended for general conversation rather than local code editing.

A project or global scope may contain no tasks. Desktop creates a Session only when the first message is sent; “Global tasks” in the sidebar simply expands that task list.

## Memory

Project Memory lives at `.seekclaw/memory/MEMORY.md`. The Agent reads it while composing prompts, making it suitable for stable architecture, naming, testing, and release conventions:

```markdown
# Project conventions

- Use Dapper for data access.
- Every public API change requires integration tests.
- Run `dotnet test` and frontend type checks before release.
```

Store only durable information that should survive across Sessions. Temporary task progress belongs in the individual Session. Global tasks never inject a project's Memory.
