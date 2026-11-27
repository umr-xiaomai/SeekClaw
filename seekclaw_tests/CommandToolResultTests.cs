using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class CommandToolResultTests
{
    private static ToolContext Context(string sessionId) => new()
    {
        Workspace = new WorkspaceInfo { Root = Path.GetTempPath(), ProjectKinds = [] },
        Events = new EventBus(),
        Agent = new AgentConfig(),
        SessionId = sessionId,
    };

    [Fact]
    public async Task ExecCommand_NonzeroExitPreservesOutputAndReportsFailure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var prompts = new FilePromptProvider();
        await using var sessions = new CommandSessionManager();
        var tool = new ExecCommandTool(prompts, sessions);
        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["cmd"] = "echo expected-output; exit 7",
            ["yield_time_ms"] = 30_000,
        }, Context("failed-command"), deadline.Token);

        Assert.False(result.Success);
        var record = JsonNode.Parse(result.Output)!;
        Assert.Equal("exited", record["status"]!.GetValue<string>());
        Assert.Equal(7, record["exitCode"]!.GetValue<int>());
        Assert.Contains("expected-output", record["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task CommandSession_ExplicitTerminationReturnsCancelledAsSuccess()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var prompts = new FilePromptProvider();
        await using var sessions = new CommandSessionManager();
        var context = Context("terminated-command");
        var tool = new CommandSessionTool(prompts, sessions);
        var started = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "start",
            ["command"] = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60",
        }, context, deadline.Token);
        var id = JsonNode.Parse(started.Output)!["sessionId"]!.GetValue<string>();

        var stopped = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "terminate",
            ["session_id"] = id,
        }, context, deadline.Token);

        Assert.True(stopped.Success);
        Assert.Equal("cancelled", JsonNode.Parse(stopped.Output)!["status"]!.GetValue<string>());
    }
}
