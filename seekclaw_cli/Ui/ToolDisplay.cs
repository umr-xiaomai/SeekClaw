using System.Globalization;
using System.Text.Json;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli.Ui;

internal static class ToolDisplay
{
    internal enum ActivityKind { Tool, Command, LegacyCommand, Exploration, TerminalInteraction }

    internal readonly record struct Presentation(string RunningLabel, string CompletedLabel, string Target,
        ActivityKind Kind = ActivityKind.Tool)
    {
        public bool IsTerminalInteraction => Kind == ActivityKind.TerminalInteraction;
        public bool IsExploration => Kind == ActivityKind.Exploration;
    }

    public static Presentation Describe(ToolCallStartedEvent tool)
    {
        using var document = ParseObject(tool.ArgumentsJson);
        var args = document?.RootElement ?? default;
        var name = tool.ToolName.ToLowerInvariant();
        var path = TextProperty(args, "path") ?? ".";
        var pattern = TextProperty(args, "pattern");

        return name switch
        {
            "write_stdin" => BackgroundTerminal(),
            "command_session" => DescribeTerminalSession(args),
            "exec_command" => new("Running", "Ran", TextProperty(args, "cmd") ?? Invocation(tool), ActivityKind.Command),
            "bash" or "powershell" => new("Running", "Ran", TextProperty(args, "command") ?? Invocation(tool), ActivityKind.LegacyCommand),
            "read_file" => new("Reading", "Read", path, ActivityKind.Exploration),
            "list_dir" => new("Listing", "List", path, ActivityKind.Exploration),
            "grep" or "glob" => new("Searching", "Search", pattern is null ? path : $"{pattern} in {path}", ActivityKind.Exploration),
            "write_file" => new("Writing", "Wrote", path),
            "edit_file" => new("Editing", "Edited", path),
            _ => new("Running", "Ran", Invocation(tool)),
        };
    }

    public static bool IsTerminalInteraction(ToolCallStartedEvent tool) => Describe(tool).IsTerminalInteraction;

    public static string Invocation(ToolCallStartedEvent tool) =>
        tool.ArgumentSummary.Length > 0 ? $"{tool.ToolName} {tool.ArgumentSummary}" : tool.ToolName;

    public static string RenderHeading(Presentation activity, bool? success, int width)
    {
        var label = success switch
        {
            true => activity.CompletedLabel,
            false when activity.IsTerminalInteraction => "Background terminal failed",
            false => "Failed",
            _ => activity.RunningLabel,
        };
        var target = Ansi.StripControls(activity.Target).Replace('\n', ' ').Replace('\t', ' ');
        if (target.Length > 0) target = " " + RenderTarget(activity.Kind, target);
        var line = "• ".Style(Ansi.Dim) + label.Style(success == false ? Ansi.Red + Ansi.Bold : Ansi.Bold) + target;
        return Ansi.TruncateVisible(line, width - 1);
    }

    public static void AppendDetail(List<string> lines, string detail, int width)
    {
        const int previewRows = 3;
        const string firstPrefix = "  └ ";
        const string nextPrefix = "    ";
        var available = width - firstPrefix.Length - 1;
        if (available <= 0) return;

        var count = 0;
        var text = Ansi.StripControls(detail).Replace("\t", "    ");
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0) continue;
            var start = 0;
            var columns = 0;
            for (var end = 0; end < trimmed.Length;)
            {
                var length = StringInfo.GetNextTextElementLength(trimmed.AsSpan(end));
                var cells = TextWidth.OfElement(trimmed.AsSpan(end, length));
                if (columns + cells > available && end > start)
                {
                    AddRow(trimmed, start, end);
                    start = end;
                    columns = 0;
                }
                columns += cells;
                end += length;
            }
            AddRow(trimmed, start, trimmed.Length);
        }

        if (count > previewRows)
        {
            var omitted = count - previewRows;
            var noun = omitted == 1 ? "line" : "lines";
            var notice = $"… +{omitted} {noun}";
            if (TextWidth.Of(notice) > available) notice = $"… +{omitted}";
            lines.Add((nextPrefix + Ansi.TruncateVisible(notice, available)).Style(Ansi.Dim));
        }

        void AddRow(string line, int start, int end)
        {
            if (count < previewRows)
            {
                var prefix = count == 0 ? firstPrefix : nextPrefix;
                lines.Add((prefix + Ansi.TruncateVisible(line[start..end], available)).Style(Ansi.Dim));
            }
            count++;
        }
    }

    public static bool TryReadTerminalResult(string result, out string output, out bool running)
    {
        using var document = ParseObject(result);
        var root = document?.RootElement ?? default;
        output = TextProperty(root, "output") ?? "";
        var status = TextProperty(root, "status");
        running = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        return status is not null;
    }

    private static Presentation DescribeTerminalSession(JsonElement args) => TextProperty(args, "action")?.ToLowerInvariant() switch
    {
        "read" or "write" => BackgroundTerminal(),
        "start" => new("Running", "Ran", TextProperty(args, "command") ?? "background terminal", ActivityKind.Command),
        "terminate" or "close" => new("Closing terminal", "Closed terminal", ""),
        "list" => new("Listing terminals", "Listed terminals", ""),
        _ => new("Managing terminal", "Managed terminal", ""),
    };

    private static Presentation BackgroundTerminal() =>
        new("Waiting for background terminal", "Background terminal", "", ActivityKind.TerminalInteraction);

    private static string RenderTarget(ActivityKind kind, string target)
    {
        if (kind is not (ActivityKind.Command or ActivityKind.LegacyCommand)) return target;
        var space = target.IndexOf(' ');
        return space < 0 ? target.Style(Ansi.Cyan) : target[..space].Style(Ansi.Cyan) + target[space..];
    }

    private static JsonDocument? ParseObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonDocument.Parse(value); }
        catch (JsonException) { return null; }
    }

    private static string? TextProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
