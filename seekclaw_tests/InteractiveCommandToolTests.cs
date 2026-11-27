using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Data;
using SeekClaw.Runtime.Execution;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Tests;

public sealed class InteractiveCommandToolTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("seekclaw-interactive-command-");
    private SeekClawDatabase? _database;

    public void Dispose()
    {
        if (_database is not null)
        {
            using var connection = _database.OpenConnection();
            SqliteConnection.ClearPool(connection);
        }
        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task TerminalPrompt_CanBePolledAndAnsweredFromANewTurn()
    {
        if (!OperatingSystem.IsWindows() && !File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) return;

        const string workdir = "terminal fixture";
        const string prompt = "fixture-input";
        var cwd = _root.CreateSubdirectory(workdir);
        await File.WriteAllTextAsync(Path.Combine(cwd.FullName, "prompt.txt"), prompt);
        var command = OperatingSystem.IsWindows()
            ? "$line = Read-Host ([IO.File]::ReadAllText('prompt.txt')); [Console]::WriteLine('received<' + $line + '>'); exit 7"
            : "cat prompt.txt; printf ': '; IFS= read -r line; printf 'received<%s>\n' \"$line\"; exit 7";
        var workspace = new WorkspaceInfo { Root = _root.FullName, ProjectKinds = [] };
        var config = new ConfigStore(Path.Combine(_root.FullName, "config.json"), Path.Combine(_root.FullName, "state.json"));
        _database = new SeekClawDatabase(Path.Combine(_root.FullName, "test.db"));
        await using var runtime = SeekClawRuntime.CreateIsolated(workspace, configureServices: services => services
            .AddSingleton<IConfigStore>(config)
            .AddSingleton(_database)
            .AddSingleton(_ => new CommandSessionManager()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var output = new StringBuilder();
        JsonObject page;
        string id;
        long cursor = 0;

        await using (var launchTurn = SeekClawRuntime.CreateIsolated(workspace, configureServices: runtime.ShareTaskServices))
        {
            var start = Assert.IsAssignableFrom<ITool>(launchTurn.Tools.Resolve("exec_command"));
            var started = await start.ExecuteAsync(new JsonObject
            {
                ["cmd"] = command,
                ["workdir"] = workdir,
                ["tty"] = true,
                ["yield_time_ms"] = 250,
                ["timeout_seconds"] = 30,
            }, Context(launchTurn, "owner"), deadline.Token);
            Assert.True(started.Success, started.Output);
            page = Assert.IsType<JsonObject>(JsonNode.Parse(started.Output));
            id = page["sessionId"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Equal("running", page["status"]!.GetValue<string>());
            AppendPage();
        }

        await using var resumeTurn = SeekClawRuntime.CreateIsolated(workspace, configureServices: runtime.ShareTaskServices);
        var input = Assert.IsAssignableFrom<ITool>(resumeTurn.Tools.Resolve("write_stdin"));
        var rejected = await input.ExecuteAsync(InputArgs("unexpected\n"), Context(resumeTurn, "another-conversation"), deadline.Token);
        Assert.False(rejected.Success);
        Assert.Contains("Command session not found in this conversation", rejected.Output);

        do { await ReadNextPage(); }
        while (page["status"]!.GetValue<string>() == "running" && !output.ToString().Contains(prompt, StringComparison.Ordinal));

        Assert.Contains(prompt, output.ToString());
        Assert.Equal("running", page["status"]!.GetValue<string>());
        Assert.DoesNotContain("received<", output.ToString());

        await ReadNextPage("typed answer\n");
        while (page["status"]!.GetValue<string>() == "running" || page["hasMore"]!.GetValue<bool>())
            await ReadNextPage();

        Assert.Equal("exited", page["status"]!.GetValue<string>());
        Assert.Equal(7, page["exitCode"]!.GetValue<int>());
        Assert.Contains("received<typed answer>", output.ToString());

        void AppendPage()
        {
            output.Append(page["output"]!.GetValue<string>());
            cursor = page["cursor"]!.GetValue<long>();
        }

        JsonObject InputArgs(string chars) => new()
        {
            ["session_id"] = id,
            ["chars"] = chars,
            ["cursor"] = cursor,
            ["yield_time_ms"] = 250,
        };

        async Task ReadNextPage(string chars = "")
        {
            var result = await input.ExecuteAsync(InputArgs(chars), Context(resumeTurn, "owner"), deadline.Token);
            page = Assert.IsType<JsonObject>(JsonNode.Parse(result.Output));
            if (page["status"]!.GetValue<string>() == "running")
                Assert.True(result.Success, result.Output);
            else
                Assert.False(result.Success, result.Output);
            AppendPage();
        }
    }

    private static ToolContext Context(SeekClawRuntime runtime, string sessionId) => new()
    {
        Workspace = runtime.Workspace,
        Events = runtime.Events,
        Agent = runtime.ConfigStore.Config.Agent,
        SessionId = sessionId,
    };
}
