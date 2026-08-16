# CLI 命令参考

`seekclaw_cli` 是 SeekClaw 的终端前端，也是打包 Runtime 中 `seekclaw.exe daemon` 的入口。Desktop 用户通常不需要手动运行这些命令，但 CLI 与 Desktop 使用同一份全局配置和会话格式。

## 安装

通过 npm 安装已发布的 `seekclaw-cli`：

```powershell
npm install -g seekclaw-cli
seekclaw --version
seekclaw doctor
```

该包是自包含 .NET 二进制包，当前 Windows x64 平台无需单独安装 .NET SDK；前置要求为 Node.js 18 或更高版本，以及用于工作区检测的 Git。

安装后可直接使用 `seekclaw` 命令。从源码运行时，在以下示例的 `seekclaw` 前替换为 `dotnet run --project seekclaw_cli --` 即可。

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

## Model 与 Profile

```bash
seekclaw model list
seekclaw model info "anthropic/claude-opus-5"
seekclaw model search quality
seekclaw model test "openai/gpt-5.5"
seekclaw model use "openai/gpt-5.5"
seekclaw model stats

seekclaw profile list
seekclaw profile create work --provider openai --model gpt-5.5 --strategy quality --temperature 0.2
seekclaw profile use work
seekclaw profile delete work

# 交互式选择 Provider、模型和路由策略
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

seekclaw skill list
seekclaw skill enable code-review
seekclaw skill disable code-review

seekclaw mcp list
seekclaw mcp test
```

`mcp test` 会连接每个已启用的 Server 并报告发现的工具数量。

## Daemon

```bash
seekclaw daemon
```

Windows 端点固定为 `\\.\pipe\seekclaw`；Linux / macOS 使用 `~/.seekclaw/daemon.sock`。Daemon 不接受自定义 `--pipe` 参数。Desktop 会自动启动和关闭自己管理的 Daemon，手动执行通常仅用于协议开发或调试。

协议方法见 [Daemon 与 IPC 2.1](/doc/daemon)。
