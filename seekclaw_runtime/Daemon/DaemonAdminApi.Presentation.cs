using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Presentation;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Runtime.Daemon;

internal sealed partial class DaemonAdminApi
{
    public string GetSchedule(JsonObject parameters) => JsonSerializer.Serialize(
        runtime.Schedules.Get(RequiredString(parameters, "id")), SeekClawJsonContext.Compact.ScheduledTask);

    /// <summary>Reads only an HTML artifact already presented in this conversation.</summary>
    public async Task<string> ReadArtifactAsync(JsonObject parameters, CancellationToken ct)
    {
        var workspace = SessionWorkspace(parameters);
        var sessionId = RequiredString(parameters, "sessionId");
        var widgetId = RequiredString(parameters, "widgetId");
        var session = runtime.Sessions.Load(workspace, sessionId)
            ?? throw new DaemonRequestException("Conversation not found.");
        var artifact = session.Messages.SelectMany(message => message.ToolWidgets ?? [])
            .OfType<ArtifactWidget>().LastOrDefault(widget => widget.Id == widgetId)
            ?? throw new DaemonRequestException("Artifact not found in this conversation.");
        if (artifact.MediaType != "text/html" || !ArtifactWidget.IsHtml(artifact.Path))
            throw new DaemonRequestException("This file does not support HTML preview.");

        var executionWorkspace = runtime.Worktrees.Resolve(workspace, sessionId);
        // Global Root is the configuration directory. Use the directory recorded by
        // the presenting tool, falling back to the legacy file-tool execution rule.
        // Project artifacts always stay inside their current workspace/worktree.
        var root = executionWorkspace.IsGlobal
            ? artifact.ExecutionRoot ?? executionWorkspace.WorkingDirectory
            : executionWorkspace.Root;
        var path = WorkspacePathGuard.Resolve(root, artifact.Path);
        const int maxBytes = 2 * 1024 * 1024;
        await using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes) throw new DaemonRequestException("HTML preview is limited to 2 MiB. Open the file from its folder instead.");
        var buffer = new byte[maxBytes + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (length > maxBytes) throw new DaemonRequestException("HTML preview is limited to 2 MiB.");
        using var reader = new StreamReader(new MemoryStream(buffer, 0, length), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new JsonObject { ["html"] = await reader.ReadToEndAsync(ct).ConfigureAwait(false), ["path"] = path }.ToJsonString();
    }
}
