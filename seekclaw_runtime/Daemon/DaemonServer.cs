using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Scheduling;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Daemon;

/// <summary>
/// Exposes the runtime over a Windows named pipe or Unix domain socket using
/// newline-delimited JSON messages. Responses keep the legacy event envelope:
/// {"id":1,"event":"result","data":"..."}.
/// </summary>
public sealed class DaemonServer : IAsyncDisposable
{
    public const string PipeName = "seekclaw";
    public const string ProtocolVersion = "2.1";
    public static string SocketPath => Path.Combine(SeekClawPaths.Home, "daemon.sock");
    private const int MaxImageCount = 10;
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxTotalImageBytes = 40 * 1024 * 1024;
    private static readonly HashSet<string> SupportedImageTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];

    private readonly SeekClawRuntime _runtime;
    private readonly DaemonAdminApi _admin;
    private readonly WorkspaceInfo _globalWorkspace;
    private readonly Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>>? _runTurn;
    private readonly bool _useIsolatedTurnRuntime;
    private readonly CancellationTokenSource _shutdown = new();

    // Central Task Coordinator: one instance per daemon process is the single
    // source of truth for file write locks across all concurrent agent turns.
    private readonly IFileLockCoordinator _fileLocks = new FileLockCoordinator();

    // Process-wide infrastructure shared by every isolated turn runtime so the
    // HttpClient connection pool and circuit-breaker state survive across turns
    // instead of being rebuilt (and reset) for every single agent task.
    private readonly LlmHttpFactory _sharedHttp = new();
    private readonly CircuitBreaker _sharedBreaker;
    private readonly ScheduleService _scheduler;
    private readonly IEventSubscription _runtimeEvents;
    private readonly Task _scheduleEventsTask;

    // Configuration and workspace administration remains serialized, while agent turns
    // execute concurrently in isolated runtime instances.
    private readonly SemaphoreSlim _adminGate = new(1, 1);

    // Connected clients that receive unsolicited daemon events (such as scheduled-task
    // completion). Each connection owns a writer and a gate so broadcasts stay ordered.
    private readonly Lock _clientsGate = new();
    private readonly Dictionary<long, ClientSink> _clients = [];
    private long _nextClientId;

    private sealed class SessionUsageAccumulator
    {
        public long LlmRounds { get; set; }
        public long ExecutionSteps { get; set; }
        public long InputTokens { get; set; }
        public long TotalInputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long OutputElapsedMs { get; set; }
        public int LastWorkflowStep { get; set; }

        public SessionUsage ToUsage() => new()
        {
            LlmRounds = LlmRounds,
            ExecutionSteps = ExecutionSteps,
            InputTokens = InputTokens,
            TotalInputTokens = TotalInputTokens,
            CachedInputTokens = CachedInputTokens,
            OutputTokens = OutputTokens,
            OutputElapsedMs = OutputElapsedMs,
        };

        public bool HasActivity =>
            LlmRounds > 0
            || ExecutionSteps > 0
            || InputTokens > 0
            || TotalInputTokens > 0
            || CachedInputTokens > 0
            || OutputTokens > 0
            || OutputElapsedMs > 0;
    }

    public DaemonServer(SeekClawRuntime runtime)
        : this(runtime, null, runtime.Workspaces.CreateGlobal())
    {
    }

    internal DaemonServer(
        SeekClawRuntime runtime,
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>>? runTurn)
        : this(runtime, runTurn, runtime.Workspaces.CreateGlobal())
    {
    }

    internal DaemonServer(
        SeekClawRuntime runtime,
        Func<AgentSession, WorkspaceInfo, string, CancellationToken, Task<AgentTurnResult>>? runTurn,
        WorkspaceInfo globalWorkspace)
    {
        _runtime = runtime;
        _globalWorkspace = globalWorkspace;
        _runTurn = runTurn;
        _useIsolatedTurnRuntime = runTurn is null;
        _sharedBreaker = new CircuitBreaker(runtime.ConfigStore.Config.Routing.Retry);
        // Tests inject a stub turn runner; route scheduled runs through it too so the
        // daemon harness stays deterministic. Production keeps the isolated runtime path.
        _scheduler = new ScheduleService(
            runtime.Schedules, runtime, _fileLocks, _sharedHttp, _sharedBreaker,
            runTurn is null ? null : (workspace, session, prompt, ct) => runTurn(session, workspace, prompt, ct));
        _runtimeEvents = _runtime.Events.Subscribe();
        _scheduleEventsTask = BroadcastScheduleEventsAsync(_runtimeEvents.Reader, _shutdown.Token);
        _admin = new DaemonAdminApi(runtime, globalWorkspace, _fileLocks, _scheduler, _shutdown.Token);
        _admin.McpStatusChanged += OnMcpStatusChanged;
    }

    /// <summary>Releases the scheduler and shared HTTP clients when the daemon host shuts down.</summary>
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _scheduleEventsTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _runtimeEvents.Dispose();
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        _sharedHttp.Dispose();
        _shutdown.Dispose();

        if (!OperatingSystem.IsWindows() && File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var runCt = linkedCts.Token;
        var schedulerTask = RunSchedulerAsync(runCt);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                while (!runCt.IsCancellationRequested)
                {
                    try
                    {
                        await ServeNamedPipeAsync(runCt).ConfigureAwait(false);
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
            else
            {
                await ServeUnixSocketLoopAsync(runCt).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await schedulerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        try
        {
            await _scheduler.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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

    private async Task ServeUnixSocketLoopAsync(CancellationToken ct)
    {
        var socketDir = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrEmpty(socketDir) && !Directory.Exists(socketDir))
        {
            Directory.CreateDirectory(socketDir);
        }

        if (File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(128);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => RunSocketConnectionAsync(socket, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Console.Error.WriteLine($"Daemon socket accept error: {ex.Message}");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        if (File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
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

    private sealed class ConnectionContext(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken connectionCt)
    {
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriterGate { get; } = writerGate;
        public CancellationToken ConnectionCt { get; } = connectionCt;
        public ConcurrentDictionary<long, ActiveTurn> ActiveTurns { get; } = new();
        public AgentSession? LegacySession;
    }

    internal async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var writerGate = new SemaphoreSlim(1, 1);
        var clientId = RegisterClient(writer, writerGate);
        var context = new ConnectionContext(writer, writerGate, ct);

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

                // Periodically clean up completed turns
                foreach (var (turnId, turn) in context.ActiveTurns)
                {
                    if (turn.Task?.IsCompleted == true)
                    {
                        if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
                        turn.Cancellation.Dispose();
                        context.ActiveTurns.TryRemove(turnId, out _);
                    }
                }

                // Non-blocking concurrent dispatch: reader immediately loops back to read the next line!
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DispatchMethodAsync(request, id, method, context).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Server error: {ex.Message}", context.ConnectionCt).ConfigureAwait(false);
                    }
                }, ct);
            }
        }
        finally
        {
            UnregisterClient(clientId);
            foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
            foreach (var turn in context.ActiveTurns.Values)
                if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
            foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Dispose();
            context.ActiveTurns.Clear();
        }
    }

    private async Task DispatchMethodAsync(JsonObject request, long id, string method, ConnectionContext context)
    {
        switch (method)
        {
                    case "ping":
                        await WriteAsync(context.Writer, context.WriterGate, id, "pong", "", context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "protocol.info":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", ProtocolInfoJson(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "workspace.init":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.InitializeWorkspace()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "factory.reset":
                    {
                        foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
                        foreach (var turn in context.ActiveTurns.Values)
                            if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.FactoryReset()), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "chat":
                    case "agent.runTurn":
                    case "agent/runTurn":
                    {
                        var parameters = Params(request);
                        var message = parameters["message"]?.GetValue<string>()
                                      ?? parameters["prompt"]?.GetValue<string>()
                                      ?? "";
                        IReadOnlyList<ChatImageAttachment> images;
                        try { images = ParseImages(parameters["images"]); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(message) && images.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.message or params.images is required", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        WorkspaceInfo workspace;
                        AgentSession turnSession;
                        var requestedSessionId = parameters["sessionId"]?.GetValue<string>();
                        try
                        {
                            workspace = ResolveWorkspace(parameters);
                            turnSession = LoadTurnSession(workspace, requestedSessionId, ref context.LegacySession);
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, requestedSessionId).ConfigureAwait(false);
                            break;
                        }
                        ReasoningLevel reasoningLevel;
                        try
                        {
                            reasoningLevel = ParseReasoningLevel(
                                parameters["reasoningLevel"], turnSession.Header.ReasoningLevel);
                            if (turnSession.Header.ReasoningLevel != reasoningLevel)
                            {
                                turnSession.Header.ReasoningLevel = reasoningLevel;
                                _runtime.Sessions.UpdateMetadata(
                                    workspace, turnSession.Header.Id, reasoningLevel: reasoningLevel);
                            }
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt, requestedSessionId).ConfigureAwait(false);
                            break;
                        }
                        var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionCt);
                        var turn = new ActiveTurn(turnSession, workspace, turnCancellation);
                        context.ActiveTurns[id] = turn;
                        turn.Task = RunTurnAsync(
                            turnSession, workspace, message, images, reasoningLevel, id, context.Writer, context.WriterGate,
                            turnCancellation.Token, context.ConnectionCt, turn.Steering);
                        break;
                    }

                    case "agent.steer":
                    {
                        var parameters = Params(request);
                        var message = parameters["message"]?.GetValue<string>()
                                      ?? parameters["prompt"]?.GetValue<string>()
                                      ?? "";
                        IReadOnlyList<ChatImageAttachment> images;
                        try { images = ParseImages(parameters["images"]); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(message) && images.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.message or params.images is required", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }

                        var requestedId = parameters["requestId"]?.GetValue<long?>();
                        var requestedSessionId = parameters["sessionId"]?.GetValue<string>();
                        ActiveTurn? target = requestedId is { } specific
                            ? context.ActiveTurns.GetValueOrDefault(specific)
                            : context.ActiveTurns.Values
                                .Where(turn => string.Equals(turn.Session.Header.Id, requestedSessionId, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(turn => turn.Session.Header.UpdatedAt)
                                .FirstOrDefault();
                        if (target is null)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "No active turn found for this session", context.ConnectionCt, requestedSessionId).ConfigureAwait(false);
                            break;
                        }

                        if (!target.Steering.TryEnqueue(ChatMessage.User(message, images)))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "The active turn is already finishing", context.ConnectionCt, target.Session.Header.Id).ConfigureAwait(false);
                            break;
                        }
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", "guidance queued", context.ConnectionCt, target.Session.Header.Id).ConfigureAwait(false);
                        break;
                    }

                    case "agent.cancel":
                    {
                        var requestedId = request["params"]?["requestId"]?.GetValue<long?>();
                        var targets = requestedId is { } specific
                            ? context.ActiveTurns.Where(item => item.Key == specific).Select(item => item.Value).ToList()
                            : context.ActiveTurns.Values.ToList();
                        if (targets.Count == 0)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", "no active turn", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        foreach (var target in targets) target.Cancellation.Cancel();
                        var detail = requestedId is { } one
                            ? $"cancellation requested for {one}"
                            : $"cancellation requested for {targets.Count} active turns";
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", detail, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "workspace.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", WorkspaceJson(_runtime.Workspace), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "workspace.open":
                    {
                        var path = request["params"]?["path"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.path is required", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }

                        string fullPath;
                        try { fullPath = Path.GetFullPath(path); }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Invalid workspace path: {ex.Message}", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }

                        if (!Directory.Exists(fullPath))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Workspace directory not found: {fullPath}", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

                        try
                        {
                            _runtime.RefreshWorkspace(fullPath);
                            context.LegacySession = null;
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", WorkspaceJson(_runtime.Workspace), context.ConnectionCt).ConfigureAwait(false);
                        }
                        finally
                        {
                            _adminGate.Release();
                        }

                        // Background connect MCP without blocking the admin gate or connection!
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _runtime.ConnectMcpAsync(_shutdown.Token).ConfigureAwait(false);
                            }
                            catch { }
                        }, _shutdown.Token);
                        break;
                    }

                    case "agent.mode.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", CurrentMode(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "agent.mode.switch":
                    {
                        var rawMode = request["params"]?["mode"]?.GetValue<string>();
                        if (!TryNormalizeMode(rawMode, out var mode))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.mode must be one of: plan, readonly, edit, auto", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

                        try
                        {
                            SaveMode(mode);
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", mode, context.ConnectionCt).ConfigureAwait(false);
                        }
                        finally
                        {
                            _adminGate.Release();
                        }
                        break;
                    }

                    case "config.status":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetConfigStatus(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "config.rebuild":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RebuildConfigAndDatabase()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "routing.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetRoutingConfig(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "routing.set":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SetRoutingConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "advanced.get":
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", _admin.GetAdvancedConfig(), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "advanced.set":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.SetAdvancedConfig(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "prompt.optimize":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            token => _admin.OptimizePromptAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListSchedules()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.create":
                    case "schedule.update":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpsertSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.toggle":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ToggleSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.delete":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "schedule.run":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.RunSchedule(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListProviders()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.upsert":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpsertProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.use":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UseProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.remove":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RemoveProvider(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.test":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            token => _admin.TestProvidersAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "provider.models.fetch":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.FetchProviderModelsAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.catalog":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ModelCatalog()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.test":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            token => _admin.TestModelAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "model.update":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpdateModel(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListMcpServers()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.upsert":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.UpsertMcpServerAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.remove":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            token => _admin.RemoveMcpServerAsync(Params(request), token), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "mcp.reload":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _admin.ReloadMcpAsync, context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListSkills()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.import":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ImportSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "skill.toggle":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ToggleSkill(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "usage.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.Usage(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "usage.timeline":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.UsageTimeline(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "doctor.run":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _admin.DoctorAsync, context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "lock.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListLocks()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "project.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListProjects()), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "project.upsert":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpsertProject(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "project.remove":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.RemoveProject(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.list":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.ListSessions(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.get":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, false,
                            _ => Task.FromResult(_admin.GetSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.truncate":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.TruncateSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.update":
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.UpdateSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;

                    case "session.archive":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (context.LegacySession?.Header.Id == sessionId)
                        {
                            context.LegacySession = null;
                        }
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.ArchiveSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "session.delete":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (context.LegacySession?.Header.Id == sessionId)
                        {
                            context.LegacySession = null;
                        }
                        await RunAdminAsync(context.Writer, context.WriterGate, id, true,
                            _ => Task.FromResult(_admin.DeleteSession(Params(request))), context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "session.resume":
                    {
                        var sessionId = request["params"]?["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(sessionId))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.id is required", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        WorkspaceInfo workspace;
                        try { workspace = ResolveWorkspace(Params(request)); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        var loaded = _runtime.Sessions.Load(workspace, sessionId);
                        if (loaded is null)
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Session {sessionId} not found", context.ConnectionCt).ConfigureAwait(false);
                        else
                        {
                            context.LegacySession = loaded;
                            await WriteAsync(context.Writer, context.WriterGate, id, "result", $"resumed {loaded.Header.Id}", context.ConnectionCt).ConfigureAwait(false);
                        }
                        break;
                    }

                    case "session.new":
                    {
                        WorkspaceInfo workspace;
                        try { workspace = ResolveWorkspace(Params(request)); }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        ReasoningLevel reasoningLevel;
                        try
                        {
                            reasoningLevel = ParseReasoningLevel(
                                Params(request)["reasoningLevel"], _runtime.ConfigStore.Config.Agent.ReasoningLevel);
                        }
                        catch (DaemonRequestException ex)
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", ex.Message, context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        var networkEnabled = Params(request)["networkEnabled"]?.GetValue<bool?>()
                            ?? _runtime.ConfigStore.Config.Agent.NetworkEnabled;
                        context.LegacySession = _runtime.Sessions.Create(workspace, reasoningLevel, networkEnabled);
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", context.LegacySession.Header.Id, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "model.list":
                    {
                        var models = _runtime.Models.All().Select(m => m.Ref).ToList();
                        var json = JsonSerializer.Serialize(models, SeekClawJsonContext.Default.ListString);
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", json, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "model.switch":
                    {
                        var modelRef = request["params"]?["model"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(modelRef))
                        {
                            await WriteAsync(context.Writer, context.WriterGate, id, "error", "params.model is required", context.ConnectionCt).ConfigureAwait(false);
                            break;
                        }
                        await _adminGate.WaitAsync(context.ConnectionCt).ConfigureAwait(false);

                        try
                        {
                            var model = _runtime.Models.Resolve(modelRef);
                            if (model is null)
                                await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Unknown model {modelRef}", context.ConnectionCt).ConfigureAwait(false);
                            else
                            {
                                var config = _runtime.ConfigStore.Config;
                                config.Provider = model.Provider.Id;
                                config.Model = model.Model.Id;
                                _runtime.ConfigStore.Save();
                                await WriteAsync(context.Writer, context.WriterGate, id, "result", $"switched to {model.Ref}", context.ConnectionCt).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _adminGate.Release();
                        }
                        break;
                    }

                    case "doctor":
                    {
                        var checks = _runtime.Health.RunChecks(_runtime.Workspace);
                        var summary = string.Join("\n", checks.Select(c => $"{(c.Ok ? "[OK]" : "[FAIL]")} {c.Name}: {c.Detail}"));
                        await WriteAsync(context.Writer, context.WriterGate, id, "result", summary, context.ConnectionCt).ConfigureAwait(false);
                        break;
                    }

                    case "shutdown":
                        foreach (var turn in context.ActiveTurns.Values) turn.Cancellation.Cancel();
                        foreach (var turn in context.ActiveTurns.Values)
                            if (turn.Task is not null) await ObserveAsync(turn.Task).ConfigureAwait(false);
                        await WriteAsync(context.Writer, context.WriterGate, id, "bye", "", context.ConnectionCt).ConfigureAwait(false);
                        _shutdown.Cancel();
                        break;

                    default:
                        await WriteAsync(context.Writer, context.WriterGate, id, "error", $"Unknown method: {method}", context.ConnectionCt).ConfigureAwait(false);
                        break;
                        }
    }

    private void OnMcpStatusChanged() => _ = BroadcastMcpStatusAsync();

    /// <summary>
    /// Tells every connected client that a background MCP reconnect finished, so the
    /// server list can show real connection results without the client polling.
    /// </summary>
    private async Task BroadcastMcpStatusAsync()
    {
        try
        {
            await BroadcastAsync(0, "mcp.updated", "", _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The daemon is shutting down or a client vanished mid-broadcast.
        }
    }

    private async Task BroadcastScheduleEventsAsync(ChannelReader<RuntimeEvent> reader, CancellationToken ct)
    {
        await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (evt is ScheduledTaskUpcomingEvent upcoming)
            {
                var upcomingDetails = new JsonObject
                {
                    ["taskId"] = upcoming.TaskId,
                    ["name"] = upcoming.Name,
                    ["runAt"] = upcoming.RunAt.ToString("O"),
                };
                await BroadcastAsync(0, "schedule.upcoming", upcoming.Name, ct, upcomingDetails)
                    .ConfigureAwait(false);
                continue;
            }
            if (evt is not ScheduledTaskCompletedEvent schedule) continue;
            var details = new JsonObject
            {
                ["taskId"] = schedule.TaskId,
                ["name"] = schedule.Name,
                ["status"] = schedule.Status,
            };
            if (!string.IsNullOrWhiteSpace(schedule.SessionId)) details["sessionId"] = schedule.SessionId;
            if (!string.IsNullOrWhiteSpace(schedule.Error)) details["error"] = schedule.Error;
            await BroadcastAsync(0, "schedule.updated", schedule.TaskId, ct, details).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(
        long id,
        string eventName,
        string data,
        CancellationToken ct,
        JsonObject? details = null)
    {
        List<ClientSink> clients;
        lock (_clientsGate) clients = _clients.Values.ToList();
        foreach (var client in clients)
        {
            try
            {
                await WriteAsync(client.Writer, client.WriterGate, id, eventName, data, ct, details: details)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client disconnected while the broadcast was in flight.
            }
        }
    }

    private long RegisterClient(StreamWriter writer, SemaphoreSlim writerGate)
    {
        var clientId = Interlocked.Increment(ref _nextClientId);
        lock (_clientsGate) _clients[clientId] = new ClientSink(writer, writerGate);
        return clientId;
    }

    private void UnregisterClient(long clientId)
    {
        lock (_clientsGate) _clients.Remove(clientId);
    }

    private async Task RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string message,
        IReadOnlyList<ChatImageAttachment> images,
        ReasoningLevel reasoningLevel,
        long id,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        CancellationToken turnCt,
        CancellationToken connectionCt,
        AgentSteeringQueue steering)
    {
        // Unique per-turn identity for file write-lock ownership; released when the
        // turn ends even if a tool was interrupted before its own finally ran.
        var owner = $"{session.Header.Id}/{Guid.NewGuid().ToString("N")[..8]}";
        await using var turnRuntime = _useIsolatedTurnRuntime
            ? SeekClawRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
              {
                  // Register the process-wide instances AFTER the default type
                  // registrations so DI resolves these for every turn.
                  services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
                  services.AddSingleton(_sharedBreaker);
              })
            : null;
        var runtime = turnRuntime ?? _runtime;
        using var subscription = runtime.Events.Subscribe();
        var sessionUsage = new SessionUsageAccumulator();
        var forwarder = ForwardEventsAsync(
            subscription, writer, writerGate, id, session.Header.Id, connectionCt, sessionUsage);
        AgentTurnResult? result = null;
        Exception? failure = null;

        try
        {
            runtime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
            runtime.Skills.Attach(workspace);
            if (_useIsolatedTurnRuntime && runtime.Mcp.LoadServerConfigs(workspace).Count > 0)
                await runtime.Mcp.ConnectAllAsync(workspace, turnCt).ConfigureAwait(false);

            result = _runTurn is null
                ? await runtime.Agent.RunTurnAsync(
                    session, workspace, message, turnCt, reasoningLevel, images, steering).ConfigureAwait(false)
                : await _runTurn(session, workspace, message, turnCt).ConfigureAwait(false);
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
            subscription.Dispose();
            try { await forwarder.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            if (sessionUsage.HasActivity)
            {
                try
                {
                    var persisted = _runtime.Sessions.RecordUsage(workspace, session.Header.Id, sessionUsage.ToUsage());
                    session.Header.LlmRounds = persisted.LlmRounds;
                    session.Header.ExecutionSteps = persisted.ExecutionSteps;
                    session.Header.InputTokens = persisted.InputTokens;
                    session.Header.TotalInputTokens = persisted.TotalInputTokens;
                    session.Header.CachedInputTokens = persisted.CachedInputTokens;
                    session.Header.OutputTokens = persisted.OutputTokens;
                    session.Header.OutputElapsedMs = persisted.OutputElapsedMs;
                    session.Header.UpdatedAt = persisted.UpdatedAt;
                }
                catch
                {
                    // Usage persistence must not fail the turn.
                }
            }
            _fileLocks.ReleaseAll(owner);
        }

        try
        {
            if (failure is not null)
                await WriteAsync(writer, writerGate, id, "error", failure.Message, connectionCt, session.Header.Id).ConfigureAwait(false);
            else if (result!.Cancelled)
                await WriteAsync(writer, writerGate, id, "cancelled", result.Text, connectionCt, session.Header.Id).ConfigureAwait(false);
            else
                await WriteAsync(writer, writerGate, id,
                    result.Error is null ? "done" : "error",
                    result.Error ?? result.Text, connectionCt, session.Header.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client disconnected while the turn was ending.
        }
    }

    private static async Task ForwardEventsAsync(
        IEventSubscription subscription,
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        string sessionId,
        CancellationToken ct,
        SessionUsageAccumulator usage)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            AccumulateSessionUsage(usage, evt);
            // ErrorEvent is a runtime diagnostic, not a terminal protocol response. The turn
            // result below sends the final `error` envelope with the provider's full detail.
            // Forwarding this event as `error` would terminate desktop clients early and lose
            // ErrorEvent.Detail (for example the HTTP status and API response message).
            var payload = evt switch
            {
                AssistantTextDeltaEvent delta => (Name: (string?)"delta", Data: delta.Delta, Details: (JsonObject?)null),
                ThinkingDeltaEvent thinking => (Name: (string?)"thinking", Data: thinking.Delta, Details: (JsonObject?)null),
                UserSteerEvent steer => (Name: (string?)"steer", Data: steer.Instruction, Details: (JsonObject?)null),
                ImageViewedEvent image => (
                    Name: (string?)"image_view",
                    Data: image.Name,
                    Details: new JsonObject
                    {
                        ["imageId"] = image.ImageId,
                        ["mediaType"] = image.MediaType,
                    }),
                StatusEvent status => (Name: (string?)"status", Data: status.Status, Details: (JsonObject?)null),
                ToolCallStartedEvent tool => (
                    Name: (string?)"tool_start",
                    Data: tool.ToolName,
                    Details: new JsonObject
                    {
                        ["callId"] = tool.CallId,
                        ["summary"] = tool.ArgumentSummary,
                    }),
                ToolCallCompletedEvent tool => (
                    Name: (string?)"tool_done",
                    Data: tool.ResultSummary,
                    Details: new JsonObject
                    {
                        ["callId"] = tool.CallId,
                        ["success"] = tool.Success,
                        ["durationMs"] = tool.Duration.TotalMilliseconds,
                    }),
                FileDiffEvent diff => (
                    Name: (string?)"file_diff",
                    Data: diff.FilePath,
                    Details: new JsonObject
                    {
                        ["callId"] = diff.CallId,
                        ["diff"] = diff.UnifiedDiff,
                    }),
                ModelInvocationStartedEvent model => (
                    Name: (string?)"model_start",
                    Data: $"{model.ProviderId}/{model.ModelId}",
                    Details: new JsonObject
                    {
                        ["provider"] = model.ProviderId,
                        ["model"] = model.ModelId,
                        ["step"] = model.Step,
                    }),
                UsageRecordedEvent tokenEvent => (
                    Name: (string?)"usage",
                    Data: $"{tokenEvent.ProviderId}/{tokenEvent.ModelId}",
                    Details: new JsonObject
                    {
                        ["provider"] = tokenEvent.ProviderId,
                        ["model"] = tokenEvent.ModelId,
                        ["inputTokens"] = tokenEvent.InputTokens,
                        ["outputTokens"] = tokenEvent.OutputTokens,
                        ["totalInputTokens"] = tokenEvent.TotalInputTokens,
                        ["cachedInputTokens"] = tokenEvent.CachedInputTokens,
                        ["elapsedMs"] = tokenEvent.Elapsed.TotalMilliseconds,
                    }),
                WorkflowEvent workflow => (
                    Name: (string?)"workflow",
                    Data: workflow.Label,
                    Details: new JsonObject
                    {
                        ["step"] = workflow.Step,
                        ["kind"] = workflow.Kind,
                        ["label"] = workflow.Label,
                        ["detail"] = workflow.Detail,
                    }),
                PlanUpdatedEvent plan => (
                    Name: (string?)"plan_update",
                    Data: plan.StepsJson,
                    Details: new JsonObject
                    {
                        ["steps"] = JsonNode.Parse(plan.StepsJson),
                        ["explanation"] = plan.Explanation,
                    }),
                _ => (Name: (string?)null, Data: "", Details: (JsonObject?)null),
            };
            if (payload.Name is not null)
                await WriteAsync(writer, writerGate, id, payload.Name, payload.Data, ct, sessionId, payload.Details).ConfigureAwait(false);
        }
    }

    private static void AccumulateSessionUsage(SessionUsageAccumulator usage, RuntimeEvent evt)
    {
        switch (evt)
        {
            case ModelInvocationStartedEvent:
                usage.LlmRounds++;
                break;
            case WorkflowEvent workflow:
                if (workflow.Step > usage.LastWorkflowStep)
                {
                    usage.ExecutionSteps += workflow.Step - usage.LastWorkflowStep;
                    usage.LastWorkflowStep = workflow.Step;
                }
                break;
            case UsageRecordedEvent token:
                usage.InputTokens += token.InputTokens;
                usage.TotalInputTokens += token.TotalInputTokens;
                usage.CachedInputTokens += token.CachedInputTokens;
                usage.OutputTokens += token.OutputTokens;
                usage.OutputElapsedMs += (long)token.Elapsed.TotalMilliseconds;
                break;
        }
    }

    private WorkspaceInfo ResolveWorkspace(JsonObject parameters)
    {
        if (parameters["global"]?.GetValue<bool?>() == true)
            return _globalWorkspace;

        var path = parameters["workspace"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path)) return _runtime.Workspace;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DaemonRequestException($"Invalid workspace path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            throw new DaemonRequestException($"Workspace directory not found: {fullPath}");
        return _runtime.Workspaces.Detect(fullPath);
    }

    private static IReadOnlyList<ChatImageAttachment> ParseImages(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array)
            throw new DaemonRequestException("params.images must be an array");
        if (array.Count > MaxImageCount)
            throw new DaemonRequestException($"A turn supports at most {MaxImageCount} images");

        var images = new List<ChatImageAttachment>(array.Count);
        long totalBytes = 0;
        foreach (var item in array)
        {
            if (item is not JsonObject image)
                throw new DaemonRequestException("Each params.images item must be an object");
            var id = image["id"]?.GetValue<string>()?.Trim();
            var name = image["name"]?.GetValue<string>()?.Trim();
            var mediaType = image["mediaType"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            var data = image["data"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(data))
                throw new DaemonRequestException("Each image requires name and base64 data");
            if (mediaType is null || !SupportedImageTypes.Contains(mediaType))
                throw new DaemonRequestException($"Unsupported image type for {name}: {mediaType ?? "unknown"}");
            if (data.Length > ((MaxImageBytes + 2L) / 3L * 4L) + 4L)
                throw new DaemonRequestException($"Image {name} exceeds the {MaxImageBytes / 1024 / 1024} MB limit");

            byte[] decoded;
            try { decoded = Convert.FromBase64String(data); }
            catch (FormatException)
            {
                throw new DaemonRequestException($"Image {name} contains invalid base64 data");
            }
            if (decoded.Length > MaxImageBytes)
                throw new DaemonRequestException($"Image {name} exceeds the {MaxImageBytes / 1024 / 1024} MB limit");
            totalBytes += decoded.Length;
            if (totalBytes > MaxTotalImageBytes)
                throw new DaemonRequestException(
                    $"Images exceed the {MaxTotalImageBytes / 1024 / 1024} MB total limit");

            var safeName = new string(name.Replace('\\', '/').Split('/').Last()
                .Where(character => !char.IsControl(character)).Take(180).ToArray());
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "image";
            images.Add(new ChatImageAttachment(
                string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id[..Math.Min(id.Length, 128)],
                safeName,
                mediaType,
                data,
                decoded.Length));
        }
        return images;
    }

    private AgentSession LoadTurnSession(
        WorkspaceInfo workspace,
        string? requestedSessionId,
        ref AgentSession? legacySession)
    {
        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            var loaded = _runtime.Sessions.Load(workspace, requestedSessionId);
            return loaded ?? throw new DaemonRequestException($"Session {requestedSessionId} not found");
        }

        if (legacySession is not null && SessionBelongsTo(legacySession, workspace))
            return legacySession;

        legacySession = _runtime.Sessions.LoadLatest(workspace)
                        ?? _runtime.Sessions.Create(workspace);
        return legacySession;
    }

    private sealed class ActiveTurn(
        AgentSession session,
        WorkspaceInfo workspace,
        CancellationTokenSource cancellation)
    {
        public AgentSession Session { get; } = session;
        public WorkspaceInfo Workspace { get; } = workspace;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AgentSteeringQueue Steering { get; } = new();
        public Task? Task { get; set; }
    }

    private string CurrentMode() =>
        AgentModeExtensions.Parse(
            _runtime.Workspace.Config?.Mode ?? _runtime.ConfigStore.Config.Agent.Mode)
        .ToString().ToLowerInvariant();

    private void SaveMode(string mode)
    {
        var config = _runtime.ConfigStore.Config;
        config.Agent.Mode = mode;
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

    private static ReasoningLevel ParseReasoningLevel(JsonNode? node, ReasoningLevel fallback)
    {
        if (node is null) return fallback;
        var value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
        if (ReasoningLevelExtensions.TryParse(value, out var level)) return level;
        throw new DaemonRequestException(
            "params.reasoningLevel must be one of: none, low, medium, high, max, xhigh, ultra");
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
            "chat", "image-input", "concurrent-turns", "reasoning-level", "agent.steer", "agent.cancel", "agent.mode", "workspace", "provider",
            "model", "mcp", "skill", "usage", "project", "session", "global-session", "doctor", "file-locks", "routing", "schedule", "factory-reset", "prompt-optimize", "config-status", "config-rebuild"),
        ["methods"] = new JsonArray(
            "ping", "protocol.info", "chat", "agent.runTurn", "agent.steer", "agent.cancel",
            "config.status", "config.rebuild",
            "workspace.get", "workspace.open", "workspace.init", "agent.mode.get", "agent.mode.switch",
            "routing.get", "routing.set", "advanced.get", "advanced.set",
            "prompt.optimize",
            "schedule.list", "schedule.create", "schedule.update", "schedule.toggle", "schedule.delete", "schedule.run",
            "provider.list", "provider.upsert", "provider.use", "provider.remove", "provider.test", "provider.models.fetch",
            "model.list", "model.catalog", "model.switch", "model.test", "model.update",
            "mcp.list", "mcp.upsert", "mcp.remove", "mcp.reload",
            "skill.list", "skill.import", "skill.toggle", "usage.get", "usage.timeline", "doctor", "doctor.run",
            "project.list", "project.upsert", "project.remove",
            "session.list", "session.get", "session.update", "session.archive", "session.delete",
            "session.resume", "session.new",
            "lock.list", "factory.reset", "shutdown"),
    }.ToJsonString();

    private async Task RunAdminAsync(
        StreamWriter writer,
        SemaphoreSlim writerGate,
        long id,
        bool exclusive,
        Func<CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        if (exclusive) await _adminGate.WaitAsync(ct).ConfigureAwait(false);

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
            if (exclusive) _adminGate.Release();
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
        CancellationToken ct,
        string? sessionId = null,
        JsonObject? details = null)
    {
        var payload = new JsonObject { ["id"] = id, ["event"] = eventName, ["data"] = data };
        if (!string.IsNullOrWhiteSpace(sessionId)) payload["sessionId"] = sessionId;
        if (details is not null) payload["details"] = details;
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

    private sealed record ClientSink(StreamWriter Writer, SemaphoreSlim WriterGate);

    private static bool SessionBelongsTo(AgentSession session, WorkspaceInfo workspace) =>
        workspace.IsGlobal
            ? string.IsNullOrWhiteSpace(session.Header.Workspace)
            : string.Equals(session.Header.Workspace, workspace.Root, StringComparison.OrdinalIgnoreCase);
}
