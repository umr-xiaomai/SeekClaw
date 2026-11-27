using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Events;
using System.Threading.Channels;
using System.Text;

namespace SeekClaw.Cli.Ui;

/// <summary>Draws terminal updates after runtime events, input changes, and resize notifications.</summary>
public sealed class TerminalRenderer : IDisposable
{
    private readonly IEventSubscription _subscription;
    private readonly Channel<UiChange> _uiChanges = Channel.CreateUnbounded<UiChange>(new() { SingleReader = true });
    private readonly LiveRegion _live;
    private readonly TerminalHistory _history = new();
    private readonly MarkdownStreamRenderer _streamMarkdown = new();
    private readonly Func<string>? _getModeText;
    private readonly Func<int?>? _getContextWindow;
    private readonly Func<string?>? _getActiveModel;
    private readonly Func<string?>? _getWorkingDirectory;
    private readonly bool _showTurnDividers;
    private readonly Timer _resizeMonitor;
    private readonly Task _renderTask;
    private TerminalSize _observedSize;
    private CliPalette _requestedPalette;
    private CliPalette _palette;
    private LineEditorFrame? _inputFrame;

    // Only the renderer mutates the turn state.
    private readonly StringBuilder _streamBuffer = new();
    private IReadOnlyList<string> _streamRenderedLines = [];
    private readonly Dictionary<string, ActiveTool> _activeTools = [];
    private readonly List<ToolDisplay.Presentation> _explored = [];
    private int _streamWidth;
    private string? _streamDirectory;
    private bool _turnActive;
    private bool _thinkingActive;
    private bool _streamDirty;
    private string _status = "";
    private string _statusDetail = "";
    private string _modelRef = "";
    private long _latestContextTokens;
    private string _modelWithUsage = "";

    private sealed record UiChange(
        string? Line = null,
        TaskCompletionSource? Drawn = null,
        LineEditorFrame? Input = null,
        bool SetInput = false,
        bool SubmitInput = false)
    {
        public static readonly UiChange Redraw = new();
    }

    private sealed record ActiveTool(ToolDisplay.Presentation Display)
    {
        public StringBuilder Output { get; } = new();
    }

    public TerminalRenderer (
        IEventBus bus,
        TextWriter? output = null,
        bool showTurnDividers = false,
        Func<string>? getModeText = null,
        Func<int?>? getContextWindow = null,
        Func<string?>? getActiveModel = null,
        Func<string?>? getWorkingDirectory = null,
        CliPalette? palette = null)
    {
        _live = new LiveRegion(output ?? Console.Out);
        _showTurnDividers = showTurnDividers;
        _getModeText = getModeText;
        _getContextWindow = getContextWindow;
        _getActiveModel = getActiveModel;
        _getWorkingDirectory = getWorkingDirectory;
        _requestedPalette = _palette = palette ?? CliPalette.Default;
        _subscription = bus.Subscribe();
        _observedSize = TerminalSize.Read();
        _renderTask = Task.Run(RenderLoopAsync);
        // System.Console does not expose a portable resize event. Measure periodically,
        // but only request a draw when the terminal dimensions actually change.
        _resizeMonitor = new Timer(CheckForResize, null, 200, 200);
    }

    public CliPalette Palette => Volatile.Read(ref _requestedPalette);

    /// <summary>Uses the palette for live content and subsequent transcript entries.</summary>
    public void SetPalette(CliPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (Interlocked.Exchange(ref _requestedPalette, palette) != palette)
            _uiChanges.Writer.TryWrite(UiChange.Redraw);
    }

    /// <summary>Queues an application notice or submitted input for scrollback.</summary>
    public void WriteLine (string line = "") => _uiChanges.Writer.TryWrite(new UiChange(Line: line));

    /// <summary>Replaces the editable input area below live agent output.</summary>
    public void SetInputFrame (LineEditorFrame? frame) =>
        _uiChanges.Writer.TryWrite(new UiChange(Input: frame, SetInput: true));

    /// <summary>Moves the submitted editor line into scrollback in a single terminal update.</summary>
    public void SubmitInput (string line) => _uiChanges.Writer.TryWrite(new UiChange(Line: line, SetInput: true, SubmitInput: true));

    private void CheckForResize (object? state)
    {
        var size = TerminalSize.Read();
        if (size == _observedSize) return;
        _observedSize = size;
        _uiChanges.Writer.TryWrite(UiChange.Redraw);
    }

