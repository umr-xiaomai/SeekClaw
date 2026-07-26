using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Sessions;

namespace SeekClaw.Runtime.Daemon;

/// <summary>
/// Daemon mode: exposes the runtime over a Windows named pipe or a Unix domain socket
/// using newline-delimited JSON-RPC-style messages, so GUIs and editors can attach.
///
/// Request:  {"id":1,"method":"chat","params":{"message":"…"}}
///           {"id":2,"method":"ping"}
/// Response: {"id":1,"event":"delta","data":"…"}   (streamed)
///           {"id":1,"event":"done","data":"…"}
/// </summary>
public sealed class DaemonServer(SeekClawRuntime runtime)
{
    public const string PipeName = "seekclaw";
    public static string SocketPath => Path.Combine(SeekClawPaths.Home, "daemon.sock");

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    await ServeNamedPipeAsync(ct).ConfigureAwait(false);
                else
                    await ServeUnixSocketAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // client vanished — accept the next one
            }
        }
    }

    private async Task ServeNamedPipeAsync(CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
    }

    private async Task ServeUnixSocketAsync(CancellationToken ct)
    {
        File.Delete(SocketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(1);

        using var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await HandleConnectionAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        AgentSession? session = null;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { request = null; }
            if (request is null) continue;

            var id = request["id"]?.GetValue<long>() ?? 0;
            var method = request["method"]?.GetValue<string>() ?? "";

            switch (method)
            {
                case "ping":
                    await WriteAsync(writer, id, "pong", "", ct).ConfigureAwait(false);
                    break;

                case "chat":
                {
                    var message = request["params"]?["message"]?.GetValue<string>() ?? "";
                    if (message.Length == 0)
                    {
                        await WriteAsync(writer, id, "error", "params.message is required", ct).ConfigureAwait(false);
                        break;
                    }

                    session ??= runtime.Sessions.LoadLatest(runtime.Workspace)
                                ?? runtime.Sessions.Create(runtime.Workspace);

                    using var subscription = runtime.Events.Subscribe();
                    var forwarder = ForwardEventsAsync(subscription, writer, id, ct);

                    var result = await runtime.Agent
                        .RunTurnAsync(session, runtime.Workspace, message, ct)
                        .ConfigureAwait(false);

                    subscription.Dispose();
                    try { await forwarder.ConfigureAwait(false); } catch (OperationCanceledException) { }

                    await WriteAsync(writer, id,
                        result.Error is null ? "done" : "error",
                        result.Error ?? result.Text, ct).ConfigureAwait(false);
                    break;
                }

                case "shutdown":
                    await WriteAsync(writer, id, "bye", "", ct).ConfigureAwait(false);
                    return;

                default:
                    await WriteAsync(writer, id, "error", $"Unknown method: {method}", ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task ForwardEventsAsync(
        IEventSubscription subscription, StreamWriter writer, long id, CancellationToken ct)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var (name, data) = evt switch
            {
                AssistantTextDeltaEvent delta => ("delta", delta.Delta),
                ThinkingDeltaEvent thinking => ("thinking", thinking.Delta),
                StatusEvent status => ("status", status.Status),
                ToolCallStartedEvent tool => ("tool_start", tool.ToolName),
                ToolCallCompletedEvent tool => ("tool_done", $"{tool.ToolName}: {tool.ResultSummary}"),
                ErrorEvent error => ("error", error.Message),
                _ => ((string?)null, ""),
            };
            if (name is not null)
                await WriteAsync(writer, id, name, data, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(StreamWriter writer, long id, string eventName, string data, CancellationToken ct)
    {
        var payload = new JsonObject { ["id"] = id, ["event"] = eventName, ["data"] = data };
        await writer.WriteLineAsync(payload.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
    }
}
