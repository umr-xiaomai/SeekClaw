using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Tools.Builtin;

namespace SeekClaw.Runtime.Agents;

/// <summary>Bounds unproductive command work within one turn, independently of the model.</summary>
internal sealed partial class CommandLoopGuard
{
    private const int UnresolvedLaunchLimit = 3;
    private const int FailedLaunchLimit = 8;
    private const int UnproductiveInteractionLimit = 3;
    private const int MaxAutomaticWaitMs = 30_000;

    private readonly Dictionary<CommandTarget, int> _unresolvedLaunches = [];
    private readonly Dictionary<string, SessionProgress> _sessions = new(StringComparer.Ordinal);
    private int _failedLaunches;

    public string? StopReason { get; private set; }

    public void Reset()
    {
        _unresolvedLaunches.Clear();
        _sessions.Clear();
        _failedLaunches = 0;
        StopReason = null;
    }

    public void RecordWorkspaceChange()
    {
        // Re-running a build or test after an actual edit can yield another result.
        foreach (var target in _unresolvedLaunches.Keys.Where(target => target.Kind != "ssh").ToArray())
            _unresolvedLaunches.Remove(target);
        _failedLaunches = 0;
    }

    public ToolCallRequest Prepare(ToolCallRequest call, out string? rejection)
    {
        rejection = null;
        var name = call.Name.ToLowerInvariant();
        if (name is not ("write_stdin" or CommandSessionTool.ToolName)) return call;

        var (args, _) = ToolArguments.Parse(call.ArgumentsJson);
        if (args is null || !IsInteraction(name, ReadString(args, "action"))) return call;
        var id = ReadString(args, "session_id");
        if (id is null || !_sessions.TryGetValue(id, out var state)) return call;

        var cursor = ReadNumber(args, "cursor");
        var input = ReadInput(name, args);
        if (cursor is >= 0 && cursor < state.Cursor)
        {
            rejection = $"Terminal action skipped because cursor {cursor} is stale. Use cursor {state.Cursor} or omit the cursor to continue with new output.";
        }
        else if (input is { } key && state.SentInputs.Contains(key))
        {
            rejection = $"This input was already sent. Read new output from session {id} before sending further input.";
        }
        else if (input is not null && state.Status is not (null or "running"))
        {
            rejection = $"Terminal input skipped because session {id} has already stopped. Check its exit status and remaining output.";
        }
        if (rejection is not null)
        {
            if (++state.RejectedInteractions >= UnproductiveInteractionLimit)
            {
                StopReason = "已停止重复的终端操作。" + SessionFollowUp(state.Status == "running" ? id : null);
            }
            return call;
        }

        if (input is not null || state.Status != "running" || state.EmptyPolls == 0) return call;

        // Spacing out polls keeps the waiting inside the runtime rather than
        // spending model steps asking whether a quiet process has exited yet.
        var waitProperty = name == "write_stdin" ? "yield_time_ms" : "wait_ms";
        var minimumWait = Math.Min(MaxAutomaticWaitMs, 1_000 << Math.Min(state.EmptyPolls, 5));
        if (ReadNumber(args, waitProperty) >= minimumWait) return call;
        args[waitProperty] = minimumWait;
        return call with { ArgumentsJson = args.ToJsonString() };
    }