    private async Task RenderLoopAsync ()
    {
        Task<bool>? nextRuntimeEvent = null;
        Task<bool>? nextUiChange = null;
        var runtimeClosed = false;
        try
        {
            while (true)
            {
                var size = TerminalSize.Read();
                var drawn = new List<TaskCompletionSource>();
                while (_uiChanges.Reader.TryRead(out var change))
                {
                    UpdatePalette();
                    if (change.SetInput) _inputFrame = change.Input;
                    if (change.Line is { } line)
                    {
                        if (change.SubmitInput) _history.AddPrompt(line, AssistantWidth(size), _palette);
                        else _history.Add(line);
                    }
                    if (change.Drawn is { } waiter) drawn.Add(waiter);
                }
                UpdatePalette();
                while (_subscription.Reader.TryRead(out var evt))
                    Apply(evt, size);

                var input = _inputFrame;
                var footer = BuildFooterStatus();
                if (input is { FooterRow: >= 0 })
                {
                    var mode = AgentModeExtensions.Parse(_getModeText?.Invoke() ?? "edit");
                    input = input.WithFooter(LineEditor.FormatFooter(size.Width, $"{mode} · / for commands", footer));
                }
                var live = BuildLiveLines(size, input?.Lines.Count ?? 0, footer);
                if (input is not null) live.AddRange(input.Lines);
                var history = _history.TakeDraw(AssistantWidth(size), replay: !_live.CanEraseAfterResize(size), palette: _palette);
                _live.Update(history.Rows, live, size, input?.CursorRowsBelow ?? 0, input?.CursorColumn ?? 0,
                    replaceHistory: history.Replay, palette: _palette);
                foreach (var waiter in drawn) waiter.TrySetResult();

                // Dispose may close both writers after the drains above. Finish any
                // runtime events produced in that interval before clearing the screen.
                if (_uiChanges.Reader.Completion.IsCompleted && _subscription.Reader.Completion.IsCompleted) return;
                nextUiChange ??= _uiChanges.Reader.WaitToReadAsync().AsTask();
                if (runtimeClosed)
                {
                    await nextUiChange.ConfigureAwait(false);
                }
                else
                {
                    nextRuntimeEvent ??= _subscription.Reader.WaitToReadAsync().AsTask();
                    await Task.WhenAny(nextUiChange, nextRuntimeEvent).ConfigureAwait(false);
                    if (nextRuntimeEvent.IsCompleted)
                    {
                        runtimeClosed = !await nextRuntimeEvent.ConfigureAwait(false);
                        nextRuntimeEvent = null;
                    }
                }
                if (nextUiChange.IsCompleted) nextUiChange = null;
            }
        }
        finally
        {
            _live.Clear();
        }
    }

    private void UpdatePalette()
    {
        var palette = Palette;
        if (_palette == palette) return;
        _palette = palette;
        _streamDirty = true;
    }

