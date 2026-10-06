# CLI 命令参考

`seekclaw_cli` 是 SeekClaw 的终端前端，也是打包 Runtime 中 `seekclaw.exe daemon` 的入口。Desktop 用户通常不需要手动运行这些命令，但 CLI 与 Desktop 使用同一份全局配置和会话格式。

从源码运行时，在以下示例的 `seekclaw` 前替换为 `dotnet run --project seekclaw_cli --` 即可。

## 对话

```bash
# 交互模式（两种写法等价）
seekclaw
seekclaw chat

# 单次任务
seekclaw "分析当前项目并修复测试"

# 继续当前工作区最近一次 Session
seekclaw --continue
seekclaw chat --continue

# 恢复指定 Session
seekclaw --resume <session-id>

# 仅为本次运行覆盖模型，不修改保存的 Profile
seekclaw --model "anthropic/claude-sonnet-5" "审查认证代码"
```

交互过程中，第一次 `Ctrl+C` 取消活动 turn；空闲时再次使用退出程序。

## Session

```bash
# 列出当前工作区最近 30 个 Session
seekclaw sessions

# 使用列表中的 ID 恢复
seekclaw chat --resume <session-id>
```

归档、恢复归档和删除目前由 Desktop 或 Daemon IPC 的 `session.*` 管理方法提供。

## Provider

```bash
seekclaw provider list

# 不带参数时进入交互式添加
seekclaw provider add

# 非交互式添加
seekclaw provider add --id deepseek --kind openai \
  --base-url "https://api.deepseek.com/v1" \
  --api-key "your-api-key" \
  --model "deepseek-chat"

seekclaw provider edit deepseek --timeout 120 --priority 1
seekclaw provider test deepseek
seekclaw provider use deepseek
seekclaw provider remove deepseek
```

`provider test` 省略 ID 时测试所有启用的 Provider。

## Model 与模型切换

```bash
seekclaw model list
seekclaw model info "anthropic/claude-opus-5"
seekclaw model search quality
seekclaw model test "openai/gpt-5.5"
seekclaw model use "openai/gpt-5.5"
seekclaw model stats

# 交互式选择 Provider 与模型
seekclaw switch
```

## 用量与诊断

```bash
seekclaw usage
seekclaw usage --days 7
seekclaw doctor
```

`usage` 按 Provider / 模型汇总调用、成功率、输入 / 输出 Token、成本和平均延迟。`doctor` 检查配置、工作区、Prompt、Provider 和活动模型。

## 工作区、Skills 与 MCP

```bash
# 初始化 .seekclaw 目录和 .gitignore 条目
seekclaw init

# 查看已发现技能
seekclaw skill list

# 从官方技能市场、URL 或本地文件安装技能
seekclaw skill install code-review
seekclaw skill install https://example.com/my-skill.zip

# 启用或禁用技能
seekclaw skill enable code-review
seekclaw skill disable code-review

# MCP Server 管理与测试
seekclaw mcp list
seekclaw mcp test
```

`mcp test` 会连接每个已启用的 Server 并报告发现的工具数量。`skill install` 支持传入官方市场技能 slug、远程 HTTP/HTTPS zip 链接或本地文件路径。

## 交互模式斜杠命令

在 `seekclaw` 或 `seekclaw chat` 交互终端中，可直接输入 `/` 触发命令提示与补全：

| 命令 | 参数 | 说明 |
| --- | --- | --- |
| `/model` | `[provider/model]` | 查看或快速切换活动模型 |
| `/mode` | `[plan\|auto\|readonly\|edit]` | 查看或切换 Agent 执行模式 |
| `/cd` | `<directory>` | 切换工作区目录并重新识别项目 |
| `/mcp` | 无 | 查看已连接的 MCP 服务器与工具列表 |
| `/skills` | 无 | 列出当前工作区可用的技能 |
| `/doctor` | 无 | 实时运行环境与 Provider 健康诊断 |
| `/clear` | 无 | 清空当前上下文并开始新会话 |
| `/usage` | 无 | 查看当前 Token 与延迟统计表 |
| `/session` | 无 | 查看当前会话 ID 与状态信息 |
| `/copy` | 无 | 将上一条 Assistant 回答复制到剪贴板 |
| `/print` | `config` | 打印当前合并配置 |
| `/help` | 无 | 显示命令帮助 |
| `/exit` | 无 | 退出 SeekClaw |

## Daemon

```bash
seekclaw daemon
```

Windows 端点固定为 `\\.\pipe\seekclaw`；Linux / macOS 使用 `~/.seekclaw/daemon.sock`。Daemon 不接受自定义 `--pipe` 参数。Desktop 会自动启动和关闭自己管理的 Daemon，手动执行通常仅用于协议开发或调试。

协议方法见 [Daemon 与 IPC 2.1](/doc/daemon)。
