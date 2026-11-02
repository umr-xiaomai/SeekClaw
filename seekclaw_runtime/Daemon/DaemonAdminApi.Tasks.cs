using System.Text.Json.Nodes;
using SeekClaw.Runtime.Mcp;

namespace SeekClaw.Runtime.Daemon;

internal sealed partial class DaemonAdminApi
{
    public async Task<string> ManageWorktreeAsync(string action, JsonObject parameters, CancellationToken ct)
    {
        var workspace = SessionWorkspace(parameters);
        if (action == "list") return new JsonArray(runtime.Worktrees.List(workspace.Root).Select(x => (JsonNode)x).ToArray()).ToJsonString();
        var id = RequiredString(parameters, "sessionId");
        if (runtime.Sessions.Load(workspace, id) is null
            && (action == "create" || runtime.Worktrees.Get(workspace.Root, id) is null))
            throw new DaemonRequestException("Conversation or managed worktree not found.");
        if (workspace.IsGlobal) throw new DaemonRequestException("A project is required for a Git worktree.");
        if (action == "archive" && runtime.Commands.List(id).OfType<JsonObject>().Any(x => x["status"]?.GetValue<string>() == "running"))
            throw new DaemonRequestException("Stop this conversation's active command sessions before archiving its worktree.");
        var result = action switch
        {
            "create" => await runtime.Worktrees.CreateAsync(workspace.Root, id, OptionalString(parameters, "baseRef") ?? "HEAD", ct).ConfigureAwait(false),
            "archive" => await runtime.Worktrees.ArchiveAsync(workspace.Root, id, ct).ConfigureAwait(false),
            "restore" => await runtime.Worktrees.RestoreAsync(workspace.Root, id, ct).ConfigureAwait(false),
            _ => throw new DaemonRequestException("Unknown worktree operation."),
        };
        return result.ToJsonString();
    }

    public async Task<string> ManageCommandAsync(string action, JsonObject parameters, CancellationToken ct)
    {
        var workspace = SessionWorkspace(parameters);
        var owner = RequiredString(parameters, "sessionId");
        if (runtime.Sessions.Load(workspace, owner) is null) throw new DaemonRequestException("Conversation not found.");
        if (action == "list") return runtime.Commands.List(owner).ToJsonString();
        var id = RequiredString(parameters, "commandId");
        var result = action switch
        {
            "read" => await runtime.Commands.ReadAsync(owner, id, parameters["cursor"]?.GetValue<long>(), parameters["waitMs"]?.GetValue<int>() ?? 0, ct).ConfigureAwait(false),
            "write" => await runtime.Commands.WriteAsync(owner, id, OptionalString(parameters, "text") ?? "", parameters["closeInput"]?.GetValue<bool>() ?? false, ct).ConfigureAwait(false),
            "terminate" or "close" => await runtime.Commands.StopAsync(owner, id, action == "close", ct).ConfigureAwait(false),
            _ => throw new DaemonRequestException("Unknown command session operation."),
        };
        return result.ToJsonString();
    }

    public async Task<string> ManageSubAgentAsync(string action, JsonObject parameters, CancellationToken ct)
    {
        var parent = RequiredString(parameters, "sessionId");
        if (runtime.Sessions.Load(SessionWorkspace(parameters), parent) is null) throw new DaemonRequestException("Conversation not found.");
        if (action == "list") return runtime.SubAgentTasks.List(parent).ToJsonString();
        var id = RequiredString(parameters, "taskId");
        return (action switch
        {
            "status" => runtime.SubAgentTasks.Get(parent, id),
            "wait" => await runtime.SubAgentTasks.WaitAsync(parent, id, parameters["waitMs"]?.GetValue<int>() ?? 1000, ct).ConfigureAwait(false),
            "cancel" => await runtime.SubAgentTasks.CancelAsync(parent, id, ct).ConfigureAwait(false),
            _ => throw new DaemonRequestException("Unknown sub-agent operation."),
        }).ToJsonString();
    }

    public async Task<string> ManageMcpAuthenticationAsync(string action, JsonObject parameters, CancellationToken ct)
    {
        if (action == "status") return runtime.McpAuth.LoginStatus(RequiredString(parameters, "state")).ToJsonString();
        var name = RequiredString(parameters, "name");
        var servers = runtime.Mcp.LoadServerConfigs(runtime.Workspace);
        if (!servers.TryGetValue(name, out var server) || server.Transport == "stdio") throw new DaemonRequestException("Remote MCP server not found.");
        if (action == "login") return (await runtime.McpAuth.BeginLoopbackAsync(name, server, ct).ConfigureAwait(false)).ToJsonString();
        if (action != "logout") throw new DaemonRequestException("Unknown MCP authentication operation.");
        await runtime.McpAuth.LogoutAsync(name, server, ct).ConfigureAwait(false);
        ReconnectMcpInBackground();
        return "ok";
    }
}