    private void Apply (RuntimeEvent evt, TerminalSize size)
    {
        switch (evt)
        {
            case TurnStartedEvent:
                _turnActive = true;
                _modelRef = "";
                ResetStream();
                _activeTools.Clear();
                _explored.Clear();
                _status = "Thinking";
                _statusDetail = "";
                break;

            case UserSteerEvent steer:
                _history.Add("");
                _history.Add($"↳ Steering Instruction: {steer.Instruction}".Style(Ansi.Cyan + Ansi.Bold));
                _history.Add("");
                break;

            case SubAgentStartedEvent subStart:
                _history.Add("");
                _history.Add($"┌─► [SubAgent: {subStart.Role}] ({subStart.SubAgentId})".Style(Ansi.Magenta + Ansi.Bold));
                _history.Add($"│   Task: {subStart.TaskPrompt}".Style(Ansi.Magenta + Ansi.Dim));
                break;

            case SubAgentProgressEvent subProg:
                var subDetail = string.IsNullOrWhiteSpace(subProg.Detail) ? "" : $" ({subProg.Detail})";
                _history.Add($"│   [{subProg.Role}] {subProg.Status}{subDetail}".Style(Ansi.Gray));
                break;

            case SubAgentCompletedEvent subDone:
                var symbol = subDone.Success ? "✓" : "✗";
                var color = subDone.Success ? Ansi.Green : Ansi.Red;
                _history.Add($"└─► [SubAgent: {subDone.Role}] {symbol} ({subDone.Elapsed.TotalSeconds:0.1}s): {subDone.Summary}".Style(color + Ansi.Bold));
                _history.Add("");
                break;

            case StatusEvent status:
                _status = Ansi.StripControls(status.Status);
                _statusDetail = Ansi.StripControls(status.Detail ?? "");
                break;

            case ModelInvocationStartedEvent model:
                _modelRef = $"{model.ProviderId}/{model.ModelId}";
                break;

            case ThinkingDeltaEvent:
                _thinkingActive = true;
                break;

            case ThinkingCompletedEvent:
                _thinkingActive = false;
                break;

            case AssistantTextDeltaEvent delta:
                if (_streamBuffer.Length == 0)
                {
                    CommitExploration(size.Width);
                    _streamDirectory = _getWorkingDirectory?.Invoke();
                }
                _streamBuffer.Append(delta.Delta);
                _streamDirty = true;
                break;

            case AssistantStreamResetEvent:
                ResetStream();
                break;

            case AssistantMessageCompletedEvent message:
                CommitExploration(size.Width);
                CompleteAssistant(message.Text, size);
                ResetStream();
                break;

            case ToolCallStartedEvent tool:
            {
                var display = ToolDisplay.Describe(tool);
                if (!display.IsExploration) CommitExploration(size.Width);
                _activeTools[tool.CallId] = new ActiveTool(display);
                break;
            }

            case ToolCallProgressEvent progress:
                if (_activeTools.TryGetValue(progress.CallId, out var activeTool))
                    activeTool.Output.Append(progress.Message);
                break;

            case ToolCallCompletedEvent done:
            {
                _activeTools.Remove(done.CallId, out var toolResult);
                var display = toolResult?.Display ?? new ToolDisplay.Presentation("Running", "Ran", done.ToolName);
                if (done.Success && display.IsExploration)
                {
                    _explored.Add(display);
                    break;
                }
                if (display.IsExploration) CommitExploration(size.Width);
                var detail = done.Success && toolResult?.Output.Length > 0
                    ? toolResult.Output.ToString()
                    : done.ResultSummary;
                if ((display.Kind == ToolDisplay.ActivityKind.Command || display.IsTerminalInteraction) &&
                    ToolDisplay.TryReadTerminalResult(done.Output, out var terminalOutput, out var running))
                {
                    if (done.Success && display.IsTerminalInteraction && running && string.IsNullOrWhiteSpace(terminalOutput)) break;
                    if (!string.IsNullOrWhiteSpace(terminalOutput))
                        detail = done.Success ? terminalOutput : done.ResultSummary + "\n" + terminalOutput;
                }
                else if (display.Kind == ToolDisplay.ActivityKind.LegacyCommand && !string.IsNullOrWhiteSpace(done.Output))
                {
                    detail = done.Output;
                }

                var activity = new List<string> { ToolDisplay.RenderHeading(display, done.Success, size.Width) };
                ToolDisplay.AppendDetail(activity, detail, size.Width);
                _history.AddRange(activity);
                break;
            }

            case FileDiffEvent diff:
                _history.AddRange(RenderDiff(diff.UnifiedDiff));
                break;

            case ProviderRetryEvent retry:
                // The retry notice can arrive before the provider's reset event. Drop its
                // failed draft before inserting the notice above the live rows.
                ResetStream();
                _history.Add($"↻ retry #{retry.Attempt} on {retry.ModelRef}: {retry.Reason}".Style(Ansi.Yellow));
                break;

            case ProviderSwitchedEvent switched:
                _history.Add($"⇄ failover {switched.FromRef} → {switched.ToRef} ({switched.Reason})".Style(Ansi.Yellow));
                break;

            case UsageRecordedEvent usage:
                _latestContextTokens = Math.Max(usage.TotalInputTokens, usage.InputTokens) + usage.OutputTokens;
                _modelWithUsage = $"{usage.ProviderId}/{usage.ModelId}";
                break;

            case VerificationStartedEvent verify:
                _history.Add($"⚙ verify: {verify.Command}".Style(Ansi.Cyan));
                break;

            case VerificationCompletedEvent verified:
                _history.Add(verified.Success
                    ? "✓ verification passed".Style(Ansi.Green)
                    : $"✗ verification failed (attempt {verified.Attempt}) — repairing".Style(Ansi.Red));
                break;

            case WarningEvent warning:
                _history.Add(("⚠ " + warning.Message).Style(Ansi.Yellow));
                break;

            case ErrorEvent error:
                _history.Add(("✗ " + error.Message + (error.Detail is null ? "" : $": {error.Detail}")).Style(Ansi.Red));
                break;

            case TurnCompletedEvent completed:
                CommitExploration(size.Width);
                if (_streamBuffer.Length > 0)
                    CompleteAssistant(_streamBuffer.ToString(), size);
                ResetStream();
                if (completed.Cancelled)
                    _history.Add("• Interrupted".Style(Ansi.Yellow));
                if (_showTurnDividers)
                    _history.Add(new string('─', Math.Max(0, size.Width - 1)).Style(Ansi.Gray));
                _turnActive = false;
                _activeTools.Clear();
                break;
        }
    }

