# 工作区、任务与 Memory

工作区决定项目任务可访问的目录、项目类型、会话范围和项目级配置。Desktop 也支持不绑定项目目录的任务。

## 项目工作区识别

Runtime 从当前目录向上查找 `.git`、`package.json`、`pyproject.toml`、`Cargo.toml`、`go.mod`、`*.sln` 或 `*.slnx` 等根标记，并检测以下项目类型：

| 类型 | 典型特征 |
| --- | --- |
| Git | `.git/` |
| .NET | `*.sln`、`*.slnx`、`*.csproj` 或 `*.fsproj` |
| Node / Vue | `package.json`，并进一步检查 Vue 依赖 |
| Python | `pyproject.toml`、`requirements.txt` 或 `setup.py` |
| Rust | `Cargo.toml` |
| Go | `go.mod` |
| Unity | `Assets/` 与 `ProjectSettings/` |

Desktop 在任务顶栏显示完整工作区目录，并把打开位置、终端、Git 变更与 Git 历史限制在该目录。

## 工作区状态目录

SeekClaw 将应用状态存放在 `~/.seekclaw`（Windows 上为 `%USERPROFILE%\.seekclaw`）。每个工作区在 `workspaces/` 下有独立目录，`<workspace-key>` 是工作区规范化完整路径的稳定 SHA-256 哈希。工作区移动后，如果规范化路径发生变化，就会得到新的键。

在项目中运行 `seekclaw init` 会初始化并打印其状态目录路径，不会改动项目目录或 `.gitignore`。项目专用配置和文件存放在这里：

```text
~/.seekclaw/workspaces/<workspace-key>/
├── config.json          # 可选的工作区配置覆盖
├── prompts/             # 项目专用 Prompt 模板
├── memory/
│   └── MEMORY.md        # 项目长期开发约定
├── skills/              # SeekClaw 工作区技能
└── mcp/
    └── servers.json     # 项目级 MCP 服务器配置
```

SeekClaw 会忽略项目内旧的 `.seekclaw/` 目录，不会迁移或删除其中的数据。仓库里的 `AGENTS.md` 和 `.gitignore` 仍分别用于提供 Agent 指令和文件忽略规则。

会话和 Desktop 项目列表保存在全局 SQLite 数据库 `~/.seekclaw/seekclaw.db`。Desktop 专用的应用程序配置目录为 `~/.seekclaw/desktop/`。

## 不绑定项目的任务

不绑定项目的任务使用同一个 SQLite 数据库中的独立会话范围。会话头不记录项目路径。它没有固定项目目录，但文件与命令工具仍可正常使用；Runtime 会跳过工作区 Prompt 与自动构建验证。

一个项目或未绑定项目的范围都可以没有任务。Desktop 只有在首条消息发送时才创建 Session；侧栏的“任务”区域显示不绑定项目的任务，项目任务仍列在对应项目下。

## Memory

项目 Memory 位于 `~/.seekclaw/workspaces/<workspace-key>/memory/MEMORY.md`。Agent 组装提示词时会读取该文件，可用于记录稳定的架构、命名、测试和发布约定：

```markdown
# 项目约定

- 数据访问统一使用 Dapper。
- 公共 API 修改必须补充集成测试。
- 发布前运行 `dotnet test` 与前端类型检查。
```

只写长期有效且确实需要跨 Session 保留的信息；临时任务进度应留在具体 Session 中。不绑定项目的任务不会注入某个项目的 Memory。
