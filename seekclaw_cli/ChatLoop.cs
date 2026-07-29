using SeekClaw.Cli.Ui;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;

namespace SeekClaw.Cli;

/// <summary>Interactive REPL and one-shot chat entry points, wired to the live renderer.</summary>
public sealed class ChatLoop(SeekClawRuntime runtime)
{
    private static readonly SlashCommand[] ReplCommands =
    [
        new("/model", "[provider/model]", "Show or switch the active model", SubmitsOnSelect: false),
        new("/mode", "[plan|auto|readonly|edit]", "Show or switch agent execution mode", SubmitsOnSelect: false),
        new("/cd", "<directory>", "Change working directory", SubmitsOnSelect: false),
        new("/mcp", "", "List connected MCP servers and tools", SubmitsOnSelect: true),
        new("/skills", "", "List available skills", SubmitsOnSelect: true),
        new("/doctor", "", "Run environment health diagnostics", SubmitsOnSelect: true),
        new("/clear", "", "Start a new session", SubmitsOnSelect: true),
        new("/usage", "", "Token and cost statistics", SubmitsOnSelect: true),
        new("/session", "", "Current session info", SubmitsOnSelect: true),
        new("/print", "config", "Print configuration", SubmitsOnSelect: true),
        new("/help", "", "Show available commands", SubmitsOnSelect: true),
        new("/exit", "", "Leave SeekClaw", SubmitsOnSelect: true),
    ];

    private CancellationTokenSource? _turnCts;
    private long _lastCancelPress;
    private volatile bool _exitRequested;
    private readonly List<string> _history = [];

