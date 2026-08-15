# 常见问题与排错

## Desktop

### Desktop 用户需要安装 .NET 吗？

不需要。Windows x64 发布文件夹包含自包含 Runtime。请保留整个 `SeekClaw-win-x64` 目录并运行顶层的 `SeekClaw.exe`。只有从源码构建或直接运行 CLI 的开发者需要 .NET 10 SDK。

### 为什么 Desktop 显示 Runtime 离线？

按顺序检查：

1. `resources\runtime\seekclaw.exe` 是否存在；
2. 发布目录是否被拆散，或安全软件是否隔离了 Runtime；
3. 是否有不兼容的旧 Daemon 占用 `\\.\pipe\seekclaw`；
4. 在“设置 → 诊断与用量”重新检查并查看完整错误。

Desktop 会自动尝试连接和重连，不需要手动运行 Runtime。从源码开发时，可先执行根目录 `build.cmd` 生成完整发布包。

### 为什么 API Key 输入框为空？

- 如果配置中直接保存了 `apiKey`，Desktop 会读取并显示真实内容。
- API Key 只从 `~/.seekclaw/config.json` 的 `apiKey` 字段读取，不支持通过环境变量注入。

如果显式保存的 Key 在一次对话后消失，请先确认启动的 Desktop 与 Daemon 使用同一用户账户和 `~/.seekclaw/config.json`，再打开诊断页查看活动 Profile 与 Provider。不要同时运行会改写同一配置的旧版本 Runtime。

### 项目和任务是什么关系？

一个项目可以没有任务，也可以有多个持久化任务。Session 只在第一次发送消息时创建。不绑定项目的任务没有固定项目目录，不显示项目路径，但仍可使用本地文件、终端和 Git 工具。

## Provider 与模型请求

### 出现网络超时、401 或 404 怎么办？

1. 在“模型与 Provider”中确认协议类型、Base URL、模型 ID 和 Key。
2. 点击 Provider 或模型旁的“测试”。
3. 检查代理设置与环境变量是否对 Desktop 启动的 Runtime 可见。
4. 打开“诊断与用量”查看具体 Provider 检查结果。

当前版本会输出 Provider 名称、HTTP 状态码和响应正文。例如 `DeepSeek returned HTTP 400: ...` 后面的服务端信息会完整保留。

### `tool_use ids were found without tool_result blocks immediately after` 是什么？

这表示发送给模型的消息历史中，某个工具调用后没有紧邻对应的工具结果。常见原因是旧 Session 中断、切换到消息规则不同的兼容接口，或早期版本保存了不完整的工具回合。

建议先升级到最新 Runtime，再新建任务复现。若只有旧任务失败，可以保留原任务用于排查并在新任务继续工作；若新任务也失败，请记录完整 HTTP 错误、Provider 协议类型、模型 ID 与 Session ID。

### 如何保护代码隐私？

使用 Ollama 或 LM Studio 并选择 `offline` 路由时，模型请求可以完全留在本机。使用云 Provider 时，相关提示词与工具结果会发送给所选 API 服务商；SeekClaw 不会额外把代码上传到自己的服务器。

## 构建与发布

### 如何一键构建 Desktop 和 Runtime？

在 Windows 根目录双击 `build.cmd`。它自动查找 `py.exe` 或 `python.exe` 并运行 `build.py`。构建成功后启动：

```text
publish\SeekClaw-win-x64\SeekClaw.exe
```

分发时复制整个 `SeekClaw-win-x64` 文件夹。

### Electron 打包下载失败怎么办？

构建脚本会设置 Electron 与 electron-builder 镜像，并最多重试三次。若仍失败：

1. 确认网络或代理可以访问 `npmmirror.com`；
2. 重新运行 `build.cmd`，已下载的依赖会复用缓存；
3. 确认系统时间和 TLS 证书正常；
4. 如需自定义镜像，在运行前设置 `ELECTRON_MIRROR` 与 `ELECTRON_BUILDER_BINARIES_MIRROR`。

`build.cmd --skip-tests` 可以跳过测试，但不会跳过 Runtime 与 Desktop 编译。

## 诊断命令

Desktop 使用“设置 → 诊断与用量”；CLI 使用：

```bash
seekclaw doctor
```

诊断覆盖工作区、元数据目录、Provider 配置、Memory 和 Provider 连通性。Provider 的 401/404 检查结果会保留在详情中，不会被简单吞掉。

## 发布说明 {#release-notes}

### Desktop 0.1.0

- 新增 Electron / Vue Windows Desktop；
- 支持项目与无目录任务、会话持久化和归档；
- 新增预提示词、模型与模式切换；
- 新增 Provider / API Key、MCP、Skills、诊断与用量管理；
- 集成项目终端、Git 变更与历史；
- 发布包内置自包含 Runtime，并由 Desktop 自动管理其生命周期；
- 模型请求显示完整 Provider / HTTP 错误。

Runtime IPC 协议版本为 `2.1`。详细方法见 [Daemon 文档](/doc/daemon)。
