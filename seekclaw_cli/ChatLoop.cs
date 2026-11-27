using SeekClaw.Cli.Ui;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using System.Collections.Concurrent;
using System.Text;

namespace SeekClaw.Cli;

/// <summary>Interactive REPL and one-shot chat entry points, wired to the live renderer.</summary>
public sealed class ChatLoop (SeekClawRuntime runtime)
{
    private static readonly SlashCommand[] ReplCommands =
    [
        new("/model", "[provider/model]", "Show or switch the active model", SubmitsOnSelect: false),
        new("/mode", "[plan|auto|readonly|edit]", "Show or switch agent execution mode", SubmitsOnSelect: false),
        new("/theme", "[auto|light|dark]", "Change the terminal theme", SubmitsOnSelect: true),
        new("/cd", "<directory>", "Change working directory", SubmitsOnSelect: false),
        new("/mcp", "", "List connected MCP servers and tools", SubmitsOnSelect: true),
        new("/skills", "", "List available skills", SubmitsOnSelect: true),
        new("/doctor", "", "Run environment health diagnostics", SubmitsOnSelect: true),
        new("/clear", "", "Start a new session", SubmitsOnSelect: true),
        new("/usage", "", "Token and latency statistics", SubmitsOnSelect: true),
        new("/session", "", "Current session info", SubmitsOnSelect: true),
        new("/goal", "[resume]", "Show or resume the persistent conversation goal", SubmitsOnSelect: false),
        new("/copy", "", "Copy the last assistant answer to clipboard", SubmitsOnSelect: true),
        new("/print", "config", "Print configuration", SubmitsOnSelect: true),
        new("/help", "", "Show available commands", SubmitsOnSelect: true),
        new("/exit", "", "Leave SeekClaw", SubmitsOnSelect: true),
    ];

    private CancellationTokenSource? _turnCts;
    private long _lastCancelPress;
    private volatile bool _exitRequested;
    private readonly List<string> _history = [];
    private readonly CliThemeSettings _themeSettings = new();
    private CliTheme _theme = CliTheme.Auto;

    private string HistoryFile => Path.Combine(runtime.Workspace.SeekClawDir, "history.txt");

    private void LoadHistory ()
    {
        try
        {
            if (File.Exists(HistoryFile))
                _history.AddRange(File.ReadAllLines(HistoryFile).Where(l => l.Length > 0).TakeLast(200));
        }
        catch (IOException) { }
    }

    private void SaveHistory (string input)
    {
        if (_history.Count == 0 || _history[^1] != input)
            _history.Add(input);
        try
        {
            Directory.CreateDirectory(runtime.Workspace.SeekClawDir);
            File.WriteAllLines(HistoryFile, _history.TakeLast(200));
        }
        catch (IOException) { }
    }

    public async Task<int> RunOneShotAsync (
        string prompt, bool continueLast, string? resumeId = null, CancellationToken ct = default)
    {
        var session = ResolveSession(continueLast, resumeId);
        using var renderer = new TerminalRenderer(
            runtime.Events,
            getModeText: GetModeDisplay,
            getContextWindow: GetActiveContextWindow,
            getActiveModel: GetActiveModelRef,
            getWorkingDirectory: () => runtime.Workspace.Root,
            palette: ResolvePalette(_themeSettings.Load() ?? CliTheme.Auto));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, prompt, cancel.Token);
            if (result.Maintenance is { } maintenance)
                await maintenance.ConfigureAwait(false);
            return result.Cancelled ? 130 : result.Error is null ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return 130;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    internal async Task<int> RunPlainAsync (
        string prompt, bool continueLast, string? resumeId, TextReader? input,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            // All piped lines form one prompt. A positional prompt takes precedence.
            if (prompt.Length == 0 && input is not null)
                prompt = await CliIo.ReadPipedInputAsync(input, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                await error.WriteLineAsync("A prompt or piped input is required.").ConfigureAwait(false);
                return 2;
            }

            var session = ResolveSession(continueLast, resumeId);
            using var subscription = runtime.Events.Subscribe();
            var pump = CliIo.WriteProgressAsync(subscription, error);
            AgentTurnResult result;
            try
            {
                result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, prompt, ct).ConfigureAwait(false);
                if (result.Maintenance is { } maintenance)
                    await maintenance.ConfigureAwait(false);
            }
            finally
            {
                subscription.Dispose();
                await pump.ConfigureAwait(false);
            }

