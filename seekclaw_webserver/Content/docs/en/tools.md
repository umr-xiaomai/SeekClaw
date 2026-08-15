# Built-in Tools and Extensions

The Runtime currently registers nine built-in tools. File and command tools require a concrete workspace, while web tools remain available in directory-free global tasks.

## Built-in tools

| Tool | Purpose | Mutating | Requires workspace |
| --- | --- | --- | --- |
| `read_file` | Reads lines with optional `offset` and `limit` | no | yes |
| `write_file` | Creates or overwrites a complete file | yes | yes |
| `edit_file` | Replaces a unique `old_string` with `new_string` | yes | yes |
| `list_dir` | Lists a directory tree to a requested depth | no | yes |
| `glob` | Matches file paths and returns up to 200 recent entries | no | yes |
| `grep` | Searches file content with regex and optional Glob filtering | no | yes |
| `bash` | Runs a shell command in the workspace | yes | yes |
| `web_search` | Searches Google, Bing, or Baidu | no | no |
| `web_fetch` | Extracts text from an HTTP or HTTPS page | no | no |

Descriptions are loaded from `prompts/tool/<name>.txt`, while arguments are validated through JSON Schema. Output budgets adapt to the model context window and remain capped by `agent.maxToolOutputChars`.

## `edit_file` arguments

The current tool uses text matching rather than line-number patches:

```json
{
  "path": "src/UserService.cs",
  "old_string": "public bool IsActive => false;",
  "new_string": "public bool IsActive => status.IsActive;",
  "replace_all": false
}
```

`old_string` must match and is required to be unique by default. Add context when it matches multiple places or set `replace_all: true` explicitly. A successful edit publishes a unified diff event for Desktop and CLI renderers.

## Modes and scopes

- Global tasks filter tools whose `RequiresWorkspace` value is `true`.
- `plan` and `readonly` filter tools whose `Mutating` value is `true`.
- `edit` and `auto` allow mutations, which can trigger build verification.
- A workspace can disable tools by name through `disabledTools`.

## Custom C# tools

Implement `ITool` and register the instance with `IToolRegistry`:

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

The returned `IDisposable` unregisters the tool. MCP reload uses the same mechanism to clean up stale registrations.