    public void Observe(ToolCallRequest call, ChatMessage result, TimeSpan elapsed = default)
    {
        var name = call.Name.ToLowerInvariant();
        if (name is not ("exec_command" or "write_stdin" or CommandSessionTool.ToolName
            or BashTool.LegacyName or BashTool.PowerShellName)) return;

        var (args, _) = ToolArguments.Parse(call.ArgumentsJson);
        var action = ReadString(args, "action");
        var shell = name is BashTool.LegacyName or BashTool.PowerShellName;
        var isLaunch = shell || name == "exec_command" || name == CommandSessionTool.ToolName && action == "start";
        var session = shell ? default : SessionResult.From(result);
        if (isLaunch)
        {
            var command = ReadString(args, name == "exec_command" ? "cmd" : "command");
            var directory = ReadString(args, name == "exec_command" ? "workdir" : "cwd");
            ObserveLaunch(command, directory, session, succeeded: result.ToolSuccess && (shell || session.Completed));
            return;
        }

        var id = session.Id ?? ReadString(args, "session_id");
        var isInteraction = IsInteraction(name, action);
        if (id is null || !isInteraction && action is not ("terminate" or "close")) return;

        var wait = ReadNumber(args, name == "write_stdin" ? "yield_time_ms" : "wait_ms") ?? 0;
        ObserveSession(id, session, ReadInput(name, args), isInteraction, wait, elapsed);
    }

    private static bool IsInteraction(string name, string? action) => name == "write_stdin" || action is "read" or "write";

    private static int? ReadInput(string name, JsonObject? args)
    {
        if (name == CommandSessionTool.ToolName && ReadString(args, "action") != "write") return null;
        var text = ReadString(args, name == "write_stdin" ? "chars" : "text");
        var closeInput = name == CommandSessionTool.ToolName
            && args?["eof"] is JsonValue value && value.TryGetValue<bool>(out var eof) && eof;
        if (string.IsNullOrEmpty(text) && !closeInput) return null;
        return InputFingerprint(text ?? "", closeInput);
    }

    private static int InputFingerprint(string input, bool closeInput = false) =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')), closeInput);

    private void ObserveLaunch(string? command, string? directory, SessionResult result, bool succeeded)
    {
        var target = string.IsNullOrWhiteSpace(command) ? (CommandTarget?)null : CommandTarget.From(command, directory);
        if (result.Id is { } id)
        {
            var state = new SessionProgress(target) { Cursor = result.Cursor, Status = result.Status };
            if (result.Status == "running") ObservePrompt(state, result.Output.AsSpan());
            _sessions[id] = state;
        }

        if (succeeded)
        {
            RecordSuccess(target);
            return;
        }

        // A fresh session ID, startup banner, or login prompt does not prove that
        // re-launching achieved more than using the previous command session.
        if (target is { } key)
        {
            var count = _unresolvedLaunches.GetValueOrDefault(key) + 1;
            _unresolvedLaunches[key] = count;
            if (count >= UnresolvedLaunchLimit)
            {
                var liveId = _sessions.LastOrDefault(pair => pair.Value.Target == key && pair.Value.Status == "running").Key;
                var subject = key.Kind == "ssh" ? "同一 SSH 主机的连接尝试" : "同一命令的启动尝试";
                StopReason = $"{subject}已累计 {count} 次，仍未取得可验证的结果。已停止自动重试。"
                    + SessionFollowUp(liveId);
                return;
            }
        }

        // When command spellings and targets vary, still bound an uninterrupted
        // series of failed launches. A genuinely running command is not a failure.
        if (result.Status != "running" && ++_failedLaunches >= FailedLaunchLimit)
        {
            StopReason = $"已累计 {_failedLaunches} 次命令执行未成功，期间没有命令成功结束。已停止自动重试，请根据工具错误调整后继续。";
        }
    }

