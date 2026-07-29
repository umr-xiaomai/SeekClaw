# Tools & Built-in System

SeekClaw equips AI agents with built-in tools (`read_file`, `write_file`, `edit_file`, `list_dir`, `glob`, `grep`, `bash`) and supports custom C# `ITool` implementations.

---

## Custom Tool Implementation

```csharp
public class MyCustomTool : ITool
{
    public string Name => "my_tool";
    public string Description => "Custom tool description";
    public bool Mutating => false;
    public string StatusLabel => "Executing custom tool...";
    public JsonElement ParameterSchema => /* Schema */;

    public async Task<ToolResult> ExecuteAsync(
        JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        return ToolResult.Success("Executed successfully");
    }
}
```
