# 工作区、任务与 Memory

工作区决定项目任务可访问的目录、项目类型、会话位置和项目级配置。Desktop 还提供不绑定目录的任务，两种范围使用独立的 Session 存储。

## 项目工作区识别

Runtime 从当前目录向上查找 `.git`、`.seekclaw`、`package.json`、`pyproject.toml`、`Cargo.toml`、`go.mod`、`*.sln` 或 `*.slnx` 等根标记，并检测以下项目类型：

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

## `.seekclaw` 目录

新工作区执行 `seekclaw init` 或 Desktop 初始化后，默认结构为：

```text
<workspace>/
└── .seekclaw/
    ├── config.json          # 可选的工作区配置覆盖
    ├── prompts/             # 项目专用 Prompt 模板
    ├── memory/
    │   └── MEMORY.md        # 项目长期架构与开发约定
    ├── cache/               # 工作区临时缓存
    ├── logs/                # 诊断与运行日志
    ├── skills/              # 本地专属技能定义
    ├── mcp/
    │   └── servers.json     # 项目级 MCP 服务器配置
    └── docs/
```

会话数据和任务历史统一持久化在全局 SQLite 数据库 `~/.seekclaw/seekclaw.db` 中。首次升级访问工作区时，旧的 `.seekclaw/sessions/*.jsonl` 会自动导入数据库，原文件保留为安全备份。初始化也会给 `.gitignore` 自动补充 SeekClaw 忽略项。

## 不绑定项目的任务

不绑定项目的任务使用 `~/.seekclaw` 下的 Session 空间，Session 头不会写入项目路径。它没有固定项目目录，但文件与命令工具仍可正常使用；Runtime 会跳过工作区 Prompt 与自动构建验证。

一个项目或未绑定项目的范围都可以没有任务。Desktop 只有在首条消息发送时才创建 Session；侧栏的“任务”区域显示不绑定项目的任务，项目任务仍列在对应项目下。

## Memory

项目 Memory 位于 `.seekclaw/memory/MEMORY.md`。Agent 组装提示词时会读取该文件，可用于记录稳定的架构、命名、测试和发布约定：

```markdown
# 项目约定

- 数据访问统一使用 Dapper。
- 公共 API 修改必须补充集成测试。
- 发布前运行 `dotnet test` 与前端类型检查。
```

只写长期有效且确实需要跨 Session 保留的信息；临时任务进度应留在具体 Session 中。不绑定项目的任务不会注入某个项目的 Memory。
