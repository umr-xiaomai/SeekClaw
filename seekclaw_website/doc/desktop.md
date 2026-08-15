# Desktop 桌面端

SeekClaw Desktop 是基于 Electron 与 Vue 的桌面客户端。它通过本地 Daemon IPC 使用同一套 .NET Runtime，在图形界面中提供项目、任务、模型、工具和运行状态管理。CLI 仍然受支持，两种前端共享相同的 Provider、Profile、MCP、Skill 与会话数据。

![SeekClaw Desktop 的 AI 对话与项目管理](/screenshots/desktop/chat-and-projects.png)

## 获取与启动

### 使用已发布版本

<LatestRelease />

当前 Desktop 发布目标为 **Windows x64**。下载或接收发布包后，请保留整个 `SeekClaw-win-x64` 文件夹并运行其中的 `SeekClaw.exe`，不要只复制 EXE。

发布包已经包含自包含的 Runtime：

```text
SeekClaw-win-x64/
├── SeekClaw.exe
└── resources/
    └── runtime/
        └── seekclaw.exe
```

终端用户不需要另行安装 .NET。Desktop 启动时会先连接已经运行的 Daemon；如果未找到，则自动启动随包附带的 Runtime。退出 Desktop 时，只会关闭由本次 Desktop 启动的 Runtime，不会终止用户自行启动的 Daemon。

### 从源码构建发布包

源码构建需要 Windows x64、.NET 10 SDK、Node.js、pnpm 与 Python 3。克隆仓库后，直接双击根目录的 `build.cmd`：

```text
build.cmd
```

`build.cmd` 会调用跨平台构建入口 `build.py`，依次安装依赖、运行 .NET 与 Desktop 测试、发布自包含 Runtime、构建 Electron 应用，并把最终文件放到：

```text
publish\SeekClaw-win-x64\SeekClaw.exe
```

需要从终端跳过测试或依赖安装时，可以使用：

```powershell
build.cmd --skip-tests
build.cmd --skip-install
```

请分发整个 `publish\SeekClaw-win-x64` 文件夹。打包阶段需要下载 Electron 二进制；脚本会自动使用镜像并对临时网络错误重试。

## 项目任务与不绑定项目的任务

Desktop 支持两类任务：

- **项目任务**绑定一个本地目录，可以读取和修改文件、打开项目终端、查看 Git 变更与提交历史。
- **不绑定项目的任务**没有固定工作目录，适合通用问答；它仍可使用本地文件、终端和 Git 工具，只是不显示项目级工具入口。

新建任务时不必立即创建 Runtime Session。只有第一次发送消息后，Desktop 才会创建并持久化会话。侧栏中的“任务”区域显示不绑定项目的任务；项目任务仍保留在各自项目节点下，可展开查看。

任务标题会根据首条提示词生成。你可以归档、恢复或删除任务，也可以批量处理全部任务或某个项目内的任务。已归档任务为只读状态。删除项目会永久删除该项目下的全部会话（包括已归档会话），但不会删除本地项目文件。

## 发起一次对话

1. 点击“新建任务”，选择一个项目，或使用不绑定目录的任务。
2. 在输入框选择 Agent 模式和 `provider/model`。
3. 输入要求并发送；也可以点击新任务页的预提示词卡片。卡片只会把文字填入输入框，**不会自动发送**。
4. 查看流式文本、思考状态、工具调用和完整错误信息。

支持视觉的模型会在输入区显示图片按钮，可一次选择多张 PNG、JPEG、WebP 或 GIF；Windows 截图后也可以在输入框直接按 `Ctrl+V` 添加剪贴板图片。发送前可以预览或逐张移除；发送后图片随 Session 保存。模型开始处理图片时，助手消息会显示“已查看”、对应文件名和缩略图，点击可再次预览。当前模型没有声明 `vision` 能力时，图片按钮会自动禁用。

AI 正在输出时仍可继续输入。点击“发送”会把消息加入发送队列，本轮结束后按顺序自动发送；队列支持叠加和删除。点击队列消息的“引导”会立即把它作为附加指导交给当前 turn，不取消正在进行的 AI 请求。

项目任务顶部会显示完整工作区目录，并提供打开位置、终端、Git 变更、Git 历史和任务设置入口。不绑定项目的任务不会显示项目目录或项目工具。

## 模型、Provider 与 API Key

打开“设置 → 模型与 Provider”可以：

- 新建、编辑、测试、启用或删除 Provider；
- 直接查看和修改显式保存在配置中的 API Key；
- 直接在配置文件中保存和管理 API Key；
- 管理模型列表、Base URL、代理、超时和优先级；
- 创建 Profile，并切换活动模型或路由策略。

![SeekClaw Desktop 的模型与 Provider 管理](/screenshots/desktop/providers-and-models.png)

::: tip API Key 的显示规则
API Key 直接保存在 `~/.seekclaw/config.json` 中，由 Runtime 和 Desktop 读取、显示和编辑。Runtime 不会从环境变量读取 API Key。
:::

保存后可立即点击“测试”。模型请求失败时，Desktop 会展示 Provider 名称、HTTP 状态码和服务端返回的完整错误信息，而不只显示笼统的 `LLM request failed`。

## MCP、Skills、诊断与用量

设置中心还提供：

- **MCP**：配置工作区或全局的 stdio / SSE Server，保存后重新加载工具；
- **Skills**：查看并启用或禁用已发现的技能；
- **诊断与用量**：检查工作区、配置和 Provider 健康状态，汇总调用、Token、延迟与成本。

![在 Desktop 中配置 MCP Server](/screenshots/desktop/mcp-servers.png)

![Desktop Runtime 诊断与用量](/screenshots/desktop/diagnostics-and-usage.png)

## Runtime 连接行为

Desktop 会在启动时自动建立连接，并在连接丢失时进行有限次数的重连。如果重连仍失败，界面会展示具体错误并允许继续重试或退出。

常见检查项：

1. 确认发布目录中存在 `resources\runtime\seekclaw.exe`。
2. 确认没有损坏或不兼容的旧 Daemon 占用 `\\.\pipe\seekclaw`。
3. 在设置的“诊断与用量”中重新检查 Runtime 和 Provider。
4. 源码运行时，先执行 `build.cmd` 生成 Desktop 所需的 Runtime。

更底层的连接方法和协议字段见 [Daemon 与 IPC 协议](/doc/daemon)，其他问题见 [常见问题与排错](/doc/faq)。