            if (result.Cancelled) return 130;
            if (result.Error is { } failure)
            {
                await error.WriteLineAsync(Ansi.StripControls(failure)).ConfigureAwait(false);
                return 1;
            }

            await output.WriteLineAsync(Ansi.StripControls(result.Text)).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync(Ansi.StripControls(ex.Message)).ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(Ansi.StripControls(ex.Message)).ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> RunInteractiveAsync (bool continueLast, string? resumeId, CancellationToken ct = default)
    {
        var savedTheme = _themeSettings.Load();
        _theme = savedTheme ?? CliTheme.Auto;
        using var renderer = new TerminalRenderer(
            runtime.Events,
            getModeText: GetModeDisplay,
            getContextWindow: GetActiveContextWindow,
            getActiveModel: GetActiveModelRef,
            getWorkingDirectory: () => runtime.Workspace.Root,
            palette: ResolvePalette(_theme));
        if (savedTheme is null)
        {
            var selected = ThemePicker.Read(renderer, CliTheme.Auto, firstRun: true, cancellationToken: ct);
            if (selected is not { } theme) return 130;
            SaveTheme(theme, renderer);
        }

        var session = ResolveSession(continueLast, resumeId);
        var sessionState = new InteractiveSessionState(session);
        PrintBanner(renderer, session, continueLast || resumeId is not null);
        renderer.Flush();
        LoadHistory();
        var editor = new LineEditor(
            ReplCommands,
            _history,
            () => runtime.Workspace.Config?.Mode ?? runtime.ConfigStore.Config.Agent.Mode,
            renderer.SetInputFrame,
            renderer.SubmitInput,
            getPalette: () => renderer.Palette);

        var pendingApprovals = new ConcurrentDictionary<string, PendingCliApproval>(StringComparer.Ordinal);
        runtime.Approvals.SetHandler(async (request, ct) =>
        {
            var key = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<ToolApprovalDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingCliApproval(key, request, completion);
            if (!pendingApprovals.TryAdd(key, pending))
                return ToolApprovalDecision.Deny("A duplicate approval request is already pending.");

            renderer.WriteLine($"approval required: {request.ToolName} · {request.ArgumentSummary}".Style(Ansi.Yellow));
            renderer.WriteLine("  type y to approve or n to deny".Style(Ansi.Dim));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ToolApprovalDecision.Deny("Timed out waiting for user approval.");
            }
            finally
            {
                pendingApprovals.TryRemove(key, out _);
            }
        });

        Task? activeTurnTask = null;
        AgentSteeringQueue? activeSteering = null;

        async Task FinishActiveTurnAsync (Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { renderer.WriteLine($"error: {ex.Message}".Style(Ansi.Red)); }
        }

