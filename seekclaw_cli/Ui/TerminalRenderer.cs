using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SeekClaw.Runtime.Events;

namespace SeekClaw.Cli.Ui;

/// <summary>
/// The terminal render loop. Runs on its own thread at ~30 FPS:
///   Agent (business thread) → EventBus → render queue → this renderer → console.
/// High-frequency events are coalesced per frame; the live area is updated in place,
/// finalized output scrolls above it. The runtime never writes to the console itself.
/// </summary>
public sealed class TerminalRenderer : IDisposable
{
    private const int FrameMs = 33; // ~30 FPS
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly IEventSubscription _subscription;
    private readonly ConcurrentQueue<string> _externalLines = new();
    private readonly LiveRegion _live;
    private readonly Thread _thread;
    private volatile bool _disposed;

    // Render-thread-only state (mutated exclusively inside the loop).
    private readonly StringBuilder _streamBuffer = new();
    private readonly StringBuilder _thinkingBuffer = new();
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

    // Track displayed buffer lengths to avoid replicating content each frame.
    private int _thinkingBufferLength;
    private int _streamBufferLength;

    public TerminalRenderer(IEventBus bus, TextWriter? output = null)
    {
        VirtualTerminal.Enable();
        _live = new LiveRegion(output ?? Console.Out);
        _subscription = bus.Subscribe();
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "seekclaw-render" };
        _thread.Start();
    }

    /// <summary>Thread-safe: queue plain lines into scrollback (user prompt echo, REPL notices).</summary>
    public void WriteLine(string line = "") => _externalLines.Enqueue(line);

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
            _live.Update(scrollback, BuildLiveLines());
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
                _thinkingBufferLength = 0;
                _streamBufferLength = 0;
                _activeTools.Clear();
                _status = "Thinking";
                _statusDetail = "";
                break;

            case StatusEvent status:
                _status = status.Status;
                _statusDetail = status.Detail ?? "";
                break;

            case ModelInvocationStartedEvent model:
                _modelRef = $"{model.ProviderId}/{model.ModelId}";
                break;

            case ThinkingDeltaEvent thinking:
                _thinkingActive = true;
                _thinkingBuffer.Append(thinking.Delta);
                break;

            case ThinkingCompletedEvent:
                if (_thinkingActive && _thinkingBuffer.Length > 0)
                    scrollback.Add($"✻ thought for {_turnClock.Elapsed.TotalSeconds:0.0}s".Style(Ansi.Gray));
                _thinkingActive = false;
                _thinkingBuffer.Clear();
                _thinkingBufferLength = 0;
                break;

            case AssistantTextDeltaEvent delta:
                _streamBuffer.Append(delta.Delta);
                break;

            case AssistantMessageCompletedEvent message:
                // Replace the streamed tail with the fully rendered markdown in scrollback.
                _streamBuffer.Clear();
                _streamBufferLength = 0;
                scrollback.Add("");
                scrollback.AddRange(MarkdownAnsi.Render(message.Text, SafeWidth() - 2));
                break;

            case ToolCallStartedEvent tool:
                _activeTools[tool.CallId] = (tool.ToolName, tool.ArgumentSummary, Environment.TickCount64);
                break;

            case ToolCallCompletedEvent done:
            {
                _activeTools.Remove(done.CallId);
                var mark = done.Success ? "✓".Style(Ansi.Green) : "✗".Style(Ansi.Red);
                var duration = done.Duration.TotalSeconds >= 0.1 ? $" ({done.Duration.TotalSeconds:0.0}s)" : "";
                scrollback.Add($"{mark} {done.ToolName.Style(Ansi.Bold)} {done.ResultSummary.Style(Ansi.Dim)}{duration.Style(Ansi.Gray)}");
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
                    _streamBufferLength = 0;
                }
                if (completed.Cancelled)
                    scrollback.Add("— cancelled —".Style(Ansi.Yellow));
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
            foreach (var line in TailIncremental(_thinkingBuffer, 3, ref _thinkingBufferLength))
                lines.Add(("✻ " + line).Style(Ansi.Gray + Ansi.Italic));
        }

        foreach (var line in TailIncremental(_streamBuffer, 6, ref _streamBufferLength))
            lines.Add(line);

        var elapsedText = _turnClock.Elapsed.TotalSeconds >= 1 ? $" · {_turnClock.Elapsed.TotalSeconds:0}s" : "";
        var detail = _statusDetail.Length > 0 ? $" {_statusDetail}" : "";
        lines.Add($"{Spinner().Style(Ansi.Cyan)} {_status.Style(Ansi.Cyan)}{detail.Style(Ansi.Dim)}{elapsedText.Style(Ansi.Gray)}"
                  + " (ctrl+c to cancel)".Style(Ansi.Gray));

        var tokens = _sessionInputTokens + _sessionOutputTokens;
        var costText = _sessionCost > 0 ? $" · ${_sessionCost:0.####}" : "";
        lines.Add($"{_modelRef}{(tokens > 0 ? $" · {tokens:N0} tok" : "")}{costText}".Style(Ansi.Gray));

        return lines;
    }

    private string Spinner() => SpinnerFrames[_spinnerTick % SpinnerFrames.Length];

    /// <summary>
    /// Returns only the new content added to the buffer since last call.
    /// Uses character position rather than line count to avoid edge cases.
    /// </summary>
    private IEnumerable<string> TailIncremental(StringBuilder buffer, int maxLines, ref int lastLength)
    {
        // If buffer is now shorter, it was reset - start from the beginning
        if (buffer.Length < lastLength)
            lastLength = 0;

        // Extract only the new content
        var newContent = buffer.ToString(lastLength, buffer.Length - lastLength);
        lastLength = buffer.Length;

        if (newContent.Length == 0) return [];

        // Split into lines and return, limiting to maxLines
        var lines = newContent.Split('\n');

        // Remove last empty entry if the content ends with \n
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];

        // Return only the last maxLines
        return lines.TakeLast(maxLines).Select(l => l.TrimEnd('\r'));
    }

    private static IEnumerable<string> Tail(StringBuilder buffer, int maxLines)
    {
        if (buffer.Length == 0) return [];
        var text = buffer.ToString();
        var lines = text.Split('\n');
        return lines.Skip(Math.Max(0, lines.Length - maxLines)).Select(l => l.TrimEnd('\r'));
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