    private List<string> BuildLiveLines (TerminalSize size, int inputRows, string footer)
    {
        var lines = new List<string>();
        if (!_turnActive) return lines;

        var preview = BuildStreamPreview(size);
        var runningExploration = _activeTools.Values.Where(tool => tool.Display.IsExploration).Select(tool => tool.Display).ToArray();
        if (runningExploration.Length > 0 || _explored.Count > 0)
            lines.AddRange(ExplorationRows(runningExploration, size.Width));

        var otherTools = _activeTools.Values.Where(tool => !tool.Display.IsExploration).ToArray();
        if (otherTools.Length == 1)
        {
            lines.Add(ToolDisplay.RenderHeading(otherTools[0].Display, success: null, size.Width));
        }
        else if (otherTools.Length > 1)
        {
            lines.Add($"• Running {otherTools.Length} tools".Style(Ansi.Dim));
        }
        else if (preview.Count == 0 && runningExploration.Length == 0)
        {
            var status = _thinkingActive ? "Thinking" : _status;
            var detail = !_thinkingActive && _statusDetail.Length > 0 ? $" {_statusDetail}" : "";
            lines.Add("• ".Style(Ansi.Dim) + (status + detail).Style(Ansi.Bold) + " (Ctrl+C to interrupt)".Style(Ansi.Dim));
        }

        var rowBeforeFooter = lines.Count;
        if (inputRows == 0 && footer.Length > 0) lines.Add(footer.Style(Ansi.Dim));
        var available = Math.Max(0, size.Height - 1 - lines.Count - inputRows);
        // The leading cell spacer only goes to history together with some content.
        static int WithSpacer(int count) => count > 0 ? count + 1 : 0;
        var emitted = _history.ShowDraft(preview, available, WithSpacer(_streamMarkdown.SpillableRows),
            WithSpacer(_streamMarkdown.StableRows), palette: _palette);
        lines.InsertRange(rowBeforeFooter, preview.Skip(emitted).Take(available));
        return lines;
    }

    private void CommitExploration (int width)
    {
        if (_explored.Count == 0) return;
        _history.AddRange(ExplorationRows([], width));
        _explored.Clear();
    }

    private List<string> ExplorationRows (IReadOnlyList<ToolDisplay.Presentation> running, int width)
    {
        var label = running.Count > 0 ? "Exploring" : "Explored";
        var lines = new List<string> { "• ".Style(Ansi.Dim) + label.Style(Ansi.Bold) };
        var items = _explored.Concat(running).GroupBy(tool => tool.CompletedLabel);
        var detail = string.Join('\n', items.Select(group => group.Key + " " + string.Join(", ", group.Select(tool => tool.Target))));
        ToolDisplay.AppendDetail(lines, detail, width);
        return lines;
    }

    private string BuildFooterStatus ()
    {
        var model = _turnActive && _modelRef.Length > 0 ? _modelRef : _getActiveModel?.Invoke() ?? _modelRef;
        var context = model == _modelWithUsage && _latestContextTokens > 0 ? _getContextWindow?.Invoke() : null;
        var separator = model.IndexOf('/');
        var name = Ansi.StripControls(separator >= 0 ? model[(separator + 1)..] : model);
        if (context is not > 0) return name;
        var remaining = 1 - Math.Clamp((double)_latestContextTokens / context.Value, 0, 1);
        return $"{name} · {remaining:P0} context left";
    }

    private void CompleteAssistant (string source, TerminalSize size)
    {
        var directory = _streamDirectory ?? _getWorkingDirectory?.Invoke();
        _history.CompleteAssistant(source, AssistantCell.Render(source, AssistantWidth(size), directory, _palette), directory, _palette);
    }

    private void ResetStream ()
    {
        _history.DiscardDraft();
        _streamBuffer.Clear();
        _streamDirectory = null;
        _streamMarkdown.Reset();
        _streamRenderedLines = [];
        _streamDirty = false;
        _thinkingActive = false;
    }

    private static int AssistantWidth (TerminalSize size) => Math.Max(1, size.Width - 3);

    private IReadOnlyList<string> BuildStreamPreview (TerminalSize size)
    {
        if (_streamBuffer.Length == 0) return [];

        var width = AssistantWidth(size);
        if (_streamDirty || width != _streamWidth)
        {
            _streamRenderedLines = AssistantCell.Format(_streamMarkdown.RenderAll(_streamBuffer.ToString(), width, _streamDirectory, _palette));
            _streamWidth = width;
            _streamDirty = false;
        }

        return _streamRenderedLines;
    }

    private static IEnumerable<string> RenderDiff (string unifiedDiff)
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

    /// <summary>Waits for the renderer to draw all output queued before this call.</summary>
    public void Flush ()
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uiChanges.Writer.TryWrite(new UiChange(Drawn: drawn))) return;
        Task.WhenAny(drawn.Task, _renderTask).GetAwaiter().GetResult().GetAwaiter().GetResult();
    }

    public void Dispose ()
    {
        _resizeMonitor.Dispose();
        _subscription.Dispose();
        _uiChanges.Writer.TryComplete();
        _renderTask.GetAwaiter().GetResult();
    }
}
