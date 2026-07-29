# 工具生态系统与内置工具 (Tools)

SeekClaw 给 AI Agent 赋予了与真实操作系统、文件系统和编译工具交互的能力。本文详细介绍内置工具的种类、执行安全控制以及如何开发自定义 C# 原生 `ITool` 工具。

---

## 内置核心工具矩阵

SeekClaw 运行时自带 7 大高频编码与探针工具：

| 工具名称 | 作用描述 | 变动性 (`Mutating`) | 状态标签 (`StatusLabel`) |
| --- | --- | --- | --- |
| `read_file` | 读取指定文件的完整内容或特定行号区间 | 否 (`false`) | *"正在读取文件..."* |
| `write_file` | 创建新文件或覆盖写入已有文件 | 是 (`true`) | *"正在写入文件..."* |
| `edit_file` | 对现存文件进行精准行替换或 Patch 修改 | 是 (`true`) | *"正在编辑代码块..."* |
| `list_dir` | 列出指定目录下的文件与子目录列表 | 否 (`false`) | *"正在检索目录..."* |
| `glob` | 使用 Glob 模式匹配文件（如 `**/*.cs`） | 否 (`false`) | *"正在搜寻文件匹配..."* |
| `grep` | 基于 Ripgrep / 正则表达式高效搜索文本 | 否 (`false`) | *"正在全文检索代码..."* |
| `bash` | 在当前工作区执行原生系统 Shell 命令 | 视命令而定 (`true`) | *"正在执行终端命令..."* |

---

## 工具入参 JSON Schema 规范

以 `edit_file` 为例，工具入参包含强类型约束，确保 LLM 输出的准确性：

```json
{
  "name": "edit_file",
  "description": "修改现有文件的内容区块",
  "parameters": {
    "type": "object",
    "properties": {
      "path": { "type": "string", "description": "相对或绝对文件路径" },
      "startLine": { "type": "integer", "description": "起始替换行号 (1-indexed)" },
      "endLine": { "type": "integer", "description": "结束替换行号 (1-indexed)" },
      "replacementContent": { "type": "string", "description": "替换后的新代码文本" }
    },
    "required": ["path", "startLine", "endLine", "replacementContent"]
  }
}
```

---

## 自定义 C# 原生 Tool 开发

通过实现 `ITool` 接口，开发者可轻松为 `seekclaw_runtime` 增加自定义业务工具：

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.CustomTools;

public class SqlQueryTool : ITool
{
    public string Name => "sql_query";
    public string Description => "在开发数据库中执行只读 SQL 查询";
    
    // 标记该工具是否更改外部状态
    public bool Mutating => false;
    
    // 终端 UI 显示的状态文本
    public string StatusLabel => "正在查询 SQL 数据库...";

    // 工具入参 JSON Schema 声明
    public JsonElement ParameterSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "要执行的 SELECT 语句" }
        },
        required = new[] { "query" }
    });

    public async Task<ToolResult> ExecuteAsync(
        JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        string sql = args["query"]?.GetValue<string>() ?? "";
        
        // 校验只读原则
        if (sql.Contains("DROP", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Error("安全规则拦截：不允许执行破坏性 SQL 语句");
        }

        // 执行查询逻辑...
        string data = await ExecuteQueryAsync(sql, ct);
        return ToolResult.Success(data);
    }
}
```

### 注册工具：
```csharp
// 在 SeekClawRuntime 初始化时挂载
toolRegistry.Register(new SqlQueryTool());
```