    private string HistoryFile => Path.Combine(runtime.Workspace.SeekClawDir, "history.txt");

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
                _history.AddRange(File.ReadAllLines(HistoryFile).Where(l => l.Length > 0).TakeLast(200));
        }
        catch (IOException) { }
    }

    private void SaveHistory(string input)
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

    public async Task<int> RunOneShotAsync(string prompt, bool continueLast)
    {
        var session = ResolveSession(continueLast, null);
        using var renderer = new TerminalRenderer(runtime.Events);
        InstallCancelHandler();

        await ConnectMcpQuietlyAsync(renderer);

        _turnCts = new CancellationTokenSource();
        var result = await runtime.Agent.RunTurnAsync(session, runtime.Workspace, prompt, _turnCts.Token);
        renderer.Flush();
        return result.Error is null ? 0 : 1;
    }

    public async Task<int> RunInteractiveAsync(bool continueLast, string? resumeId)
    {
        var session = ResolveSession(continueLast, resumeId);
        using var renderer = new TerminalRenderer(runtime.Events, showTurnDividers: true);
        InstallCancelHandler();

        PrintBanner(renderer, session, continueLast || resumeId is not null);
        await ConnectMcpQuietlyAsync(renderer);
        renderer.Flush();
        LoadHistory();
        var editor = new LineEditor(
            ReplCommands,
            _history,
            () => runtime.Workspace.Config?.Mode ?? runtime.ConfigStore.Config.Agent.Mode,
            renderer.SetInputFrame,
            renderer.WriteLine);

        Task? activeTurnTask = null;

        while (!_exitRequested)
        {
            var input = editor.ReadLine();
            if (input is null) break; // Ctrl+C / Ctrl+D on an empty line, or EOF
            input = input.Trim();
            if (input.Length == 0) continue;
            SaveHistory(input);

            if (input.StartsWith('/'))
            {
                if (HandleSlashCommand(input, ref session, renderer)) break;
                continue;
            }

            // If an AI agent turn is actively running, treat input as mid-turn steering guidance!
            if (activeTurnTask is not null && !activeTurnTask.IsCompleted)
            {
                var steerMsg = ChatMessage.User($"[User Steering Instruction]: {input}");
                runtime.Sessions.Append(session, steerMsg);
                runtime.Events.Publish(new UserSteerEvent(input));
                continue;
            }

            // Start a new agent turn asynchronously so LineEditor remains responsive for user steering!
            _turnCts?.Dispose();
            _turnCts = new CancellationTokenSource();
            var currentCts = _turnCts;
            var currentPrompt = input;

            activeTurnTask = Task.Run(async () =>
            {
                try
                {
                    await runtime.Agent.RunTurnAsync(session, runtime.Workspace, currentPrompt, currentCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    renderer.Flush();
                }
            });
        }

        renderer.WriteLine();
        renderer.WriteLine("bye.".Style(Ansi.Gray));
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private AgentSession ResolveSession(bool continueLast, string? resumeId)
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

    private void InstallCancelHandler()
    {
        Console.CancelKeyPress += (_, e) =>
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
    }

    private void PrintBanner(TerminalRenderer renderer, AgentSession session, bool resumed)
    {
        var workspace = runtime.Workspace;
        string activeModel;
        try
        {
            activeModel = runtime.Providers.ResolveActive(workspace.Config).Ref;
        }
        catch (Exception)
        {
            activeModel = "not configured — run 'seekclaw switch'".Style(Ansi.Yellow);
        }

        var banner = Banner.Build(new Banner.Info(
            Model: activeModel,
            Workspace: workspace.Root,
            ProjectKinds: string.Join(", ", workspace.ProjectKinds),
            Session: session.Header.Id + (resumed ? $" · {session.Messages.Count} messages" : ""),
            Resumed: resumed));
        foreach (var line in banner)
            renderer.WriteLine(line);
    }

    private async Task ConnectMcpQuietlyAsync(TerminalRenderer renderer)
    {
        if (runtime.Mcp.LoadServerConfigs(runtime.Workspace).Count == 0) return;
        try
        {
            var statuses = await runtime.ConnectMcpAsync(CancellationToken.None);
            foreach (var status in statuses)
                renderer.WriteLine(status.Connected
                    ? $"  mcp {status.Name}: {status.ToolCount} tools".Style(Ansi.Gray)
                    : $"  mcp {status.Name}: {status.Error}".Style(Ansi.Yellow));
        }
        catch (Exception ex)
        {
            renderer.WriteLine($"  mcp: {ex.Message}".Style(Ansi.Yellow));
        }
    }

    /// <summary>Returns true when the REPL should exit.</summary>
    private bool HandleSlashCommand(string input, ref AgentSession session, TerminalRenderer renderer)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit" or "/quit" or "/q":
                return true;

            case "/clear" or "/new":
                session = runtime.Sessions.Create(runtime.Workspace);
                renderer.WriteLine($"new session {session.Header.Id}".Style(Ansi.Gray));
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
                        var profile = runtime.ConfigStore.Config.GetActiveProfile();
                        profile.Provider = model.Provider.Id;
                        profile.Model = model.Model.Id;
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

            case "/mode":
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    var targetMode = AgentModeExtensions.Parse(parts[1]);
                    if (runtime.Workspace.Config is not null)
                    {
                        runtime.Workspace.Config.Mode = targetMode.ToString().ToLowerInvariant();
                    }
                    var profile = runtime.ConfigStore.Config.GetActiveProfile();
                    profile.Mode = targetMode.ToString().ToLowerInvariant();
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

                    try
                    {
                        targetDir = Path.GetFullPath(targetDir);
                        if (Directory.Exists(targetDir))
                        {
                            Directory.SetCurrentDirectory(targetDir);
                            runtime.RefreshWorkspace(targetDir);
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
                    renderer.WriteLine($"{a.Provider}/{a.Model}  {a.TotalTokens:N0} tok  ${a.Cost:0.####}  {a.Calls} calls".Style(Ansi.Dim));
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

            case "/help":
                renderer.WriteLine("/model [ref]   show or switch the active model".Style(Ansi.Dim));
                renderer.WriteLine("/cd <dir>      change working directory".Style(Ansi.Dim));
                renderer.WriteLine("/mcp           list connected MCP servers and tools".Style(Ansi.Dim));
                renderer.WriteLine("/skills        list available skills".Style(Ansi.Dim));
                renderer.WriteLine("/doctor        run runtime health diagnostics".Style(Ansi.Dim));
                renderer.WriteLine("/clear         start a new session".Style(Ansi.Dim));
                renderer.WriteLine("/usage         token/cost statistics".Style(Ansi.Dim));
                renderer.WriteLine("/session       current session info".Style(Ansi.Dim));
                renderer.WriteLine("/print config  print configuration file path".Style(Ansi.Dim));
                renderer.WriteLine("/exit          leave".Style(Ansi.Dim));
                return false;

            default:
                renderer.WriteLine($"unknown command {parts[0]} — /help".Style(Ansi.Yellow));
                return false;
        }
    }
}
