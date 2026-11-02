# 会话交互组件

会话正文后的卡片由工具执行结果生成，绑定真实任务和文件。Agent 不需要生成特殊 Markdown、HTML 卡片标记或按钮指令。Desktop 等当前轮执行结束后，再将这一轮工具产生的组件汇总到最后一个 assistant 消息下；工具完成、模型继续思考或用户中途补充指令时，不会提前发布本轮卡片。历史轮次的卡片仍正常显示。同一资源重复产生卡片时，以本轮最新结果为准。

## 当前支持

| 类型 | Agent 入口 | 会话内操作 |
| --- | --- | --- |
| 定时任务 | `schedule` 的创建、修改、暂停、恢复、完成、删除 | 查看当前状态与下次执行时间，编辑指令/频率/通知，暂停/恢复，立即运行，展开最近结果 |
| HTML 预览 | `write_file` / `edit_file` 自动产生；也可调用 `present_artifact` | 打开右侧预览、桌面/手机宽度切换、重新读取文件、定位文件 |
| 成果文件 | `present_artifact` | 查看名称/说明，定位文件；保留原文件格式 |

例如用户说“每天早上九点继续检查这个项目”，Agent 调用 `schedule` 创建绑定当前会话的任务后，Desktop 会显示任务卡片。用户仍可直接说“改成晚上八点”，Agent 更新同一个任务 ID；卡片在挂载时和收到任务变化事件时读取最新状态。

HTML 示例：先创建 `artifacts/demo.html`，再调用 `present_artifact`：

```json
{"path":"artifacts/demo.html","title":"项目进展概览","description":"可交互的项目状态页面"}
```

如果文件由 `write_file` 或 `edit_file` 修改，HTML 卡片会自动出现，无需再调用工具。通过 shell 创建的文件使用 `present_artifact` 显式展示。普通工具输出仍保留原来的“查看结果”，有卡片的工具默认展示卡片以避免重复显示原始 JSON。

## 协议与持久化

`ToolResult.Widgets`、`ToolCallCompletedEvent.Widgets` 与 `ChatMessage.ToolWidgets` 使用独立的 `ConversationWidget` 类型，不复用 MCP 元数据。SQLite 消息 JSON 保留组件，现有数据库无需新增列。

实时 `tool_done.details.widgets` 与 `session.get.messages[].toolWidgets` 使用相同格式：

```json
{
  "kind": "artifact",
  "version": 1,
  "id": "artifact:/absolute/path/demo.html",
  "title": "项目进展概览",
  "path": "/absolute/path/demo.html",
  "executionRoot": "/absolute/path",
  "mediaType": "text/html"
}
```

`artifact.executionRoot` 是工具生成并持久化的可选字段，用于在 daemon 重启后保留全局会话的文件执行目录；客户端不需要传回该字段。`schedule` 类型携带任务快照 `task` 和可选的 `deleted`。资源 ID 用于合并卡片，工具调用 ID 仍用于关联原始工具结果。任务删除后卡片保留操作记录并禁用执行入口。

新增接口：

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `schedule.get` | `id` | 最新任务；已删除时返回 JSON `null` |
| `artifact.read` | `workspace` 或 `global`、`sessionId`、`widgetId` | `{html,path}` |

任务状态变化通过 `schedule.changed` 广播，运行结果继续使用 `schedule.updated`。独立轮次运行时的变更事件会转发到 daemon 共享事件总线，其他已打开的卡片也能刷新。

## 预览边界

`artifact.read` 从指定会话的已保存组件查找文件，不接受调用方任意指定的路径或执行目录。项目会话始终使用当前工作区或任务 worktree 的目录边界，忽略组件中其他执行目录。全局会话的配置目录不等于文件执行目录：优先使用工具记录的 `executionRoot`，没有该字段的旧卡片则沿用全局文件工具的 daemon 工作目录规则。路径经过执行目录的遍历/链接检查，读取上限为 2 MiB，并在每次刷新时重新读取磁盘文件。

预览以 `srcdoc` 加载，iframe 仅启用 `allow-scripts`，没有同源、弹窗、下载、表单提交或父窗口导航权限。CSP 禁止网络请求、外部脚本和嵌套 iframe；Electron 额外阻止子框架跳转到外部页面。本地相对资源也不加载，因此页面应内嵌样式、脚本与资源。适合单文件原型和演示，不替代完整开发服务器。

## 扩展新组件

1. 在 `Presentation/ConversationWidget.cs` 定义数据类型、类型标识及版本；仅传声明式数据，不传任意可执行动作。
2. 工具完成真实操作后将组件附加到 `ToolResult.Widgets`；共用已有事件和历史保存链路。
3. 在前端协议校验及 `ConversationWidgets.vue` 注册组件，按钮只能调用客户端明确允许的接口。
4. 覆盖数据保存/恢复、实时映射、交互错误与响应式布局。前端忽略未知类型/版本；普通工具结果作为兼容回退。

卡片沿用主题变量与紧凑的中性色层次，默认显示摘要和关键操作，编辑/历史按需展开。预览使用原生 modal dialog，支持 Escape、焦点约束与关闭后焦点返回；窄窗口自适应，减少动态效果偏好也受支持。
