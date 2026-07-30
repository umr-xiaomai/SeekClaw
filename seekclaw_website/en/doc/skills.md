# Skills

A Skill is a directory-based prompt extension. The prompt from every enabled Skill is composed into the Skill prompt slot on each turn. Directory and prompt edits are discovered again on the next turn.

## Locations and precedence

- Global: `~/.seekclaw/skills/<skill-name>/`
- Workspace: `<workspace>/.seekclaw/skills/<skill-name>/`
- Legacy compatibility: an existing root-level `skills/` directory remains in use.

A workspace Skill overrides a global Skill with the same name. Global tasks have no project Skill root but can still use global Skills.

## Files

```text
skills/
└── code-review/
    ├── skill.yaml
    └── prompt.txt
```

The current manifest fields are:

```yaml
name: code-review
description: Review code for correctness and maintainability
version: 1.0.0
prompt: prompt.txt
```

`prompt` can name another file inside the Skill directory and defaults to `prompt.txt`. A bare directory containing only `prompt.txt` is also discovered, with its directory name used as the Skill name.

`triggers`, `tags`, `priority`, and a manifest-level `enabled` flag are not current Runtime fields. Enablement is stored separately in global `state.json` or workspace `disabledSkills`.

## Prompt content

```markdown
You are reviewing code:

1. Report correctness and security issues first.
2. Give a concrete file location and repair for each issue.
3. Never remove tests merely to make verification pass.
```

A Skill contributes text only and does not automatically load Skill-specific C# tools. Use `ITool` registration or an MCP Server for tool extensions.

## Administration

Desktop lists and toggles Skills under “Settings → Skills.” CLI commands are:

```bash
seekclaw skill list
seekclaw skill enable code-review
seekclaw skill disable code-review
```