    private void ObserveSession(string id, SessionResult result, int? input, bool isInteraction, long waitMs, TimeSpan elapsed)
    {
        if (!_sessions.TryGetValue(id, out var state)) _sessions[id] = state = new SessionProgress(null);

        var previousStatus = state.Status;
        var output = result.Output.AsSpan();
        var newCharacters = Math.Min(output.Length, Math.Max(0, result.Cursor - Math.Max(0, state.Cursor)));
        var newOutput = output[^(int)newCharacters..];
        state.Cursor = Math.Max(result.Cursor, state.Cursor);
        state.Status = result.Status ?? state.Status;

        // A returned page shows that stdin was written even if the process then
        // failed. Log output by itself does not authorize resending stdin.
        var wroteInput = input is { } key && result.Id is not null && state.SentInputs.Add(key);
        if (result.Status == "running") ObservePrompt(state, newOutput);
        // Paging buffered output returns immediately, even for a time-based read.
        var drainedPage = result.HasMore && newCharacters > 0;
        if (!isInteraction || previousStatus != state.Status || !newOutput.IsWhiteSpace() || drainedPage || wroteInput)
        {
            state.EmptyPolls = 0;
            state.RejectedInteractions = 0;
            state.PrematureReturns = 0;
            if (result.Completed && previousStatus != state.Status) RecordSuccess(state.Target);
            return;
        }

        if (result.Status != "running")
        {
            if (++state.RejectedInteractions >= UnproductiveInteractionLimit)
                StopReason = "命令会话没有返回新输出。已停止重复读取，请检查退出状态或工具错误。";
            return;
        }

        state.EmptyPolls++;
        // A completed wait is valid even if a process was quiet. Only repeated
        // empty returns that bypassed a requested wait indicate a stuck loop.
        if (input is null && waitMs >= 1_000 && elapsed.TotalMilliseconds < Math.Min(waitMs, 60_000) / 2)
        {
            if (++state.PrematureReturns >= UnproductiveInteractionLimit)
                StopReason = "终端反复提前结束等待，未返回新输出。已结束本轮等待。" + SessionFollowUp(id);
        }
        else state.PrematureReturns = 0;
    }

    private static void ObservePrompt(SessionProgress state, ReadOnlySpan<char> output)
    {
        if (output.IsEmpty) return;
        var lineStart = output.LastIndexOf('\n') + 1;
        var startsNewLine = lineStart > 0 || state.OutputAtLineStart;
        var tail = output[lineStart..].Trim();
        state.OutputAtLineStart = startsNewLine && tail.IsEmpty;

        // A remote shell typically repeats its exact prompt after completing input.
        // A line break establishes a new prompt even when it straddles tool pages.
        if (tail.IsEmpty || output[^1] == '\r' || tail[^1] is not (':' or '?' or '>' or '$' or '#' or ']')) return;
        var currentPrompt = string.GetHashCode(tail, StringComparison.Ordinal);
        if (IsInputEcho(state, tail)) return;
        if (currentPrompt == state.LastPrompt && !startsNewLine) return;

        state.LastPrompt = currentPrompt;
        state.SentInputs.Clear();
    }

    private static bool IsInputEcho(SessionProgress state, ReadOnlySpan<char> text)
    {
        if (state.SentInputs.Count == 0) return false;
        var echo = text.ToString();
        return state.SentInputs.Contains(InputFingerprint(echo)) || state.SentInputs.Contains(InputFingerprint(echo + "\n"));
    }

    private void RecordSuccess(CommandTarget? target)
    {
        _failedLaunches = 0;
        if (target is { } key) _unresolvedLaunches.Remove(key);
    }

    private static string SessionFollowUp(string? id) => id is null
        ? "请根据工具结果调整方式后继续。"
        : $"命令会话 ID 为 {id}。运行时保持运行期间，可以在当前对话按 ID 继续查看或交互。";

    private sealed class SessionProgress(CommandTarget? target)
    {
        public CommandTarget? Target { get; } = target;
        public long Cursor { get; set; } = -1;
        public string? Status { get; set; }
        public HashSet<int> SentInputs { get; } = [];
        public int? LastPrompt { get; set; }
        public bool OutputAtLineStart { get; set; }
        public int EmptyPolls { get; set; }
        public int RejectedInteractions { get; set; }
        public int PrematureReturns { get; set; }
    }

