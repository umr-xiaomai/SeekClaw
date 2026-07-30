# 工作区、全局任务与 Memory

工作区决定项目任务可访问的目录、项目类型、会话位置和项目级配置。Desktop 还提供不绑定目录的全局任务，两种范围使用独立的 Session 存储。

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
    ├── config.json          # 可选的工作区覆盖
    ├── prompts/             # 项目 Prompt
    ├── memory/
    │   └── MEMORY.md        # 项目长期约定
    ├── cache/
    ├── sessions/            # JSONL Session
    ├── logs/
    ├── skills/
    ├── mcp/
    │   └── servers.json
    └── docs/
```

为了兼容旧工作区，如果根目录已经存在 `.session/`、`skills/`、`mcp/` 或 `docs/`，Runtime 会继续使用这些目录。初始化也会给 `.gitignore` 补充 SeekClaw 状态目录条目。

## 全局任务

全局任务使用 `~/.seekclaw` 下的全局 Session 空间，Session 头不会写入项目路径。Runtime 会过滤所有 `RequiresWorkspace` 工具，并跳过工作区 Prompt 与自动构建验证，因此它适合通用问答而不是本地代码编辑。

一个项目或全局范围都可以没有任务。Desktop 只有在首条消息发送时才创建 Session；侧栏的“全局任务”只是展开对应列表。

## Memory

项目 Memory 位于 `.seekclaw/memory/MEMORY.md`。Agent 组装提示词时会读取该文件，可用于记录稳定的架构、命名、测试和发布约定：

```markdown
# 项目约定

- 数据访问统一使用 Dapper。
- 公共 API 修改必须补充集成测试。
- 发布前运行 `dotnet test` 与前端类型检查。
```

只写长期有效且确实需要跨 Session 保留的信息；临时任务进度应留在具体 Session 中。全局任务不会注入某个项目的 Memory。
