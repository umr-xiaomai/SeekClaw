# SeekClaw

<div align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![GitHub Stars](https://img.shields.io/github/stars/umr-xiaomai/SeekClaw.svg)](https://github.com/umr-xiaomai/SeekClaw/stargazers)
[![GitHub Forks](https://img.shields.io/github/forks/umr-xiaomai/SeekClaw.svg)](https://github.com/umr-xiaomai/SeekClaw/network/members)
[![GitHub Issues](https://img.shields.io/github/issues/umr-xiaomai/SeekClaw.svg)](https://github.com/umr-xiaomai/SeekClaw/issues)
[![GitHub Pull Requests](https://img.shields.io/github/issues-pr/umr-xiaomai/SeekClaw.svg)](https://github.com/umr-xiaomai/SeekClaw/pulls)
[![Build Status](https://img.shields.io/github/actions/workflow/status/umr-xiaomai/SeekClaw/dotnet.yml?branch=master)](https://github.com/umr-xiaomai/SeekClaw/actions)

**现代化、高性能的 AI Agent 运行时**

SeekClaw 是基于 .NET 10.0 构建的高性能 AI Agent 运行时，采用清洁架构和事件驱动设计。它为构建 AI 驱动的编码助手提供了完整平台，支持多 LLM 提供商、工具执行、会话管理和流畅的终端交互体验。

[English](README_EN.md) •
[快速开始](#-安装) •
[功能特性](#-功能特性) •
[贡献指南](#-贡献指南)

</div>

## ✨ 功能特性

### 🚀 Runtime First 架构
- **清洁架构**：以 `seekclaw_runtime` 为核心，`seekclaw_cli` 作为默认前端，关注点分离
- **插件系统**：通过 Tools、Skills 和 MCP（Model Context Protocol）实现可扩展性
- **事件驱动**：通过事件总线实现渲染与业务逻辑解耦

### 🤖 多提供商支持
- **OpenAI 兼容**：GPT-5.5、GPT-5.5-mini 及所有 OpenAI 兼容 API
- **Anthropic**：Claude Opus、Claude Sonnet、Claude Haiku
- **Google**：Gemini Pro、Gemini Flash
- **本地模型**：Ollama、LM Studio
- **智能路由**：快速、均衡、质量、经济、离线等多种策略
- **故障转移**：自动重试、指数退避和熔断器机制

### 🛠️ 工具生态系统
- **内置工具**：文件操作（读/写/编辑）、搜索（grep/glob）、bash 执行
- **MCP 支持**：stdio 和 SSE 传输，自动发现工具/提示/资源
- **技能系统**：基于目录的技能系统，支持提示注入和工作流

### 💻 现代化终端体验
- **游戏式渲染**：30-60 FPS 刷新率，双缓冲技术
- **流式输出**：实时 token 流式传输，显示思考/推理过程
- **实时 UI**：加载动画、进度条、工具状态和 Markdown 渲染
- **增量更新**：无闪烁、无滚动、平滑动画

### 📁 工作区管理
- **项目识别**：自动识别 Git、.NET、Node.js、Python、Rust、Go、Unity、Vue 项目
- **隔离配置**：每个工作区独立的配置、会话、缓存和内存
- **自动初始化**：自动创建 `.seekclaw/` 目录结构

### 🔧 开发者体验
- **会话管理**：基于 JSONL 的会话持久化和恢复
- **内存系统**：工作区特定的内存，自动上下文注入
- **验证机制**：代码修改后自动构建/检查/修复循环
- **热重载**：提示文件和配置无需重启即可重载

## 📦 安装

### 前置要求
- .NET 10.0 SDK 或更高版本
- Git（用于工作区检测）

### 从源码构建
```bash
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw
dotnet build
```

### 运行
```bash
# 交互式聊天模式
dotnet run --project seekclaw_cli

# 单次提示
dotnet run --project seekclaw_cli -- "解释这个项目的架构"

# 继续上一个会话
dotnet run --project seekclaw_cli -- --continue

# 恢复特定会话
dotnet run --project seekclaw_cli -- --resume <session-id>

# 覆盖模型
dotnet run --project seekclaw_cli -- --model "openai/gpt-5.5"
```

## 🏗️ 架构设计

```mermaid
flowchart TD
    subgraph Frontends[前端]
        CLI[seekclaw_cli<br/>System.CommandLine + 渲染引擎]
        GUI[GUI / Web / IDE<br/>（未来）]
    end

    subgraph Runtime[seekclaw_runtime]
        Facade[SeekClawRuntime<br/>组合根 / Facade]
        Agent[Agent 主循环]
        Bus[(EventBus)]
        subgraph Provider[提供商层]
            PM[ProviderManager<br/>路由·重试·故障转移·熔断]
            MR[ModelRegistry]
            OAI[OpenAiCompatibleClient]
            ANT[AnthropicClient]
            HC[HealthChecker]
            UT[UsageTracker]
        end
        subgraph Plugins[插件体系]
            TR[ToolRegistry]
            PR[PromptRegistry]
            SK[SkillManager]
            MCP[McpManager<br/>stdio / SSE]
        end
        PP[PromptProvider<br/>文件化·热加载·变量]
        WS[WorkspaceManager]
        SS[SessionStore]
        VF[BuildVerifier]
        CFG[ConfigStore<br/>~/.seekclaw/config.json]
        DMN[DaemonServer<br/>Named Pipe / Unix Socket]
    end

    CLI --> Facade
    GUI -. daemon 协议 .-> DMN
    DMN --> Agent
    Facade --> Agent
    Agent --> PM
    Agent --> TR
    Agent --> VF
    Agent --> SS
    Agent --> Bus
    PM --> MR
    PM --> OAI
    PM --> ANT
    PM --> UT
    SK --> PR
    MCP --> TR
    MCP --> PR
    Agent --> PP
    PP --> PR
    WS --> CFG
    Bus --> CLI
```

## 📁 项目结构

```
SeekClaw/
├── seekclaw_cli/           # CLI 前端
│   ├── Commands/           # CLI 命令（provider、model、profile 等）
│   ├── Ui/                 # 终端渲染引擎
│   └── Program.cs          # 入口点
├── seekclaw_runtime/       # 核心运行时
│   ├── Agents/             # Agent 循环和上下文规划
│   ├── Configuration/      # 配置管理
│   ├── Events/             # 事件总线系统
│   ├── Mcp/                # MCP 客户端实现
│   ├── Prompts/            # 提示加载和组合
│   ├── Providers/          # LLM 提供商集成
│   ├── Sessions/           # 会话持久化
│   ├── Skills/             # 技能管理
│   ├── Tools/              # 工具注册和实现
│   ├── Verification/       # 构建验证
│   └── Workspaces/         # 工作区检测和管理
├── seekclaw_tests/         # 单元测试
├── docs/                   # 文档
├── prompts/                # 提示模板（未来）
├── skills/                 # 技能定义（未来）
└── mcp/                    # MCP 服务器配置（未来）
```

## ⚙️ 配置

### 全局配置
位于 `~/.seekclaw/config.json`：
```json
{
  "providers": {
    "openai": {
      "apiKey": "sk-...",
      "baseUrl": "https://api.openai.com/v1"
    },
    "anthropic": {
      "apiKey": "sk-ant-..."
    }
  },
  "profiles": {
    "default": {
      "provider": "openai",
      "model": "gpt-5.5"
    }
  },
  "agent": {
    "maxSteps": 10,
    "maxRepairAttempts": 3,
    "autoVerify": true
  }
}
```

### 工作区配置
每个项目可以在 `.seekclaw/config.json` 中覆盖：
- 提供商和模型选择
- 温度和上下文设置
- 工具权限
- 技能配置
- MCP 服务器设置

## 🎯 使用示例

### 交互式聊天
```bash
seekclaw chat
# 或直接
seekclaw
```

### 单次任务
```bash
seekclaw "将认证模块重构为使用 JWT"
seekclaw "为 UserService 类编写单元测试"
seekclaw "修复项目中的构建错误"
```

### 提供商管理
```bash
seekclaw provider list
seekclaw provider add openai --api-key sk-...
seekclaw provider test openai
seekclaw provider use anthropic
```

### 模型管理
```bash
seekclaw model list
seekclaw model use openai/gpt-5.5
seekclaw model info claude-opus
seekclaw model search "快速编码模型"
```

### 会话管理
```bash
seekclaw session list
seekclaw session resume <session-id>
seekclaw session export <session-id> --format json
```

### 健康检查
```bash
seekclaw doctor
```

## 🔌 扩展 SeekClaw

### 添加工具
实现 `ITool` 接口：
```csharp
public class MyTool : ITool
{
    public string Name => "my_tool";
    public string Description => "执行有用的操作";
    public JsonElement ParameterSchema => /* JSON schema */;
    public bool Mutating => false;
    public string StatusLabel => "正在运行我的工具";
    
    public async Task<ToolResult> ExecuteAsync(
        JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        // 实现
    }
}
```

### 创建技能
在 `skills/` 目录中创建：
```
skills/
  my-skill/
    skill.yaml          # 元数据和配置
    prompt.txt          # 提示模板
    tools/              # 可选的工具实现
```

### MCP 服务器
在 `mcp/servers.json` 中配置：
```json
{
  "servers": {
    "my-server": {
      "command": "node",
      "args": ["path/to/server.js"],
      "transport": "stdio"
    }
  }
}
```

## 🧪 测试

```bash
# 运行所有测试
dotnet test seekclaw_tests

# 运行特定测试类
dotnet test seekclaw_tests --filter "ClassName=ProviderTests"
```

## 📊 监控

SeekClaw 包含内置监控：
- **使用统计**：Token 计数、成本、响应时间
- **健康检查**：提供商可用性和延迟
- **熔断器**：自动故障检测和恢复
- **会话分析**：对话历史和模式

## 🤝 贡献指南

1. Fork 本仓库
2. 创建功能分支 (`git checkout -b feature/amazing-feature`)
3. 提交更改 (`git commit -m '添加令人惊叹的功能'`)
4. 推送到分支 (`git push origin feature/amazing-feature`)
5. 创建 Pull Request

### 开发规范
- 遵循 SOLID 原则
- 保持清洁架构
- 为新功能编写单元测试
- 将提示存储在外部文件中（禁止硬编码字符串）
- 使用事件驱动模式进行 UI 更新

## 📄 许可证

本项目采用 MIT 许可证 - 查看 [LICENSE](LICENSE) 文件了解详情。

## 🙏 致谢

- 使用 .NET 10.0 和 System.CommandLine 构建
- 使用 Spectre.Console 进行终端渲染
- 遵循清洁架构和垂直切片模式
- 采用 Native AOT 友好的设计理念

---

**SeekClaw** - 基于现代化 .NET 的高性能 AI Agent 运行时。