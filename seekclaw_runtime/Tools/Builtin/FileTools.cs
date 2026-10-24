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
    // Virtual (not interface defaults): the interface map is fixed at this base
    // class, so derived tools must override these to be visible to ITool callers.
    public virtual bool RequiresWorkspace => true;
    public virtual bool RequiresNetwork => false;
    public virtual bool RequiresVision => false;
    public virtual string StatusLabel => "Working";

    /// <summary>Tool descriptions live in prompts/tool/&lt;name&gt;.txt (hot-reloadable).</summary>
    public string Description => prompts.TryGet($"tool/{Name}") ?? $"The {Name} tool.";

    /// <summary>How long a mutating tool waits for the central file write lock before failing.</summary>
    protected static readonly TimeSpan FileWriteLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a mutating tool body under the central file write lock when a coordinator is
    /// present (daemon / concurrent turns). The mutation itself re-reads the file inside
    /// the lock, so edits always apply to the latest on-disk content. Without a
    /// coordinator (single-turn CLI) the body runs directly.
    /// </summary>
    protected static async Task<ToolResult> WithFileWriteLockAsync(
        ToolContext context,
        string path,
        Func<CancellationToken, Task<ToolResult>> mutation,
        CancellationToken ct)
    {
        var coordinator = context.Coordinator;
        if (coordinator is null)
            return await mutation(ct).ConfigureAwait(false);

        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        var acquired = await coordinator.TryAcquireAsync(
            context.Workspace.Root, path, context.Owner, FileWriteLockTimeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            var holder = coordinator.GetOwner(context.Workspace.Root, path);
            return ToolResult.Fail(
                $"Write lock timeout: {relative} is being edited by another task" +
                (holder is null ? "" : $" ({holder})") +
                ". Wait for it to finish, re-read the latest file content, then retry.");
        }
        try
        {
            return await mutation(ct).ConfigureAwait(false);
        }
        finally
        {
            coordinator.Release(context.Workspace.Root, path, context.Owner);
        }
    }

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

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        var content = GetString(arguments, "content") ?? "";
        return WithFileWriteLockAsync(context, path, _ => WriteCoreAsync(path, content, context, ct), ct);
    }

    private static async Task<ToolResult> WriteCoreAsync(
        string path, string content, ToolContext context, CancellationToken ct)
    {
        var existed = File.Exists(path);
        var oldFile = existed
            ? await TextFileIO.ReadAsync(path, ct).ConfigureAwait(false)
            : DecodedTextFile.EmptyUtf8;
        var oldText = oldFile.Text;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await TextFileIO.WriteAtomicAsync(path, content, oldFile.Encoding, oldFile.Preamble, ct)
            .ConfigureAwait(false);

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

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"File not found: {path}"));

        var oldString = GetString(arguments, "old_string") ?? "";
        var newString = GetString(arguments, "new_string") ?? "";
        if (oldString.Length == 0)
            return Task.FromResult(ToolResult.Fail("old_string must not be empty."));
        if (oldString == newString)
            return Task.FromResult(ToolResult.Fail("old_string and new_string are identical."));

        var replaceAll = GetBool(arguments, "replace_all");
        return WithFileWriteLockAsync(context, path, _ => EditCoreAsync(path, oldString, newString, replaceAll, context, ct), ct);
    }

    private static async Task<ToolResult> EditCoreAsync(
        string path, string oldString, string newString, bool replaceAll, ToolContext context, CancellationToken ct)
    {
        var file = await TextFileIO.ReadAsync(path, ct).ConfigureAwait(false);
        var text = file.Text;
        var newline = TextFileIO.DetectDominantNewline(text);
        var normalizedText = TextFileIO.NormalizeNewlines(text);
        var normalizedOld = TextFileIO.NormalizeNewlines(oldString);
        var normalizedNew = TextFileIO.NormalizeNewlines(newString);
        var occurrences = CountOccurrences(normalizedText, normalizedOld);
        if (occurrences == 0)
            return ToolResult.Fail($"old_string not found in {path}. Read the file again — the content may have changed.");

        if (occurrences > 1 && !replaceAll)
            return ToolResult.Fail(
                $"old_string matches {occurrences} locations in {path}. Provide more surrounding context to make it unique, or set replace_all.");

        var updatedNormalized = replaceAll
            ? normalizedText.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
            : ReplaceFirst(normalizedText, normalizedOld, normalizedNew);
        var updated = TextFileIO.RestoreNewlines(updatedNormalized, newline);
        await TextFileIO.WriteAtomicAsync(path, updated, file.Encoding, file.Preamble, ct)
            .ConfigureAwait(false);

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

internal sealed record DecodedTextFile(string Text, Encoding Encoding, byte[] Preamble)
{
    public static DecodedTextFile EmptyUtf8 { get; } = new("", new UTF8Encoding(false), []);
}

/// <summary>
/// Encoding-preserving text I/O for mutating file tools. Writes go to a sibling
/// temporary file and are atomically renamed over the destination, so a crash or
/// cancellation cannot leave a half-written source file.
/// </summary>
internal static class TextFileIO
{
    public static async Task<DecodedTextFile> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new DecodedTextFile(text, encoding, bytes[..preambleLength]);
    }

    public static async Task WriteAtomicAsync(
        string path, string text, Encoding encoding, byte[] preamble, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new IOException($"Cannot determine the parent directory of '{path}'.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            }))
            {
                if (preamble.Length > 0)
                    await stream.WriteAsync(preamble, ct).ConfigureAwait(false);

                var body = encoding.GetBytes(text);
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                try { File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string DetectDominantNewline(string text)
    {
        var crlf = 0;
        var bareLf = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            if (index > 0 && text[index - 1] == '\r') crlf++;
            else bareLf++;
        }
        return crlf > bareLf ? "\r\n" : "\n";
    }

    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string RestoreNewlines(string text, string newline) =>
        newline == "\n" ? text : text.Replace("\n", newline, StringComparison.Ordinal);

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }
}
