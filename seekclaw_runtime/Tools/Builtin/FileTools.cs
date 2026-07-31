using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

public abstract class BuiltinTool(IPromptProvider prompts) : ITool
{
    public abstract string Name { get; }
    public abstract JsonObject ParameterSchema { get; }
    public virtual bool Mutating => false;
    public virtual string StatusLabel => "Working";

    /// <summary>Tool descriptions live in prompts/tool/&lt;name&gt;.txt (hot-reloadable).</summary>
    public string Description => prompts.TryGet($"tool/{Name}") ?? $"The {Name} tool.";

    public abstract Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct);

    protected static string? GetString(JsonObject args, string name) =>
        args[name] is { } node ? node.GetValue<string>() : null;

    protected static int? GetInt(JsonObject args, string name) =>
        args[name] is { } node ? (int)node.GetValue<double>() : null;

    protected static bool GetBool(JsonObject args, string name) =>
        args[name] is { } node && node.GetValue<bool>();
}

public sealed class ReadFileTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "read_file";
    public override string StatusLabel => "Reading files";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("File path (absolute, or relative to the workspace root)"), true),
        ("offset", ToolSchema.Integer("1-based line number to start reading from"), false),
        ("limit", ToolSchema.Integer("Maximum number of lines to read"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"File not found: {path}"));

        var offset = Math.Max(1, GetInt(arguments, "offset") ?? 1);
        var limit = Math.Max(1, GetInt(arguments, "limit") ?? 2000);

        var lines = File.ReadLines(path).Skip(offset - 1).Take(limit).ToList();
        var sb = new StringBuilder();

        // Send file content in chunks with progress events
        const int chunkSize = 50; // lines per chunk
        var chunks = (lines.Count + chunkSize - 1) / chunkSize;

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var start = chunk * chunkSize;
            var end = Math.Min(start + chunkSize, lines.Count);

            var chunkSb = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                var line = lines[i].Length > 2000 ? lines[i][..2000] + "…" : lines[i];
                chunkSb.Append(offset + i).Append('\t').AppendLine(line);
            }

            sb.Append(chunkSb);

            // Send progress event for UI display
            if (chunks > 1)
                context.Events.Publish(new ToolCallProgressEvent(
                    context.CallId,
                    $"[{end}/{lines.Count}] Reading…\n{chunkSb}"));
            else if (chunks == 1)
                context.Events.Publish(new ToolCallProgressEvent(context.CallId, chunkSb.ToString()));
        }

        var output = sb.Length == 0 ? "(empty file)" : context.Truncate(sb.ToString(), "file");
        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        return Task.FromResult(ToolResult.Ok(output, $"Read {lines.Count} lines from {relative}") with { FilePath = path });
    }
}

public sealed class WriteFileTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "write_file";
    public override bool Mutating => true;
    public override string StatusLabel => "Editing";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("File path to create or overwrite"), true),
        ("content", ToolSchema.String("Full new file content"), true));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        var content = GetString(arguments, "content") ?? "";

        var existed = File.Exists(path);
        var oldText = existed ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);

        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        var lineCount = content.Count(c => c == '\n') + 1;
        var diff = DiffUtil.Unified(oldText, content, relative);
        if (diff.Length > 0)
            context.Events.Publish(new FileDiffEvent(context.CallId, relative, diff));

        return ToolResult.Ok(
            existed ? $"Overwrote {relative}" : $"Created {relative}",
            $"{(existed ? "Updated" : "Created")} {relative} ({lineCount} lines)") with
        { Diff = diff, FilePath = path };
    }
}

public sealed class EditFileTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "edit_file";
    public override bool Mutating => true;
    public override string StatusLabel => "Editing";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("File path to edit"), true),
        ("old_string", ToolSchema.String("Exact text to replace (must match uniquely unless replace_all)"), true),
        ("new_string", ToolSchema.String("Replacement text"), true),
        ("replace_all", ToolSchema.Boolean("Replace every occurrence instead of requiring a unique match"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {path}");

        var oldString = GetString(arguments, "old_string") ?? "";
        var newString = GetString(arguments, "new_string") ?? "";
        if (oldString.Length == 0)
            return ToolResult.Fail("old_string must not be empty.");
        if (oldString == newString)
            return ToolResult.Fail("old_string and new_string are identical.");

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var occurrences = CountOccurrences(text, oldString);
        if (occurrences == 0)
            return ToolResult.Fail($"old_string not found in {path}. Read the file again — the content may have changed.");

        var replaceAll = GetBool(arguments, "replace_all");
        if (occurrences > 1 && !replaceAll)
            return ToolResult.Fail(
                $"old_string matches {occurrences} locations in {path}. Provide more surrounding context to make it unique, or set replace_all.");

        var updated = replaceAll
            ? text.Replace(oldString, newString)
            : ReplaceFirst(text, oldString, newString);
        await File.WriteAllTextAsync(path, updated, ct).ConfigureAwait(false);

        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        var diff = DiffUtil.Unified(text, updated, relative);
        if (diff.Length > 0)
            context.Events.Publish(new FileDiffEvent(context.CallId, relative, diff));

        var summary = replaceAll && occurrences > 1
            ? $"Replaced {occurrences} occurrences in {relative}"
            : $"Edited {relative}";
        return ToolResult.Ok(summary, summary) with { Diff = diff, FilePath = path };
    }

    internal static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    internal static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }
}