        var onCancel = CreateCancelHandler();
        Console.CancelKeyPress += onCancel;
        try
        {
            while (!_exitRequested)
            {
                if (activeTurnTask is { IsCompleted: true })
                {
                    await FinishActiveTurnAsync(activeTurnTask).ConfigureAwait(false);
                    activeTurnTask = null;
                    activeSteering = null;
                    _turnCts?.Dispose();
                    _turnCts = null;
                }

                var input = editor.ReadLine();
                if (input is null)
                {
                    // Only leave when idle. While a turn is running, treat the key as
                    // cancellation and return to the prompt instead of exiting with work alive.
                    if (activeTurnTask is { IsCompleted: false })
                    {
                        _turnCts?.Cancel();
                        await FinishActiveTurnAsync(activeTurnTask).ConfigureAwait(false);
                        activeTurnTask = null;
                        activeSteering = null;
                        _turnCts?.Dispose();
                        _turnCts = null;
                        continue;
                    }
                    break;
                }
                input = input.Trim();
                if (input.Length == 0) continue;

                var pendingApproval = pendingApprovals.Values.FirstOrDefault(
                    item => !item.Completion.Task.IsCompleted);
                if (pendingApproval is not null)
                {
                    if (IsExitCommand(input)) break;
                    if (TryParseApprovalInput(input, out var approved))
                    {
                        if (pendingApprovals.TryRemove(pendingApproval.Key, out var removed))
                        {
                            removed.Completion.TrySetResult(approved
                                ? ToolApprovalDecision.Allow
                                : ToolApprovalDecision.Deny("The user denied this tool call."));
                        }
                    }
                    else
                    {
                        renderer.WriteLine("an approval is pending: type y to approve or n to deny.".Style(Ansi.Yellow));
                    }
                    continue;
                }

                SaveHistory(input);

                var resumeGoal = input.Equals("/goal resume", StringComparison.OrdinalIgnoreCase);
                if (resumeGoal && activeTurnTask is { IsCompleted: false })
                {
                    renderer.WriteLine("Wait for the current turn to finish before resuming a goal.".Style(Ansi.Yellow));
                    continue;
                }

                if (input.StartsWith('/') && !resumeGoal)
                {
                    var shouldExit = await HandleSlashCommandAsync(input, sessionState, renderer).ConfigureAwait(false);
                    session = sessionState.Session;
                    if (shouldExit) break;
                    continue;
                }

                // Guidance must go through the Agent steering queue. Appending it directly
                // can insert a user message between assistant tool_calls and tool results.
                if (activeTurnTask is not null && !activeTurnTask.IsCompleted)
                {
                    var steerMsg = ChatMessage.User($"[User Steering Instruction]: {input}");
                    if (activeSteering?.TryEnqueue(steerMsg) == true) continue;

                    await FinishActiveTurnAsync(activeTurnTask).ConfigureAwait(false);
                    activeTurnTask = null;
                    activeSteering = null;
                    _turnCts?.Dispose();
                    _turnCts = null;
                }

                // Start a new agent turn asynchronously so LineEditor remains responsive for user steering!
                _turnCts = new CancellationTokenSource();
                var currentCts = _turnCts;
                var currentPrompt = resumeGoal ? "" : input;
                var currentSession = session;
                var currentWorkspace = runtime.Workspace;
                var steering = new AgentSteeringQueue();
                activeSteering = steering;

                activeTurnTask = Task.Run(async () =>
                {
                    try
                    {
                        var result = await runtime.Agent.RunTurnAsync(
                            currentSession, currentWorkspace, currentPrompt, currentCts.Token,
                            steering: steering, resumeGoal: resumeGoal).ConfigureAwait(false);
                        // Memory distillation runs after the turn is reported done; keep it
                        // inside the turn task so the next prompt sees fresh memory.
                        if (result.Maintenance is { } maintenance)
                            await maintenance.ConfigureAwait(false);
                    }
                    finally
                    {
                        renderer.Flush();
                    }
                });
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            runtime.Approvals.ClearHandler();
            if (activeTurnTask is not null)
            {
                if (!activeTurnTask.IsCompleted) _turnCts?.Cancel();
                await FinishActiveTurnAsync(activeTurnTask).ConfigureAwait(false);
            }
            _turnCts?.Dispose();
            _turnCts = null;
        }

        renderer.WriteLine();
        renderer.WriteLine("bye.".Style(Ansi.Gray));
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private AgentSession ResolveSession (bool continueLast, string? resumeId)
    {
        if (resumeId is not null)
            return runtime.Sessions.Load(runtime.Workspace, resumeId)
                   ?? throw new InvalidOperationException($"Session '{resumeId}' not found in {runtime.Workspace.SessionsDir}.");
        if (continueLast)
        {
            var latest = runtime.Sessions.LoadLatest(runtime.Workspace);
            if (latest is not null) return latest;
        }
        return runtime.Sessions.Create(runtime.Workspace);
    }

    private string GetModeDisplay () =>
        AgentModeExtensions.Parse(runtime.Workspace.Config?.Mode ?? runtime.ConfigStore.Config.Agent.Mode)
            .ToDisplayString();

    private string? GetActiveModelRef ()
    {
        try { return runtime.Providers.ResolveActive(runtime.Workspace.Config).Ref; }
        catch (Exception) { return null; }
    }

    private int? GetActiveContextWindow ()
    {
        try { return runtime.Providers.ResolveActive(runtime.Workspace.Config).Model.EffectiveContextWindow; }
        catch (Exception) { return null; }
    }

    private ConsoleCancelEventHandler CreateCancelHandler () => (_, e) =>
        {
            e.Cancel = true;
            var now = Environment.TickCount64;
            var doublePress = now - _lastCancelPress < 2000;
            _lastCancelPress = now;

            if (_turnCts is { IsCancellationRequested: false } cts && !doublePress)
            {
                cts.Cancel(); // first press: cancel the current task
            }
            else
            {
                _exitRequested = true; // second press (or idle): exit
                e.Cancel = false;
            }
        };

    private static CliPalette ResolvePalette(CliTheme theme) => theme switch
    {
        CliTheme.Light => CliPalette.Light,
        CliTheme.Dark => CliPalette.Dark,
        _ => TerminalBackground.Detect(),
    };

    private void SaveTheme(CliTheme theme, TerminalRenderer renderer)
    {
        _theme = theme;
        renderer.SetPalette(ResolvePalette(theme));
        if (!_themeSettings.TrySave(theme))
            renderer.WriteLine("Could not save the theme. It applies to this run.".Style(Ansi.Yellow));
    }

    private void PrintBanner (TerminalRenderer renderer, AgentSession session, bool resumed)
    {
        var banner = Banner.Build(new Banner.Info(
            Workspace: runtime.Workspace.Root,
            Session: session.Header.Id + (resumed ? $" · {session.Messages.Count} messages" : ""),
            Resumed: resumed));
        foreach (var line in banner)
            renderer.WriteLine(line);
    }

    /// <summary>Returns true when the REPL should exit.</summary>
    private async Task<bool> HandleSlashCommandAsync (string input, InteractiveSessionState sessionState, TerminalRenderer renderer)
    {
        var session = sessionState.Session;
        var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit" or "/quit" or "/q":
                return true;

            case "/copy":
            {
                var last = session.Messages.LastOrDefault(message =>
                    message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text));
                if (last is null)
                {
                    renderer.WriteLine("no assistant answer to copy.".Style(Ansi.Gray));
                }
                else
                {
                    CopyViaOsc52(last.Text);
                    renderer.WriteLine("copied last answer to clipboard.".Style(Ansi.Gray));
                }
                return false;
            }

            case "/clear" or "/new":
                sessionState.Session = runtime.Sessions.Create(runtime.Workspace);
                renderer.WriteLine($"new session {sessionState.Session.Header.Id}".Style(Ansi.Gray));
                return false;

            case "/model":
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    var model = runtime.Models.Resolve(parts[1]);
                    if (model is null)
                    {
                        renderer.WriteLine($"unknown model: {parts[1]}".Style(Ansi.Red));
                    }
                    else
                    {
                        runtime.ConfigStore.Config.Provider = model.Provider.Id;
                        runtime.ConfigStore.Config.Model = model.Model.Id;
                        runtime.ConfigStore.Save();
                        renderer.WriteLine($"model → {model.Ref}".Style(Ansi.Green));
                    }
                }
                else
                {
                    try { renderer.WriteLine($"active model: {runtime.Providers.ResolveActive(runtime.Workspace.Config).Ref}"); }
                    catch (Exception ex) { renderer.WriteLine(ex.Message.Style(Ansi.Red)); }
                }
                return false;

