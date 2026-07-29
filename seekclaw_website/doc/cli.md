# CLI 命令行交互指南

SeekClaw 的 `seekclaw_cli` 前端工具基于 `System.CommandLine` 构建，提供了简洁高效的极客终端交互指令体系。

---

## 核心基础指令

### 1. 默认交互聊天 (`chat`)

直接启动全屏游戏式流式终端，与 Agent 展开多轮深度对话：

```bash
seekclaw chat
# 或简写
seekclaw
```

在对话过程中：
- 直接输入需求文本发送。
- 按 **Ctrl+C** 可立即中断模型思考或正在执行的工具。
- 输入 `/exit` 或 `exit` 退出对话。

### 2. 单次提示指令 (Single-shot Mode)

无缝嵌入自动化 Shell 脚本或 CI/CD 流程：

```bash
seekclaw "重构 src/Auth.cs 移除过时的 MD5 哈希算法"
```

### 3. 会话恢复与断点接续

```bash
# 列出最近的所有会话
seekclaw session list

# 继续上一次在此工作区进行的中断会话
seekclaw --continue

# 恢复特定的 Session ID
seekclaw --resume <session-id>

# 将历史会话导出为 JSON 格式
seekclaw session export <session-id> --format json
```

---

## 管理类子命令详解

### Provider 提供商管理 (`seekclaw provider`)

```bash
# 查看已注册的 API 提供商及其健康状态
seekclaw provider list

# 添加新的 API 提供商配置
seekclaw provider add <name> --api-key <key> [--base-url <url>]

# 测试指定提供商的网络延迟与 API 可用性
seekclaw provider test <name>

# 切换当前全局使用的默认提供商
seekclaw provider use <name>
```

### Model 模型管理 (`seekclaw model`)

```bash
# 列出注册表中所有可用的模型别名
seekclaw model list

# 切换当前模型为指定参考模型
seekclaw model use openai/gpt-5.5

# 查看特定模型的详细配置（上下文窗口、Token 单价、能力位）
seekclaw model info claude-opus

# 搜索匹配特性的模型
seekclaw model search "快速编码模型"
```

### Workspace 工作区初始化 (`seekclaw init`)

在当前代码目录中初始化 SeekClaw 独立环境（自动创建 `.seekclaw/` 结构、`config.json` 与 `.seekclaw/memory/MEMORY.md` 基础模板）：

```bash
seekclaw init
```

### Diagnostics 诊断管理 (`seekclaw doctor`)

对本地系统运行环境进行一键自检与健康排错：

```bash
seekclaw doctor
```

主要检测点：
- .NET 10.0 SDK 与运行时完整性
- 家目录 `~/.seekclaw/config.json` 语法正确性
- API Keys 连通性与网络代理设置
- Git 命令行工具响应状态
- 提示词模板及缓存文件读写权限

### Session 会话导出 (`seekclaw session`)

```bash
seekclaw session list
seekclaw session resume <session-id>
seekclaw session export <session-id> --output ./history.json
```

### Skill 技能插件管理 (`seekclaw skill`)

```bash
# 查看工作区及全局可用技能
seekclaw skill list

# 启用或禁用特定 Skill
seekclaw skill enable code-review
seekclaw skill disable legacy-migration
```

### MCP 客户端管理 (`seekclaw mcp`)

```bash
# 检查工作区所有已连接的 MCP 服务器状态
seekclaw mcp status

# 测试重新连接 MCP 服务
seekclaw mcp reload
```

### Daemon 服务启动 (`seekclaw daemon`)

后台启动 Named Pipe / Unix Socket 监听进程，为外部 GUI / IDE 插件提供低延迟 JSON 协议服务：

```bash
seekclaw daemon --pipe seekclaw-daemon.pipe
```
