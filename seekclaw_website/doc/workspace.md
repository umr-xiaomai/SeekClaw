# 工作区管理与 Memory 记忆体系

SeekClaw 具备**智能化项目识别**与**深度记忆持久化（Memory System）**机制。无需人工繁琐设置，启动时即可自动探测项目技术栈与工程结构。

---

## 自动项目类型识别

`IWorkspaceManager` 会分析当前工作区的特征标志文件，自动匹配最佳的开发体验与构建验证规则：

| 项目类型 | 识别特征文件 | 自动推荐的构建/检查工具 |
| --- | --- | --- |
| **.NET** | `*.sln`, `*.csproj`, `*.fsproj` | `dotnet build`, `dotnet test` |
| **Rust** | `Cargo.toml` | `cargo check`, `cargo test` |
| **Node.js / Vue / React** | `package.json` | `npm run build`, `pnpm run check` |
| **Python** | `pyproject.toml`, `requirements.txt`, `setup.py` | `pytest`, `mypy` |
| **Go** | `go.mod` | `go build ./...`, `go test ./...` |
| **Unity** | `Assets/`, `ProjectSettings/` | Unity BatchMode 校验 |
| **Git 仓库** | `.git/` | 自动识别分支与 Git 修改 Diff |

---

## 隔离的 `.seekclaw/` 目录规范

当在工作区运行 SeekClaw 时，系统会在该目录下保持隔离的环境与日志（可通过 `.gitignore` 忽略部分临时缓存）：

```
<workspace-root>/
├── .seekclaw/
│   ├── config.json              # 工作区专属配置覆盖
│   ├── memory/
│   │   └── MEMORY.md            # 项目知识库与架构记忆
│   ├── prompts/                 # 工作区自定义 Prompt 覆盖
│   └── mcp/
│       └── servers.json         # 项目专属 MCP 配置
├── .session/                    # 会话历史 (JSONL 格式)
├── .cache/                      # 符号索引与临时缓存
└── logs/                        # 运行日志
```

---

## Memory 记忆体系与自动注入

为了让 Agent 在跨会话开发时理解团队规范与独特架构设计，SeekClaw 会在每次组装 System Prompt 时自动注入 `.seekclaw/memory/MEMORY.md`。

### 示例 `MEMORY.md`：

```markdown
# 项目特定记忆与架构规范

- **数据库访问**：统一使用 Dapper 配合原生的 SQL 语句，禁止引入重型 EF Core。
- **异常处理**：所有的 Service 必须捕获 `BusinessException` 并使用 `Result<T>` 范式返回。
- **命名空间规范**：核心逻辑必须位于 `Company.Project.Domain` 命名空间内。
```

Agent 会自动阅读此规范并在后续生成的代码中强制遵循，避免因为上下文断链重复犯错。
