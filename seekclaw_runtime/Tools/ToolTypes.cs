using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Tools;

public sealed class ToolContext
{
    public required WorkspaceInfo Workspace { get; init; }
    public required IEventBus Events { get; init; }
    public required AgentConfig Agent { get; init; }
    /// <summary>Adaptive per-call output budget (characters), derived from the model context window.</summary>
    public int MaxOutputChars { get; init; } = 30_000;
    public string CallId { get; init; } = "";
    /// <summary>Centralized write-lock coordinator shared by all concurrent turns.</summary>
    public IFileLockCoordinator? Coordinator { get; init; }
    /// <summary>Task identity used when acquiring file write locks.</summary>
    public string Owner { get; init; } = "";

    public string ResolvePath(string path)
    {
        var root = Workspace.IsGlobal ? Directory.GetCurrentDirectory() : Workspace.Root;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    public string Truncate(string text, string label = "output")
    {
        if (text.Length <= MaxOutputChars) return text;
        return text[..MaxOutputChars] + $"\n… [{label} truncated: {text.Length - MaxOutputChars} characters omitted]";
    }
}

public sealed record ToolResult
{
    public required bool Success { get; init; }
    /// <summary>Full text returned to the model.</summary>
    public required string Output { get; init; }
    /// <summary>One-line summary for the terminal renderer.</summary>
    public string? Summary { get; init; }
    public string? Diff { get; init; }
    public string? FilePath { get; init; }

    public static ToolResult Ok(string output, string? summary = null) =>
        new() { Success = true, Output = output, Summary = summary };

    public static ToolResult Fail(string error) =>
        new() { Success = false, Output = error, Summary = error.Split('\n')[0] };
}

public interface ITool
{
    string Name { get; }
    /// <summary>Loaded from prompts/tool/&lt;name&gt;.txt — never hard-coded.</summary>
    string Description { get; }
    JsonObject ParameterSchema { get; }
    /// <summary>True when the tool changes files (triggers the auto build/verify cycle).</summary>
    bool Mutating { get; }
    /// <summary>True when the tool needs a concrete local project directory.</summary>
    bool RequiresWorkspace => true;
    /// <summary>True when the tool needs outbound network access (web search / web fetch).</summary>
    bool RequiresNetwork => false;
    /// <summary>Status label for the renderer while the tool runs (Reading files, Searching…).</summary>
    string StatusLabel { get; }

    Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct);
}

public interface IToolRegistry
{
    IDisposable Register(ITool tool);
    ITool? Resolve(string name);
    IReadOnlyList<ITool> All { get; }
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Lock _gate = new();
    private readonly List<ITool> _tools = [];

    public IReadOnlyList<ITool> All
    {
        get { lock (_gate) return [.. _tools]; }
    }

    public IDisposable Register(ITool tool)
    {
        lock (_gate)
        {
            if (_tools.Any(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Tool '{tool.Name}' is already registered.");
            _tools.Add(tool);
        }
        return new Registration(this, tool);
    }

    public ITool? Resolve(string name)
    {
        lock (_gate)
            return _tools.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void Remove(ITool tool)
    {
        lock (_gate) _tools.Remove(tool);
    }

    private sealed class Registration(ToolRegistry owner, ITool tool) : IDisposable
    {
        public void Dispose() => owner.Remove(tool);
    }
}

/// <summary>Fluent JSON Schema builder for tool parameters.</summary>
public static class ToolSchema
{
    public static JsonObject Object(params (string Name, JsonObject Property, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, property, isRequired) in properties)
        {
            props[name] = property;
            if (isRequired) required.Add((JsonNode)name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
        };
    }

    public static JsonObject String(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Integer(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Boolean(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };
}
