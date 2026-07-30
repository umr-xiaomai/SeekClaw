# 内置工具与扩展

Runtime 当前注册 9 个内置工具。文件和命令工具需要具体工作区；网络工具可用于不绑定目录的全局任务。

## 内置工具

| 工具 | 作用 | 修改状态 | 需要工作区 |
| --- | --- | --- | --- |
| `read_file` | 按行读取文件，支持 `offset` / `limit` | 否 | 是 |
| `write_file` | 创建或覆盖完整文件 | 是 | 是 |
| `edit_file` | 用唯一 `old_string` 替换为 `new_string` | 是 | 是 |
| `list_dir` | 按指定深度列出目录树 | 否 | 是 |
| `glob` | 匹配文件路径，最多返回最近的 200 项 | 否 | 是 |
| `grep` | 正则搜索文件内容，可按 Glob 过滤 | 否 | 是 |
| `bash` | 在工作区运行 Shell 命令 | 是 | 是 |
| `web_search` | 使用 Google、Bing 或百度搜索 | 否 | 否 |
| `web_fetch` | 提取 HTTP / HTTPS 页面正文 | 否 | 否 |

工具描述从 `prompts/tool/<name>.txt` 加载，可以随 Prompt 更新；参数由 JSON Schema 校验。工具输出预算会根据模型上下文窗口调整，并受 `agent.maxToolOutputChars` 上限保护。

## `edit_file` 参数

当前 `edit_file` 使用文本匹配，而不是行号 Patch：

```json
{
  "path": "src/UserService.cs",
  "old_string": "public bool IsActive => false;",
  "new_string": "public bool IsActive => status.IsActive;",
  "replace_all": false
}
```

`old_string` 必须匹配；默认要求只出现一次。多处匹配时，应增加上下文使其唯一，或明确设置 `replace_all: true`。修改完成后会发布统一 Diff 事件，供 Desktop 与 CLI 展示。

## 模式与作用域

- 全局任务过滤 `RequiresWorkspace: true` 的工具，仅保留网络等无工作区工具。
- `plan` 和 `readonly` 模式过滤所有 `Mutating: true` 工具。
- `edit` 与 `auto` 模式允许修改工具；修改成功后可触发构建验证。
- 工作区 `disabledTools` 可以按名称禁用工具。

## 自定义 C# Tool

自定义工具实现 `ITool` 并注册到 `IToolRegistry`：

```csharp
public sealed class ProjectSummaryTool : ITool
{
    public string Name => "project_summary";
    public string Description => "Summarize the current project";
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("depth", ToolSchema.Integer("Maximum directory depth"), false));
    public bool Mutating => false;
    public bool RequiresWorkspace => true;
    public string StatusLabel => "Inspecting project";

    public Task<ToolResult> ExecuteAsync(
        JsonObject arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var summary = $"Workspace: {context.Workspace.Root}";
        return Task.FromResult(ToolResult.Ok(summary));
    }
}

using var registration = toolRegistry.Register(new ProjectSummaryTool());
```

注册返回的 `IDisposable` 用于注销工具；MCP 重载也依赖这一机制清理旧注册。
