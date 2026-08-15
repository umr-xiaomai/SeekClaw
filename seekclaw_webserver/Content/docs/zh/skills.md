# Skills 技能

Skill 是一个目录化 Prompt 扩展。每个已启用 Skill 的 Prompt 会在每个 turn 组装到 Skill 提示槽中；修改目录或 Prompt 后，下一个 turn 会重新发现并读取内容。

## 目录与优先级

- 全局：`~/.seekclaw/skills/<skill-name>/`
- 工作区：`<workspace>/.seekclaw/skills/<skill-name>/`
- 兼容旧结构：如果项目根目录已经有 `skills/`，继续使用该目录。

同名工作区 Skill 覆盖全局 Skill。全局任务不绑定项目 Skill 根目录，但仍可使用全局 Skill。

## 文件结构

```text
skills/
└── code-review/
    ├── skill.yaml
    └── prompt.txt
```

当前 Manifest 支持以下字段：

```yaml
name: code-review
description: Review code for correctness and maintainability
version: 1.0.0
prompt: prompt.txt
```

`prompt` 可指向 Skill 目录内的其他文件，默认为 `prompt.txt`。只包含 `prompt.txt` 而没有 YAML 的裸 Skill 也可被发现，名称取目录名。

`triggers`、`tags`、`priority` 和 Manifest 内的 `enabled` 目前不是运行时字段；启用状态单独保存在全局 `state.json` 或工作区的 `disabledSkills` 中。

## Prompt

```markdown
你正在执行代码评审：

1. 优先报告正确性与安全问题。
2. 为每个问题给出具体文件位置和修复建议。
3. 不要为了通过检查而删除测试。
```

Skill Prompt 作为文本注入，不会自动携带专属 C# 工具。需要工具扩展时使用 `ITool` 注册或 MCP Server。

## 管理

Desktop 在“设置 → Skills”中列出并切换状态。CLI 使用：

```bash
seekclaw skill list
seekclaw skill enable code-review
seekclaw skill disable code-review
```
