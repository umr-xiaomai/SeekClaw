using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Daemon;

/// <summary>
/// Exposes the runtime over a Windows named pipe or Unix domain socket using
/// newline-delimited JSON messages. Responses keep the legacy event envelope:
/// {"id":1,"event":"result","data":"..."}.
/// </summary>
public sealed class DaemonServer
{
    public const string PipeName = "seekclaw";
    public const string ProtocolVersion = "2.0";
    public static string SocketPath => Path.Combine(SeekClawPaths.Home, "daemon.sock");

    private readonly SeekClawRuntime _runtime;
    private readonly DaemonAdminApi _admin;
    private readonly WorkspaceInfo _globalWorkspace;
    private readonly Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>> _runTurn;
    private readonly CancellationTokenSource _shutdown = new();

    // SeekClawRuntime currently owns one mutable workspace and one global event bus.
    // A daemon therefore permits one turn or workspace mutation at a time.
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);

    public DaemonServer(SeekClawRuntime runtime)
        : this(runtime, runtime.Agent.RunTurnAsync, runtime.Workspaces.CreateGlobal())
    {
    }

    internal DaemonServer(
        SeekClawRuntime runtime,
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>> runTurn)
        : this(runtime, runTurn, runtime.Workspaces.CreateGlobal())
    {
    }

    internal DaemonServer(
        SeekClawRuntime runtime,
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>> runTurn,
        WorkspaceInfo globalWorkspace)
    {
        _runtime = runtime;
        _globalWorkspace = globalWorkspace;
        _admin = new DaemonAdminApi(runtime, globalWorkspace);
        _runTurn = runTurn;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var runCt = linkedCts.Token;
        while (!runCt.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    await ServeNamedPipeAsync(runCt).ConfigureAwait(false);
                else
                    await ServeUnixSocketAsync(runCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(100, runCt).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeNamedPipeAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _ = Task.Run(() => RunPipeConnectionAsync(pipe, ct));
    }

    private async Task ServeUnixSocketAsync(CancellationToken ct)
    {
        if (File.Exists(SocketPath)) File.Delete(SocketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(10);

        var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
        _ = Task.Run(() => RunSocketConnectionAsync(socket, ct));
    }

    private async Task RunPipeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try { await HandleConnectionAsync(pipe, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine($"Daemon connection failed: {ex.Message}"); }
        }
    }

    private async Task RunSocketConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            try { await HandleConnectionAsync(stream, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.Error.WriteLine($"Daemon connection failed: {ex.Message}"); }
        }
    }

    internal async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var writerGate = new SemaphoreSlim(1, 1);

        AgentSession? session = null;
        Task? activeTurn = null;
        CancellationTokenSource? activeTurnCts = null;
        long activeTurnId = 0;

        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;
                try { request = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { request = null; }

                if (request is null)
                {
                    await WriteAsync(writer, writerGate, 0, "error", "Invalid JSON request", ct).ConfigureAwait(false);
                    continue;
                }

                long id;
                string method;
                try
                {
                    id = request["id"]?.GetValue<long>() ?? 0;
                    method = request["method"]?.GetValue<string>() ?? "";
                }
                catch (InvalidOperationException)
                {
                    await WriteAsync(writer, writerGate, 0, "error", "Request id and method have invalid types", ct).ConfigureAwait(false);
                    continue;
                }

                if (activeTurn is { IsCompleted: true })
                {
                    await ObserveAsync(activeTurn).ConfigureAwait(false);
                    activeTurnCts?.Dispose();
                    activeTurn = null;
                    activeTurnCts = null;
                    activeTurnId = 0;
                }

                switch (method)
                {
                    case "ping":
                        await WriteAsync(writer, writerGate, id, "pong", "", ct).ConfigureAwait(false);
                        break;

                    case "protocol.info":
                        await WriteAsync(writer, writerGate, id, "result", ProtocolInfoJson(), ct).ConfigureAwait(false);
                        break;

                    case "workspace.init":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.InitializeWorkspace()), ct).ConfigureAwait(false);
                        break;

                    case "chat":
                    case "agent.runTurn":
                    case "agent/runTurn":
                    {
                        var message = request["params"]?["message"]?.GetValue<string>()
                                      ?? request["params"]?["prompt"]?.GetValue<string>()
                                      ?? "";
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "params.message is required", ct).ConfigureAwait(false);
                            break;
                        }
                        if (activeTurn is not null)
                        {
                            await WriteAsync(writer, writerGate, id, "error", $"Agent is busy with request {activeTurnId}", ct).ConfigureAwait(false);
                            break;
                        }
                        if (!_runtimeGate.Wait(0))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "Runtime is busy with another request", ct).ConfigureAwait(false);
                            break;
                        }

                        var workspace = request["params"]?["global"]?.GetValue<bool?>() == true
                            ? _globalWorkspace
                            : _runtime.Workspace;
                        if (session is not null && !SessionBelongsTo(session, workspace))
                        {
                            session = null;
                        }
                        session ??= _runtime.Sessions.LoadLatest(workspace)
                                    ?? _runtime.Sessions.Create(workspace);

                        activeTurnId = id;
                        activeTurnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        activeTurn = RunTurnAsync(
                            session, workspace, message, id, writer, writerGate,
                            activeTurnCts.Token, ct);
                        break;
                    }

                    case "agent.cancel":
                    {
                        if (activeTurn is null || activeTurnCts is null)
                        {
                            await WriteAsync(writer, writerGate, id, "result", "no active turn", ct).ConfigureAwait(false);
                            break;
                        }

                        var requestedId = request["params"]?["requestId"]?.GetValue<long?>();
                        if (requestedId.HasValue && requestedId.Value != activeTurnId)
                        {
                            await WriteAsync(writer, writerGate, id, "error", $"Request {requestedId.Value} is not active", ct).ConfigureAwait(false);
                            break;
                        }

                        activeTurnCts.Cancel();
                        await WriteAsync(writer, writerGate, id, "result", $"cancellation requested for {activeTurnId}", ct).ConfigureAwait(false);
                        break;
                    }

                    case "workspace.get":
                        await WriteAsync(writer, writerGate, id, "result", WorkspaceJson(_runtime.Workspace), ct).ConfigureAwait(false);
                        break;

                    case "workspace.open":
                    {
                        var path = request["params"]?["path"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "params.path is required", ct).ConfigureAwait(false);
                            break;
                        }

                        string fullPath;
                        try { fullPath = Path.GetFullPath(path); }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                        {
                            await WriteAsync(writer, writerGate, id, "error", $"Invalid workspace path: {ex.Message}", ct).ConfigureAwait(false);
                            break;
                        }

                        if (!Directory.Exists(fullPath))
                        {
                            await WriteAsync(writer, writerGate, id, "error", $"Workspace directory not found: {fullPath}", ct).ConfigureAwait(false);
                            break;
                        }
                        if (!_runtimeGate.Wait(0))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "Cannot switch workspace while the runtime is busy", ct).ConfigureAwait(false);
                            break;
                        }

                        try
                        {
                            _runtime.RefreshWorkspace(fullPath);
                            session = null;
                            await _runtime.ConnectMcpAsync(ct).ConfigureAwait(false);
                            await WriteAsync(writer, writerGate, id, "result", WorkspaceJson(_runtime.Workspace), ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _runtimeGate.Release();
                        }
                        break;
                    }

                    case "agent.mode.get":
                        await WriteAsync(writer, writerGate, id, "result", CurrentMode(), ct).ConfigureAwait(false);
                        break;

                    case "agent.mode.switch":
                    {
                        var rawMode = request["params"]?["mode"]?.GetValue<string>();
                        if (!TryNormalizeMode(rawMode, out var mode))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "params.mode must be one of: plan, readonly, edit, auto", ct).ConfigureAwait(false);
                            break;
                        }
                        if (!_runtimeGate.Wait(0))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "Cannot switch mode while the runtime is busy", ct).ConfigureAwait(false);
                            break;
                        }

                        try
                        {
                            SaveMode(mode);
                            await WriteAsync(writer, writerGate, id, "result", mode, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _runtimeGate.Release();
                        }
                        break;
                    }

                    case "profile.list":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.ListProfiles()), ct).ConfigureAwait(false);
                        break;

                    case "profile.upsert":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.UpsertProfile(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "profile.use":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.UseProfile(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "profile.remove":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.RemoveProfile(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "provider.list":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.ListProviders()), ct).ConfigureAwait(false);
                        break;

                    case "provider.upsert":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.UpsertProvider(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "provider.use":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.UseProvider(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "provider.remove":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.RemoveProvider(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "provider.test":
                        await RunAdminAsync(writer, writerGate, id, false,
                            token => _admin.TestProvidersAsync(Params(request), token), ct).ConfigureAwait(false);
                        break;

                    case "model.catalog":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.ModelCatalog()), ct).ConfigureAwait(false);
                        break;

                    case "model.test":
                        await RunAdminAsync(writer, writerGate, id, false,
                            token => _admin.TestModelAsync(Params(request), token), ct).ConfigureAwait(false);
                        break;

                    case "mcp.list":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.ListMcpServers()), ct).ConfigureAwait(false);
                        break;

                    case "mcp.upsert":
                        await RunAdminAsync(writer, writerGate, id, true,
                            token => _admin.UpsertMcpServerAsync(Params(request), token), ct).ConfigureAwait(false);
                        break;

                    case "mcp.remove":
                        await RunAdminAsync(writer, writerGate, id, true,
                            token => _admin.RemoveMcpServerAsync(Params(request), token), ct).ConfigureAwait(false);
                        break;

                    case "mcp.reload":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _admin.ReloadMcpAsync, ct).ConfigureAwait(false);
                        break;

                    case "skill.list":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.ListSkills()), ct).ConfigureAwait(false);
                        break;

                    case "skill.toggle":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.ToggleSkill(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "usage.get":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.Usage(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "doctor.run":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _admin.DoctorAsync, ct).ConfigureAwait(false);
                        break;

                    case "session.list":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.ListSessions(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "session.get":
                        await RunAdminAsync(writer, writerGate, id, false,
                            _ => Task.FromResult(_admin.GetSession(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "session.update":
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.UpdateSession(Params(request))), ct).ConfigureAwait(false);
                        break;

                    case "session.archive":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (session?.Header.Id == sessionId)
                        {
                            session = null;
                        }
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.ArchiveSession(Params(request))), ct).ConfigureAwait(false);
                        break;
                    }

                    case "session.delete":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (session?.Header.Id == sessionId)
                        {
                            session = null;
                        }
                        await RunAdminAsync(writer, writerGate, id, true,
                            _ => Task.FromResult(_admin.DeleteSession(Params(request))), ct).ConfigureAwait(false);
                        break;
                    }

                    case "session.resume":
                    {
                        if (activeTurn is not null)
                        {
                            await WriteAsync(writer, writerGate, id, "error", "Cannot resume a session while an agent turn is active", ct).ConfigureAwait(false);
                            break;
                        }
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "params.id is required", ct).ConfigureAwait(false);
                            break;
                        }
                        var workspace = request["params"]?["global"]?.GetValue<bool?>() == true
                            ? _globalWorkspace
                            : _runtime.Workspace;
                        var loaded = _runtime.Sessions.Load(workspace, sessionId);
                        if (loaded is null)
                            await WriteAsync(writer, writerGate, id, "error", $"Session {sessionId} not found", ct).ConfigureAwait(false);
                        else
                        {
                            session = loaded;
                            await WriteAsync(writer, writerGate, id, "result", $"resumed {session.Header.Id}", ct).ConfigureAwait(false);
                        }
                        break;
                    }

                    case "session.new":
                    {
                        if (activeTurn is not null)
                        {
                            await WriteAsync(writer, writerGate, id, "error", "Cannot create a session while an agent turn is active", ct).ConfigureAwait(false);
                            break;
                        }
                        var workspace = request["params"]?["global"]?.GetValue<bool?>() == true
                            ? _globalWorkspace
                            : _runtime.Workspace;
                        session = _runtime.Sessions.Create(workspace);
                        await WriteAsync(writer, writerGate, id, "result", session.Header.Id, ct).ConfigureAwait(false);
                        break;
                    }

                    case "model.list":
                    {
                        var models = _runtime.Models.All().Select(m => m.Ref).ToList();
                        var json = JsonSerializer.Serialize(models, SeekClawJsonContext.Default.ListString);
                        await WriteAsync(writer, writerGate, id, "result", json, ct).ConfigureAwait(false);
                        break;
                    }

                    case "model.switch":
                    {
                        var modelRef = request["params"]?["model"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(modelRef))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "params.model is required", ct).ConfigureAwait(false);
                            break;
                        }
                        if (!_runtimeGate.Wait(0))
                        {
                            await WriteAsync(writer, writerGate, id, "error", "Cannot switch model while the runtime is busy", ct).ConfigureAwait(false);
                            break;
                        }

                        try
                        {
                            var model = _runtime.Models.Resolve(modelRef);
                            if (model is null)
                                await WriteAsync(writer, writerGate, id, "error", $"Unknown model {modelRef}", ct).ConfigureAwait(false);
                            else
                            {
                                var profile = _runtime.ConfigStore.Config.GetActiveProfile();
                                profile.Provider = model.Provider.Id;
                                profile.Model = model.Model.Id;
                                _runtime.ConfigStore.Save();
                                await WriteAsync(writer, writerGate, id, "result", $"switched to {model.Ref}", ct).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _runtimeGate.Release();
                        }
                        break;
                    }

                    case "doctor":
                    {
                        var checks = _runtime.Health.RunChecks(_runtime.Workspace);
                        var summary = string.Join("\n", checks.Select(c => $"{(c.Ok ? "[OK]" : "[FAIL]")} {c.Name}: {c.Detail}"));
                        await WriteAsync(writer, writerGate, id, "result", summary, ct).ConfigureAwait(false);
                        break;
                    }

                    case "shutdown":
                        activeTurnCts?.Cancel();
                        if (activeTurn is not null) await ObserveAsync(activeTurn).ConfigureAwait(false);
                        await WriteAsync(writer, writerGate, id, "bye", "", ct).ConfigureAwait(false);
                        _shutdown.Cancel();
                        return;

                    default:
                        await WriteAsync(writer, writerGate, id, "error", $"Unknown method: {method}", ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            activeTurnCts?.Cancel();
            if (activeTurn is not null) await ObserveAsync(activeTurn).ConfigureAwait(false);
            activeTurnCts?.Dispose();
        }
    }

    private async Task RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string message,
        long id,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken turnCt,
        CancellationToken connectionCt)
    {
        using var subscription = _runtime.Events.Subscribe();
        var forwarder = ForwardEventsAsync(subscription, writer, writerGate, id, connectionCt);
        AgentTurnResult? result = null;
        Exception? failure = null;

        try
        {
            _runtime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
            _runtime.Skills.Attach(workspace);
            result = await _runTurn(session, workspace, message, turnCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
        {
            result = new AgentTurnResult("", true, null);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _runtime.Prompts.SetWorkspaceRoot(_runtime.Workspace.PromptsDir);
            _runtime.Skills.Attach(_runtime.Workspace);
            subscription.Dispose();
            try { await forwarder.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }

        try
        {
            if (failure is not null)
                await WriteAsync(writer, writerGate, id, "error", failure.Message, connectionCt).ConfigureAwait(false);
            else if (result!.Cancelled)
                await WriteAsync(writer, writerGate, id, "cancelled", result.Text, connectionCt).ConfigureAwait(false);
            else
                await WriteAsync(writer, writerGate, id,
                    result.Error is null ? "done" : "error",
                    result.Error ?? result.Text, connectionCt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client disconnected while the turn was ending.
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private static async Task ForwardEventsAsync(
        IEventSubscription subscription,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        CancellationToken ct)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // ErrorEvent is a runtime diagnostic, not a terminal protocol response. The turn
            // result below sends the final `error` envelope with the provider's full detail.
            // Forwarding this event as `error` would terminate desktop clients early and lose
            // ErrorEvent.Detail (for example the HTTP status and API response message).
            var (name, data) = evt switch
            {
                AssistantTextDeltaEvent delta => ("delta", delta.Delta),
                ThinkingDeltaEvent thinking => ("thinking", thinking.Delta),
                StatusEvent status => ("status", status.Status),
                ToolCallStartedEvent tool => ("tool_start", tool.ToolName),
                ToolCallCompletedEvent tool => ("tool_done", $"{tool.ToolName}: {tool.ResultSummary}"),
                _ => ((string?)null, ""),
            };
            if (name is not null)
                await WriteAsync(writer, writerGate, id, name, data, ct).ConfigureAwait(false);
        }
    }

    private string CurrentMode() =>
        AgentModeExtensions.Parse(
            _runtime.Workspace.Config?.Mode ?? _runtime.ConfigStore.Config.Agent.Mode)
        .ToString().ToLowerInvariant();

    private void SaveMode(string mode)
    {
        var config = _runtime.ConfigStore.Config;
        config.Agent.Mode = mode;
        config.GetActiveProfile().Mode = mode;
        _runtime.ConfigStore.Save();

        if (_runtime.Workspace.Config is not { } workspaceConfig) return;
        workspaceConfig.Mode = mode;
        Directory.CreateDirectory(_runtime.Workspace.SeekClawDir);
        var path = Path.Combine(_runtime.Workspace.SeekClawDir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(workspaceConfig, SeekClawJsonContext.Default.WorkspaceConfig));
    }

    private static bool TryNormalizeMode(string? rawMode, out string mode)
    {
        mode = rawMode?.Trim().ToLowerInvariant() ?? "";
        return mode is "plan" or "readonly" or "edit" or "auto";
    }

    private string WorkspaceJson(WorkspaceInfo workspace)
    {
        var kinds = new JsonArray(workspace.ProjectKinds.Select(kind => JsonValue.Create(kind)).ToArray());
        return new JsonObject
        {
            ["path"] = workspace.Root,
            ["name"] = workspace.Name,
            ["projectKinds"] = kinds,
            ["mode"] = CurrentMode(),
        }.ToJsonString();
    }

    private static string ProtocolInfoJson() => new JsonObject
    {
        ["version"] = ProtocolVersion,
        ["transport"] = "jsonl",
        ["capabilities"] = new JsonArray(
            "chat", "agent.cancel", "agent.mode", "workspace", "profile", "provider",
            "model", "mcp", "skill", "usage", "session", "global-session", "doctor"),
        ["methods"] = new JsonArray(
            "ping", "protocol.info", "chat", "agent.runTurn", "agent.cancel",
            "workspace.get", "workspace.open", "workspace.init", "agent.mode.get", "agent.mode.switch",
            "profile.list", "profile.upsert", "profile.use", "profile.remove",
            "provider.list", "provider.upsert", "provider.use", "provider.remove", "provider.test",
            "model.list", "model.catalog", "model.switch", "model.test",
            "mcp.list", "mcp.upsert", "mcp.remove", "mcp.reload",
            "skill.list", "skill.toggle", "usage.get", "doctor", "doctor.run",
            "session.list", "session.get", "session.update", "session.archive", "session.delete",
            "session.resume", "session.new", "shutdown"),
    }.ToJsonString();

    private async Task RunAdminAsync(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        bool exclusive,
        Func<CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        if (exclusive && !_runtimeGate.Wait(0))
        {
            await WriteAsync(writer, writerGate, id, "error", "Runtime is busy with an active turn", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var data = await action(ct).ConfigureAwait(false);
            await WriteAsync(writer, writerGate, id, "result", data, ct).ConfigureAwait(false);
        }
        catch (DaemonRequestException ex)
        {
            await WriteAsync(writer, writerGate, id, "error", ex.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            await WriteAsync(writer, writerGate, id, "error", $"Invalid request: {ex.Message}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAsync(writer, writerGate, id, "error", ex.Message, ct).ConfigureAwait(false);
        }
        finally
        {
            if (exclusive) _runtimeGate.Release();
        }
    }

    private static JsonObject Params(JsonObject request) =>
        request["params"] as JsonObject ?? new JsonObject();

    private static async Task WriteAsync(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        string eventName,
        string data,
        CancellationToken ct)
    {
        var payload = new JsonObject { ["id"] = id, ["event"] = eventName, ["data"] = data };
        await writerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(payload.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            writerGate.Release();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static bool SessionBelongsTo(AgentSession session, WorkspaceInfo workspace) =>
        workspace.IsGlobal
            ? string.IsNullOrWhiteSpace(session.Header.Workspace)
            : string.Equals(session.Header.Workspace, workspace.Root, StringComparison.OrdinalIgnoreCase);
}