            case "/theme":
            {
                var hasArgument = parts.Length > 1 && parts[1].Length > 0;
                var choice = hasArgument ? CliThemeSettings.Parse(parts[1]) : ThemePicker.Read(renderer, _theme);
                if (choice is { } theme)
                {
                    SaveTheme(theme, renderer);
                    renderer.WriteLine($"Theme set to {theme}.");
                }
                else if (hasArgument)
                {
                    renderer.WriteLine("Usage: /theme [auto|light|dark]".Style(Ansi.Yellow));
                }
                return false;
            }

            case "/mode":
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    var targetMode = AgentModeExtensions.Parse(parts[1]);
                    if (runtime.Workspace.Config is not null)
                    {
                        runtime.Workspace.Config.Mode = targetMode.ToString().ToLowerInvariant();
                    }
                    runtime.ConfigStore.Config.Agent.Mode = targetMode.ToString().ToLowerInvariant();
                    runtime.ConfigStore.Save();
                    renderer.WriteLine($"mode → {targetMode.ToDisplayString()}".Style(Ansi.Green));
                }
                else
                {
                    var rawMode = runtime.Workspace.Config?.Mode ?? runtime.ConfigStore.Config.Agent.Mode;
                    var currentMode = AgentModeExtensions.Parse(rawMode);
                    renderer.WriteLine($"current mode: {currentMode.ToDisplayString()}".Style(Ansi.Cyan));
                    renderer.WriteLine("available modes: plan | readonly | edit | auto".Style(Ansi.Gray));
                }
                return false;

            case "/cd":
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    var targetDir = parts[1];
                    // Support ~ for home directory
                    if (targetDir.StartsWith("~"))
                        targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), targetDir[1..]);

                    // Resolve relative paths
                    if (!Path.IsPathRooted(targetDir))
                        targetDir = Path.Combine(runtime.Workspace.Root, targetDir);

                    var previousDirectory = Directory.GetCurrentDirectory();
                    try
                    {
                        targetDir = Path.GetFullPath(targetDir);
                        if (Directory.Exists(targetDir))
                        {
                            Directory.SetCurrentDirectory(targetDir);
                            try { runtime.RefreshWorkspace(targetDir); }
                            catch
                            {
                                Directory.SetCurrentDirectory(previousDirectory);
                                throw;
                            }
                            await runtime.Mcp.PrepareAsync(runtime.Workspace, CancellationToken.None).ConfigureAwait(false);
                            renderer.WriteLine($"cd → {runtime.Workspace.Root}".Style(Ansi.Green));
                        }
                        else
                        {
                            renderer.WriteLine($"directory not found: {targetDir}".Style(Ansi.Red));
                        }
                    }
                    catch (Exception ex)
                    {
                        renderer.WriteLine($"error: {ex.Message}".Style(Ansi.Red));
                    }
                }
                else
                {
                    renderer.WriteLine(runtime.Workspace.Root);
                }
                return false;

            case "/usage":
            {
                var aggregates = runtime.Usage.Aggregate();
                if (aggregates.Count == 0) { renderer.WriteLine("no usage recorded yet.".Style(Ansi.Gray)); return false; }
                foreach (var a in aggregates.Take(10))
                    renderer.WriteLine($"{a.Provider}/{a.Model}  {a.TotalTokens:N0} tok  {a.Calls} calls  {a.AvgLatencyMs:0}ms".Style(Ansi.Dim));
                return false;
            }

            case "/mcp":
            {
                var statuses = runtime.Mcp.Status;
                if (statuses.Count == 0)
                {
                    renderer.WriteLine("no mcp servers configured.".Style(Ansi.Gray));
                }
                else
                {
                    foreach (var s in statuses)
                    {
                        var info = s.Connected
                            ? $"{s.Name} ({s.Transport}): {s.ToolCount} tools".Style(Ansi.Green)
                            : $"{s.Name} ({s.Transport}): {s.Error ?? "disconnected"}".Style(Ansi.Red);
                        renderer.WriteLine(info);
                    }
                }
                return false;
            }

            case "/skills":
            {
                var skills = runtime.Skills.Discover(runtime.Workspace);
                if (skills.Count == 0)
                {
                    renderer.WriteLine("no skills found.".Style(Ansi.Gray));
                }
                else
                {
                    foreach (var s in skills)
                    {
                        var status = s.Enabled ? "[enabled]".Style(Ansi.Green) : "[disabled]".Style(Ansi.Gray);
                        renderer.WriteLine($"{s.Name} {status} · {s.Directory}");
                    }
                }
                return false;
            }

            case "/doctor":
            {
                renderer.WriteLine("Running SeekClaw environment diagnostics...".Style(Ansi.Cyan));
                var checks = runtime.Health.RunChecks(runtime.Workspace);
                foreach (var check in checks)
                {
                    var status = check.Ok ? "[OK]".Style(Ansi.Green) : "[FAIL]".Style(Ansi.Red);
                    renderer.WriteLine($"{status} {check.Name}: {check.Detail}");
                }
                return false;
            }

            case "/print":
                if (parts.Length > 1 && parts[1].Equals("config", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.WriteLine(SeekClawPaths.ConfigFile);
                }
                else
                {
                    renderer.WriteLine("usage: /print config".Style(Ansi.Dim));
                }
                return false;

            case "/session":
                renderer.WriteLine($"session {session.Header.Id} · {session.Messages.Count} messages · {session.FilePath}".Style(Ansi.Dim));
                return false;

            case "/goal":
            {
                var goal = runtime.Goals.Get(runtime.Workspace, session.Header.Id);
                if (goal is null)
                {
                    renderer.WriteLine("No saved goal. Ask SeekClaw to start a persistent goal for this conversation.");
                    return false;
                }
                renderer.WriteLine($"{goal.Objective} ({goal.Status})");
                renderer.WriteLine($"Completion criteria: {goal.Criteria}");
                if (goal.Progress is not null) renderer.WriteLine($"Progress: {goal.Progress}");
                if (goal.PauseReason is not null) renderer.WriteLine($"Paused: {goal.PauseReason}");
                return false;
            }

            case "/help":
                renderer.WriteLine("/model [ref]   show or switch the active model".Style(Ansi.Dim));
                renderer.WriteLine("/theme         choose Auto, Light, or Dark".Style(Ansi.Dim));
                renderer.WriteLine("/cd <dir>      change working directory".Style(Ansi.Dim));
                renderer.WriteLine("/mcp           list connected MCP servers and tools".Style(Ansi.Dim));
                renderer.WriteLine("/skills        list available skills".Style(Ansi.Dim));
                renderer.WriteLine("/doctor        run runtime health diagnostics".Style(Ansi.Dim));
                renderer.WriteLine("/clear         start a new session".Style(Ansi.Dim));
                renderer.WriteLine("/usage         token/latency statistics".Style(Ansi.Dim));
                renderer.WriteLine("/session       current session info".Style(Ansi.Dim));
                renderer.WriteLine("/goal [resume] show or resume the conversation goal".Style(Ansi.Dim));
                renderer.WriteLine("/copy          copy last assistant answer".Style(Ansi.Dim));
                renderer.WriteLine("/print config  print configuration file path".Style(Ansi.Dim));
                renderer.WriteLine("/exit          leave".Style(Ansi.Dim));
                return false;

            default:
                renderer.WriteLine($"unknown command {parts[0]} — /help".Style(Ansi.Yellow));
                return false;
        }
    }

    private static void CopyViaOsc52 (string text)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Out.Write($"\x1b]52;c;{payload}\x1b\\");
        Console.Out.Flush();
    }

    private static bool IsExitCommand (string input) =>
        input.Equals("/exit", StringComparison.OrdinalIgnoreCase)
        || input.Equals("/quit", StringComparison.OrdinalIgnoreCase)
        || input.Equals("/q", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseApprovalInput (string input, out bool approved)
    {
        switch (input.Trim().ToLowerInvariant())
        {
            case "y" or "yes" or "approve" or "allow":
                approved = true;
                return true;
            case "n" or "no" or "deny" or "reject":
                approved = false;
                return true;
            default:
                approved = false;
                return false;
        }
    }

    private sealed record PendingCliApproval (
        string Key,
        ToolApprovalRequest Request,
        TaskCompletionSource<ToolApprovalDecision> Completion);

    private sealed class InteractiveSessionState (AgentSession session)
    {
        public AgentSession Session { get; set; } = session;
    }
}
