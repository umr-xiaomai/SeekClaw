using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using SeekClaw.Runtime.Prompts;

namespace SeekClaw.Runtime.Tools.Builtin;

public sealed class ListDirTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "list_dir";
    public override string StatusLabel => "Reading files";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("Directory to list (defaults to the workspace root)"), false),
        ("depth", ToolSchema.Integer("Recursion depth, default 2"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? ".");
        if (!Directory.Exists(path))
            return Task.FromResult(ToolResult.Fail($"Directory not found: {path}"));

        var depth = Math.Clamp(GetInt(arguments, "depth") ?? 2, 1, 6);
        var sb = new StringBuilder();
        var entries = 0;
        AppendTree(path, "", depth, sb, ref entries);

        var output = entries == 0 ? "(empty directory)" : sb.ToString();
        return Task.FromResult(ToolResult.Ok(
            context.Truncate(output, "listing"),
            $"Listed {entries} entries under {Path.GetRelativePath(context.Workspace.Root, path)}"));
    }

    private static void AppendTree(string dir, string indent, int depth, StringBuilder sb, ref int entries)
    {
        if (depth == 0 || entries > 500) return;

        IEnumerable<string> subdirs, files;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException) { return; }

        foreach (var subdir in subdirs)
        {
            var name = Path.GetFileName(subdir);
            if (FileWalker.IgnoredDirectories.Contains(name)) continue;
            if (++entries > 500) return;
            sb.Append(indent).Append(name).AppendLine("/");
            AppendTree(subdir, indent + "  ", depth - 1, sb, ref entries);
        }

        foreach (var file in files)
        {
            if (++entries > 500) return;
            sb.Append(indent).AppendLine(Path.GetFileName(file));
        }
    }
}

public sealed class GlobTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "glob";
    public override string StatusLabel => "Searching";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("pattern", ToolSchema.String("Glob pattern, e.g. **/*.cs or src/**/*.ts"), true),
        ("path", ToolSchema.String("Directory to search from (defaults to the workspace root)"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var pattern = GetString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolResult.Fail("pattern is required."));

        var root = context.ResolvePath(GetString(arguments, "path") ?? ".");
        if (!Directory.Exists(root))
            return Task.FromResult(ToolResult.Fail($"Directory not found: {root}"));

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase).AddInclude(pattern);
        var matches = FileWalker.EnumerateFiles(root)
            .Where(f => matcher.Match(root, f).HasMatches)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(200)
            .Select(f => Path.GetRelativePath(context.Workspace.Root, f.FullName))
            .ToList();

        var output = matches.Count == 0 ? "No files matched." : string.Join('\n', matches);
        return Task.FromResult(ToolResult.Ok(
            context.Truncate(output, "matches"),
            $"{matches.Count} files matched {pattern}"));
    }
}

public sealed class GrepTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "grep";
    public override string StatusLabel => "Searching";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("pattern", ToolSchema.String("Regular expression to search for"), true),
        ("path", ToolSchema.String("Directory or file to search (defaults to the workspace root)"), false),
        ("glob", ToolSchema.String("Filter files by glob pattern, e.g. *.cs"), false),
        ("max_results", ToolSchema.Integer("Maximum matching lines to return, default 100"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var patternText = GetString(arguments, "pattern");
        if (string.IsNullOrWhiteSpace(patternText))
            return Task.FromResult(ToolResult.Fail("pattern is required."));

        Regex pattern;
        try
        {
            pattern = new Regex(patternText, RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail($"Invalid regex: {ex.Message}"));
        }

        var target = context.ResolvePath(GetString(arguments, "path") ?? ".");
        var maxResults = Math.Clamp(GetInt(arguments, "max_results") ?? 100, 1, 1000);
        var globPattern = GetString(arguments, "glob");
        Matcher? matcher = null;
        if (!string.IsNullOrWhiteSpace(globPattern))
            matcher = new Matcher(StringComparison.OrdinalIgnoreCase).AddInclude(
                globPattern.Contains('/') ? globPattern : $"**/{globPattern}");

        var files = File.Exists(target)
            ? [target]
            : Directory.Exists(target)
                ? FileWalker.EnumerateFiles(target)
                : Enumerable.Empty<string>();

        var sb = new StringBuilder();
        var hits = 0;
        var searchRoot = File.Exists(target) ? Path.GetDirectoryName(target)! : target;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (matcher is not null && !matcher.Match(searchRoot, file).HasMatches) continue;
            if (FileWalker.IsProbablyBinary(file)) continue;

            var lineNumber = 0;
            IEnumerable<string> lines;
            try { lines = File.ReadLines(file); }
            catch (IOException) { continue; }

            foreach (var line in lines)
            {
                lineNumber++;
                bool isMatch;
                try { isMatch = pattern.IsMatch(line); }
                catch (RegexMatchTimeoutException) { break; }
                if (!isMatch) continue;

                var display = line.Length > 400 ? line[..400] + "…" : line;
                sb.Append(Path.GetRelativePath(context.Workspace.Root, file))
                  .Append(':').Append(lineNumber).Append(": ").AppendLine(display.TrimEnd());
                if (++hits >= maxResults) goto Done;
            }
        }

        Done:
        var output = hits == 0 ? "No matches found." : sb.ToString();
        return Task.FromResult(ToolResult.Ok(context.Truncate(output, "matches"), $"{hits} matches for /{patternText}/"));
    }
}
