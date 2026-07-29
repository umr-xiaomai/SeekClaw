using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli.Ui;

/// <summary>
/// The terminal render loop. Runs on its own thread at ~12 FPS:
///   Agent (business thread) → EventBus → render queue → this renderer → console.
/// High-frequency events are coalesced per frame; the live area is updated in place,
/// finalized output scrolls above it. The runtime never writes to the console itself.
/// </summary>
public sealed class TerminalRenderer : IDisposable
{
    private const int FrameMs = 80;
    private const int ThinkingPreviewMs = 240;
    private const int ThinkingPreviewRows = 2;
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly IEventSubscription _subscription;
    private readonly ConcurrentQueue<string> _externalLines = new();
    private readonly LiveRegion _live;
    private LineEditorFrame? _inputFrame;
    private readonly bool _showTurnDividers;
    private readonly Thread _thread;
    private volatile bool _disposed;

    // Render-thread-only state (mutated exclusively inside the loop).
    private readonly StringBuilder _streamBuffer = new();
    private readonly StringBuilder _thinkingBuffer = new();
    private readonly StringBuilder _toolOutputBuffer = new();  // For ToolCallProgressEvent
    private readonly List<string> _thinkingPreview = [];
    private readonly Dictionary<string, (string Name, string Args, long StartedAt)> _activeTools = [];
    private readonly Stopwatch _turnClock = new();
    private bool _turnActive;
    private bool _thinkingActive;
    private string _status = "";
    private string _statusDetail = "";
    private string _modelRef = "";
    private long _sessionInputTokens;
    private long _sessionOutputTokens;
    private decimal _sessionCost;
    private int _spinnerTick;
    private long _thinkingStartedAt;
    private long _nextThinkingPreviewAt;