    private readonly record struct SessionResult(string? Id, string? Status, long Cursor, string? Output, bool Completed,
        bool HasMore)
    {
        public static SessionResult From(ChatMessage result)
        {
            var parsed = result.ToolStructuredContent as JsonObject;
            if (parsed is null && result.Text.AsSpan().TrimStart().StartsWith("{"))
            {
                try { parsed = JsonNode.Parse(result.Text) as JsonObject; }
                catch (JsonException) { }
            }
            var status = ReadString(parsed, "status");
            var hasMore = parsed?["hasMore"] is JsonValue value && value.TryGetValue(out bool more) && more;
            return new SessionResult(ReadString(parsed, "sessionId"), status,
                ReadNumber(parsed, "cursor") ?? -1, ReadString(parsed, "output"),
                status == "exited" && ReadNumber(parsed, "exitCode") == 0, hasMore);
        }
    }

    private readonly record struct CommandTarget(string Kind, string Value, string? Directory = null)
    {
        public static CommandTarget From(string command, string? directory) => SshHost(command) is { } host
            ? new("ssh", host.ToLowerInvariant())
            : new("shell", command.Trim(), string.IsNullOrWhiteSpace(directory) ? "." : directory.Trim());
    }

    private static string? ReadString(JsonObject? value, string key) =>
        value?[key] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;

    private static long? ReadNumber(JsonObject? value, string key)
    {
        if (value?[key] is not JsonValue item) return null;
        if (item.TryGetValue<long>(out var number)) return number;
        return item.TryGetValue<int>(out var integer) ? integer : null;
    }

    // Look for a literal ssh invocation at a statement boundary. Skip quoted text
    // and PowerShell here-strings, which often contain the remote command itself.
    // A script with several destinations is not attributed to a single host.
    private static string? SshHost(string command)
    {
        var host = SshHostAt(command, 0);
        foreach (Match span in ShellSpans().Matches(command))
        {
            if (!span.Groups["boundary"].Success) continue;
            var next = SshHostAt(command, span.Index + span.Length);
            if (next is null) continue;
            if (host is not null && !host.Equals(next, StringComparison.OrdinalIgnoreCase)) return null;
            host = next;
        }
        return host;
    }

    private static string? SshHostAt(string command, int start)
    {
        var token = ShellWord().Match(command, start);
        if (TokenValue(token) == "&") token = token.NextMatch();
        if (!token.Success) return null;

        var executable = TokenValue(token);
        executable = executable[(executable.LastIndexOfAny(['/', '\\']) + 1)..];
        if (!executable.Equals("ssh", StringComparison.OrdinalIgnoreCase)
            && !executable.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase)) return null;

        for (token = token.NextMatch(); token.Success; token = token.NextMatch())
        {
            var value = TokenValue(token);
            if (value.StartsWith('-'))
            {
                // OpenSSH options that take a following argument (attached values
                // are part of the current token).
                if (value.Length == 2 && "BcDEeFIiJLlmOoPpQRSWw".Contains(value[1])) token = token.NextMatch();
                continue;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "ssh" && uri.Host.Length > 0)
                return uri.Host;
            var host = value[(value.LastIndexOf('@') + 1)..];
            return host.Length > 0 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')
                ? host : null;
        }
        return null;
    }

    private static string TokenValue(Match token) => token.Groups[1].Success ? token.Groups[1].Value
        : token.Groups[2].Success ? token.Groups[2].Value
        : token.Groups[3].Value;

    [GeneratedRegex("""
        (?msx)
        @"\r?\n.*?^"@ | @'\r?\n.*?^'@
        | "(?:[`\\].|[^"`\\])*" | '(?:''|[^'])*'
        | [`\\]\r?\n | \#[^\r\n]* | (?<boundary>[;\r\n]|&&|\|\|)
        """, RegexOptions.CultureInvariant)]
    private static partial Regex ShellSpans();

    [GeneratedRegex("""\G(?:[ \t]+|[`\\]\r?\n)*(?:"([^"\r\n]*)"|'([^'\r\n]*)'|(&(?=\s)|[^\s"';&|<>]+))""", RegexOptions.CultureInvariant)]
    private static partial Regex ShellWord();
}
