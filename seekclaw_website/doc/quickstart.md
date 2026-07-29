# 快速开始指南

本指南帮助您在 5 分钟内快速安装、配置并开始使用 SeekClaw AI Agent 运行时。

---

## 前置要求

在构建和运行 SeekClaw 之前，请确保您的系统中已安装以下环境：

1. **.NET 10.0 SDK** 或更高版本
   - 检查命令：`dotnet --version`
2. **Git**（用于工作区项目类型自动检测与仓库感知）
   - 检查命令：`git --version`
3. 一个或多个 **LLM API Key**（如 OpenAI, Anthropic, Gemini, 或本地安装的 Ollama / LM Studio）

---

## 从源码构建

复制 SeekClaw GitHub 仓库源码并进行编译：

```bash
# 1. 克隆代码仓库
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw

# 2. 编译项目
dotnet build
```

---

## 极速配置 API Key

SeekClaw 支持交互式配置或直接编辑配置文件。

### 方式 A：通过 CLI 命令行添加（推荐）

```bash
# 添加 OpenAI API Key
dotnet run --project seekclaw_cli -- provider add openai --api-key "sk-proj-xxxxxxxx"

# 添加 Anthropic API Key
dotnet run --project seekclaw_cli -- provider add anthropic --api-key "sk-ant-xxxxxxxx"

# 测试提供商连接状态
dotnet run --project seekclaw_cli -- provider test openai
```

### 方式 B：手工编辑全局配置文件

配置文件默认创建于用户家目录 `~/.seekclaw/config.json`：

```json
{
  "providers": {
    "openai": {
      "apiKey": "sk-proj-xxxxxxxx",
      "baseUrl": "https://api.openai.com/v1"
    },
    "anthropic": {
      "apiKey": "sk-ant-xxxxxxxx"
    }
  },
  "profiles": {
    "default": {
      "provider": "openai",
      "model": "gpt-5.5"
    }
  }
}
```

---

## 运行 SeekClaw

### 1. 交互式 Chat 聊天模式

直接启动 SeekClaw 终端应用，进入无缝对流交互：

```bash
dotnet run --project seekclaw_cli
```

在交互模式中，您可以输入自然语言任务指令（如 *"帮我重构 UserService.cs 并补全单元测试"*）。SeekClaw 将自动流式推理、展示思考过程、调用文件操作工具，并在修改后自动进行代码编译校验。

### 2. 单次指令模式 (Single Shot)

通过命令行参数直接执行单次自动化任务：

```bash
dotnet run --project seekclaw_cli -- "分析当前项目的架构与依赖关系"
```

### 3. 会话恢复与继续

```bash
# 继续上一次中断的会话
dotnet run --project seekclaw_cli -- --continue

# 恢复特定的 Session ID
dotnet run --project seekclaw_cli -- --resume <session-id>
```

### 4. 覆盖指定模型

```bash
dotnet run --project seekclaw_cli -- --model "anthropic/claude-opus" -- "帮我重构底层锁逻辑"
```

---

## 系统运行状况诊断 (Doctor)

若遇到连接问题，可随时运行医生诊断命令：

```bash
dotnet run --project seekclaw_cli -- doctor
```

系统将自动检查 .NET 环境、配置文件合法性、供应商连通性、内置 Prompt 模板状态及全局缓存权限。