    public TerminalRenderer(IEventBus bus, TextWriter? output = null, bool showTurnDividers = false)
    {
        VirtualTerminal.Enable();
        _live = new LiveRegion(output ?? Console.Out);
        _showTurnDividers = showTurnDividers;
        _subscription = bus.Subscribe();
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "seekclaw-render" };
        _thread.Start();
    }

    /// <summary>Thread-safe: queue plain lines into scrollback (user prompt echo, REPL notices).</summary>
    public void WriteLine(string line = "") => _externalLines.Enqueue(line);

    /// <summary>Thread-safe: replaces the editable input area rendered below live agent output.</summary>
    public void SetInputFrame(LineEditorFrame? frame) => Volatile.Write(ref _inputFrame, frame);

    // ---------------------------------------------------------------- render loop

    private void RenderLoop()
    {
        while (!_disposed)
        {
            var scrollback = new List<string>();

            while (_externalLines.TryDequeue(out var line))
                scrollback.Add(line);

            while (_subscription.Reader.TryRead(out var evt))
                Apply(evt, scrollback);

            _spinnerTick++;
            var live = BuildLiveLines();
            var input = Volatile.Read(ref _inputFrame);
            if (input is not null)
                live.AddRange(input.Lines);
            _live.Update(scrollback, live, input?.CursorRowsBelow ?? 0, input?.CursorColumn ?? 0);
            Thread.Sleep(FrameMs);
        }
    }

    private void Apply(RuntimeEvent evt, List<string> scrollback)
    {
        switch (evt)
        {
            case TurnStartedEvent:
                _turnActive = true;
                _turnClock.Restart();
                _streamBuffer.Clear();
                _thinkingBuffer.Clear();
                _toolOutputBuffer.Clear();
                _thinkingPreview.Clear();
                _activeTools.Clear();
                _thinkingStartedAt = 0;
                _nextThinkingPreviewAt = 0;
                _status = "Thinking";
                _statusDetail = "";
                break;

            case UserSteerEvent steer:
                scrollback.Add("");
                scrollback.Add($"↳ Steering Instruction: {steer.Instruction}".Style(Ansi.Cyan + Ansi.Bold));
                scrollback.Add("");
                break;

            case StatusEvent status:
                _status = status.Status;
                _statusDetail = status.Detail ?? "";
                break;

            case ModelInvocationStartedEvent model:
                _modelRef = $"{model.ProviderId}/{model.ModelId}";
                break;

            case ThinkingDeltaEvent thinking:
                if (!_thinkingActive)
                    _thinkingStartedAt = Environment.TickCount64;
                _thinkingActive = true;
                _thinkingBuffer.Append(thinking.Delta);
                break;

            case ThinkingCompletedEvent:
                if (_thinkingActive && _thinkingBuffer.Length > 0)
                {
                    scrollback.Add("");
                    var elapsed = Math.Max(0, Environment.TickCount64 - _thinkingStartedAt) / 1000.0;
                    scrollback.Add($"✻ Thought for {Math.Max(0.1, elapsed):0.0}s".Style(Ansi.Cyan + Ansi.Dim));
                    var thinkingLines = _thinkingBuffer.ToString().Split('\n');
                    foreach (var line in thinkingLines.Where(l => l.Length > 0))
                        scrollback.Add(("  " + line).Style(Ansi.Gray + Ansi.Italic));
                }
                _thinkingActive = false;
                _thinkingBuffer.Clear();
                _thinkingPreview.Clear();
                break;

            case AssistantTextDeltaEvent delta:
                _streamBuffer.Append(delta.Delta);
                break;

            case AssistantMessageCompletedEvent message:
                // Replace the streamed tail with the fully rendered markdown in scrollback.
                _streamBuffer.Clear();
                scrollback.Add("");
                scrollback.AddRange(MarkdownAnsi.Render(message.Text, SafeWidth() - 2));
                break;

            case ToolCallStartedEvent tool:
                _activeTools[tool.CallId] = (tool.ToolName, tool.ArgumentSummary, Environment.TickCount64);
                _toolOutputBuffer.Clear();
                break;

            case ToolCallProgressEvent progress:
                _toolOutputBuffer.Append(progress.Message);
                break;

            case ToolCallCompletedEvent done:
            {
                _activeTools.Remove(done.CallId);

                // If there's accumulated tool output, add it to scrollback as a formatted block
                if (_toolOutputBuffer.Length > 0)
                {
                    scrollback.Add("");
                    scrollback.Add(("► " + done.ToolName).Style(Ansi.Blue + Ansi.Bold));
                    var toolLines = _toolOutputBuffer.ToString().Split('\n');
                    foreach (var line in toolLines.Where(l => l.Length > 0))
                    {
                        if (line.Length > 100)
                            scrollback.Add(("  " + line[..97] + "...").Style(Ansi.Dim));
                        else
                            scrollback.Add(("  " + line).Style(Ansi.Dim));
                    }
                    _toolOutputBuffer.Clear();
                }

                var mark = done.Success ? "✓".Style(Ansi.Green) : "✗".Style(Ansi.Red);
                var duration = done.Duration.TotalSeconds >= 0.1 ? $" ({done.Duration.TotalSeconds:0.0}s)" : "";
                scrollback.Add($"{mark} {done.ResultSummary.Style(Ansi.Dim)}{duration.Style(Ansi.Gray)}");
                scrollback.Add("");
                break;
            }

            case FileDiffEvent diff:
                scrollback.AddRange(RenderDiff(diff.UnifiedDiff));
                break;

            case ProviderRetryEvent retry:
                scrollback.Add($"↻ retry #{retry.Attempt} on {retry.ModelRef}: {retry.Reason}".Style(Ansi.Yellow));
                break;

            case ProviderSwitchedEvent switched:
                scrollback.Add($"⇄ failover {switched.FromRef} → {switched.ToRef} ({switched.Reason})".Style(Ansi.Yellow));
                break;

            case UsageRecordedEvent usage:
                _sessionInputTokens += usage.InputTokens;
                _sessionOutputTokens += usage.OutputTokens;
                _sessionCost += usage.Cost;
                break;

            case VerificationStartedEvent verify:
                scrollback.Add($"⚙ verify: {verify.Command}".Style(Ansi.Cyan));
                break;

            case VerificationCompletedEvent verified:
                scrollback.Add(verified.Success
                    ? "✓ verification passed".Style(Ansi.Green)
                    : $"✗ verification failed (attempt {verified.Attempt}) — repairing".Style(Ansi.Red));
                break;

            case WarningEvent warning:
                scrollback.Add(("⚠ " + warning.Message).Style(Ansi.Yellow));
                break;

            case ErrorEvent error:
                scrollback.Add(("✗ " + error.Message + (error.Detail is null ? "" : $": {error.Detail}")).Style(Ansi.Red));
                break;

            case TurnCompletedEvent completed:
                if (_streamBuffer.Length > 0)
                {
                    // Model was cancelled mid-stream: keep what we have.
                    scrollback.Add("");
                    scrollback.AddRange(MarkdownAnsi.Render(_streamBuffer.ToString(), SafeWidth() - 2));
                    _streamBuffer.Clear();
                }
                if (completed.Cancelled)
                    scrollback.Add("— cancelled —".Style(Ansi.Yellow));
                if (_showTurnDividers)
                    scrollback.Add(TurnDivider());
                _turnActive = false;
                _activeTools.Clear();
                _thinkingActive = false;
                _turnClock.Stop();
                break;
        }
    }

    private List<string> BuildLiveLines()
    {
        if (!_turnActive) return [];

        var lines = new List<string>();

        foreach (var (_, (name, args, startedAt)) in _activeTools)
        {
            var elapsed = (Environment.TickCount64 - startedAt) / 1000.0;
            var suffix = elapsed >= 1 ? $" {elapsed:0.0}s" : "";
            lines.Add($"{Spinner()} {name.Style(Ansi.Bold)}({args.Style(Ansi.Dim)}){suffix.Style(Ansi.Gray)}");
        }

        if (_thinkingActive)
        {
            var now = Environment.TickCount64;
            var elapsed = now - _thinkingStartedAt >= 1000 ? $" · {(now - _thinkingStartedAt) / 1000.0:0}s" : "";
            lines.Add($"{Spinner().Style(Ansi.Cyan)} {"Thinking…".Style(Ansi.Cyan)}{elapsed.Style(Ansi.Gray)}");

            if (_thinkingPreview.Count == 0 || now >= _nextThinkingPreviewAt)
            {
                _thinkingPreview.Clear();
                _thinkingPreview.AddRange(BuildStableTail(
                    _thinkingBuffer.ToString(), Math.Max(20, SafeWidth() - 5), ThinkingPreviewRows));
                while (_thinkingPreview.Count < ThinkingPreviewRows)
                    _thinkingPreview.Add("");
                _nextThinkingPreviewAt = now + ThinkingPreviewMs;
            }

            foreach (var line in _thinkingPreview)
                lines.Add(("  " + line).Style(Ansi.Gray + Ansi.Italic));
        }

        foreach (var line in BuildStableTail(_streamBuffer.ToString(), Math.Max(20, SafeWidth() - 1), 6))
            lines.Add(line);

        // Show tool output if any tool is active
        if (_toolOutputBuffer.Length > 0)
        {
            lines.Add("» Tool Output".Style(Ansi.Blue + Ansi.Dim));
            foreach (var line in BuildStableTail(_toolOutputBuffer.ToString(), Math.Max(20, SafeWidth() - 3), 4))
                lines.Add(("  " + line).Style(Ansi.Dim));
        }

        if (!_thinkingActive)
        {
            var elapsedText = _turnClock.Elapsed.TotalSeconds >= 1 ? $" · {_turnClock.Elapsed.TotalSeconds:0}s" : "";
            var detail = _statusDetail.Length > 0 ? $" {_statusDetail}" : "";
            lines.Add($"{Spinner().Style(Ansi.Cyan)} {_status.Style(Ansi.Cyan)}{detail.Style(Ansi.Dim)}{elapsedText.Style(Ansi.Gray)}"
                      + " (ctrl+c to cancel)".Style(Ansi.Gray));
        }

        var tokens = _sessionInputTokens + _sessionOutputTokens;
        var costText = _sessionCost > 0 ? $" · ${_sessionCost:0.####}" : "";
        if (_modelRef.Length > 0 || tokens > 0 || costText.Length > 0)
            lines.Add($"{_modelRef}{(tokens > 0 ? $" · {tokens:N0} tokens" : "")}{costText}".Style(Ansi.Gray));

        return lines;
    }

    private string Spinner() => SpinnerFrames[_spinnerTick % SpinnerFrames.Length];

    internal static IReadOnlyList<string> BuildStableTail(string text, int lineWidth, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text) || lineWidth <= 0 || maxLines <= 0) return [];

        // Streams can be very long. Only shape enough recent text to fill the live preview.
        var sampleLength = Math.Min(text.Length, Math.Max(256, lineWidth * maxLines * 4));
        var start = text.Length - sampleLength;
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;

        var lines = new List<string>();
        var line = new StringBuilder();
        var width = 0;
        var pendingSpace = false;

        foreach (var rune in text.AsSpan(start).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = line.Length > 0;
                continue;
            }

            var glyph = rune.ToString();
            var glyphWidth = TextWidth.Of(glyph);
            var spaceWidth = pendingSpace && line.Length > 0 ? 1 : 0;
            if (line.Length > 0 && width + spaceWidth + glyphWidth > lineWidth)
            {
                lines.Add(line.ToString());
                line.Clear();
                width = 0;
                spaceWidth = 0;
            }

            if (spaceWidth > 0)
            {
                line.Append(' ');
                width++;
            }
            line.Append(glyph);
            width += glyphWidth;
            pendingSpace = false;
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines.TakeLast(maxLines).ToList();
    }

    private static IEnumerable<string> RenderDiff(string unifiedDiff)
    {
        var lines = unifiedDiff.Split('\n');
        var shown = 0;
        foreach (var line in lines)
        {
            if (shown++ > 40)
            {
                yield return $"  … diff truncated ({lines.Length - shown} more lines)".Style(Ansi.Gray);
                yield break;
            }
            yield return line switch
            {
                _ when line.StartsWith("+++", StringComparison.Ordinal) => ("  " + line).Style(Ansi.Bold),
                _ when line.StartsWith("---", StringComparison.Ordinal) => ("  " + line).Style(Ansi.Bold),
                _ when line.StartsWith("@@", StringComparison.Ordinal) => ("  " + line).Style(Ansi.Cyan),
                _ when line.StartsWith('+') => ("  " + line).Style(Ansi.Green),
                _ when line.StartsWith('-') => ("  " + line).Style(Ansi.Red),
                _ => ("  " + line).Style(Ansi.Dim),
            };
        }
    }

    private static int SafeWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch (IOException) { return 100; }
    }

    private static string TurnDivider() =>
        new string('─', Math.Max(20, SafeWidth() - 1)).Style(Ansi.Gray);

    /// <summary>Blocks briefly so queued frames land before the prompt is shown again.</summary>
    public void Flush()
    {
        var deadline = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < deadline
               && (!_externalLines.IsEmpty || _subscription.Reader.TryPeek(out _)))
            Thread.Sleep(FrameMs);
        Thread.Sleep(FrameMs * 2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _thread.Join(1000);
        _live.Clear();
        _subscription.Dispose();
    }
}
