using SeekClaw.Cli.Ui;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Sessions;

namespace SeekClaw.Cli;

/// <summary>Interactive REPL and one-shot chat entry points, wired to the live renderer.</summary>
public sealed class ChatLoop(SeekClawRuntime runtime)
{
    private static readonly SlashCommand[] ReplCommands =
    [
        new("/model", "[provider/model]", "Show or switch the active model", SubmitsOnSelect: false),
        new("/clear", "", "Start a new session", SubmitsOnSelect: true),
        new("/usage", "", "Token and cost statistics", SubmitsOnSelect: true),
        new("/session", "", "Current session info", SubmitsOnSelect: true),
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
        using var renderer = new TerminalRenderer(runtime.Events);
        InstallCancelHandler();

        PrintBanner(renderer, session, continueLast || resumeId is not null);
        await ConnectMcpQuietlyAsync(renderer);
        renderer.Flush();
        LoadHistory();
        var editor = new LineEditor(ReplCommands, _history);

        while (!_exitRequested)
        {
            Console.Write("\n");
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

            _turnCts = new CancellationTokenSource();
            try
            {
                await runtime.Agent.RunTurnAsync(session, runtime.Workspace, input, _turnCts.Token);
            }
            finally
            {
                _turnCts.Dispose();
                _turnCts = null;
            }
            renderer.Flush();
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

            case "/usage":
            {
                var aggregates = runtime.Usage.Aggregate();
                if (aggregates.Count == 0) { renderer.WriteLine("no usage recorded yet.".Style(Ansi.Gray)); return false; }
                foreach (var a in aggregates.Take(10))
                    renderer.WriteLine($"{a.Provider}/{a.Model}  {a.TotalTokens:N0} tok  ${a.Cost:0.####}  {a.Calls} calls".Style(Ansi.Dim));
                return false;
            }

            case "/session":
                renderer.WriteLine($"session {session.Header.Id} · {session.Messages.Count} messages · {session.FilePath}".Style(Ansi.Dim));
                return false;

            case "/help":
                renderer.WriteLine("/model [ref]   show or switch the active model".Style(Ansi.Dim));
                renderer.WriteLine("/clear         start a new session".Style(Ansi.Dim));
                renderer.WriteLine("/usage         token/cost statistics".Style(Ansi.Dim));
                renderer.WriteLine("/session       current session info".Style(Ansi.Dim));
                renderer.WriteLine("/exit          leave".Style(Ansi.Dim));
                return false;

            default:
                renderer.WriteLine($"unknown command {parts[0]} — /help".Style(Ansi.Yellow));
                return false;
        }
    }
}
