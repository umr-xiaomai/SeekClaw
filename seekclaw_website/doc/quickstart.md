# 快速开始

图形化使用最快的是已打包的 Windows Desktop；终端工作流最快的是通过 npm 安装 `seekclaw-cli`。需要开发 SeekClaw 本身时，再选择源码构建。

## 方式一：通过 npm 安装 CLI（终端推荐）

```powershell
npm install -g seekclaw-cli
seekclaw --version
seekclaw
```

已发布的 `seekclaw-cli` 是自包含 .NET 二进制包，无需单独安装 .NET SDK。前置要求仅为 Node.js 18 或更高版本，以及用于工作区检测的 Git。

也可执行单次任务、恢复会话或诊断：

```powershell
seekclaw "分析当前项目的架构与依赖关系"
seekclaw --continue
seekclaw --resume <session-id>
seekclaw doctor
```

## 方式二：使用 Desktop（推荐）

### 1. 启动

获取 `SeekClaw-win-x64` 发布文件夹，保留其中的全部文件，然后运行：

```text
SeekClaw-win-x64\SeekClaw.exe
```

发布包内已经包含 .NET Runtime。Desktop 会自动连接现有 Daemon，或启动 `resources\runtime\seekclaw.exe`，无需用户手动打开 Runtime。

### 2. 配置模型

1. 打开左下角设置入口。
2. 进入“模型与 Provider”。
3. 编辑一个 Provider，填写 API Key、Base URL 与模型列表。
4. 保存后点击“测试”，再选择“使用”或切换活动模型。

API Key 需要直接保存到配置文件中的 `apiKey` 字段，Desktop 会显示并编辑该值。

### 3. 创建任务

- 点击“新建任务”并选择项目目录，可让 Agent 使用文件、终端和 Git 能力。
- 在侧栏“任务”区域新建不绑定目录的任务，可进行不涉及本地项目的对话。
- 点击新任务页的预提示词只会填入输入框，确认内容后再手动发送。

更多界面与发布说明见 [Desktop 指南](/doc/desktop)。

## 方式三：从源码构建 Desktop 发布包

构建机器需要：

- Windows x64；
- .NET 10 SDK；
- Node.js 与 pnpm；
- Python 3；
- 可访问 Electron 二进制镜像的网络。

```powershell
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw
build.cmd
```

Windows 用户可以直接双击 `build.cmd`。它会启动 `build.py`，编译并测试最新 Runtime 与 Desktop，最终生成：

```text
publish\SeekClaw-win-x64\SeekClaw.exe
```

分发时必须复制整个 `SeekClaw-win-x64` 文件夹。

## 方式四：从源码运行 CLI

CLI 源码运行需要 .NET 10 SDK；Git 用于仓库感知和项目工具。

```bash
git clone https://github.com/umr-xiaomai/SeekClaw.git
cd SeekClaw
dotnet build

# 配置并测试 Provider
dotnet run --project seekclaw_cli -- provider add --id openai --kind openai --base-url "https://api.openai.com/v1" --api-key "sk-..." --model "gpt-5.5"
dotnet run --project seekclaw_cli -- provider test openai

# 进入交互模式
dotnet run --project seekclaw_cli
```

也可以直接执行单次任务：

```bash
dotnet run --project seekclaw_cli -- "分析当前项目的架构与依赖关系"
```

恢复会话或指定模型：

```bash
dotnet run --project seekclaw_cli -- --continue
dotnet run --project seekclaw_cli -- --resume <session-id>
dotnet run --project seekclaw_cli -- --model "anthropic/claude-sonnet-5" -- "检查并修复测试"
```

## 诊断

Desktop 用户可打开“设置 → 诊断与用量”。CLI 用户运行：

```bash
seekclaw doctor
```

源码运行 CLI 时使用：

```bash
dotnet run --project seekclaw_cli -- doctor
```

诊断会检查工作区、配置、Provider 连通性、Memory 和运行目录。如果请求失败，当前版本会保留 Provider 返回的 HTTP 状态与完整错误内容，便于定位模型协议或消息格式问题。
