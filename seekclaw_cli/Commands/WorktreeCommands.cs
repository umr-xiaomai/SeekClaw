using System.CommandLine;
using System.Text.Json.Nodes;

namespace SeekClaw.Cli.Commands;

public static class WorktreeCommands
{
    public static Command Build()
    {
        var command = new Command("worktree", "Manage worktrees associated with conversations in this workspace");
        var list = new Command("list", "List managed task worktrees as JSON");
        list.SetAction(async (_, _) =>
        {
            await using var runtime = CliHost.CreateRuntime();
            Console.WriteLine(new JsonArray(runtime.Worktrees.List(runtime.Workspace.Root).Select(x => (JsonNode)x).ToArray()).ToJsonString());
            return 0;
        });
        command.Add(list);
        foreach (var operation in new[] { "create", "archive", "restore" })
        {
            var subcommand = new Command(operation, $"{operation} a conversation's worktree");
            var id = new Argument<string>("session-id");
            var revision = new Option<string?>("--base-ref");
            subcommand.Add(id);
            if (operation == "create") subcommand.Add(revision);
            subcommand.SetAction(async (parse, ct) =>
            {
                try
                {
                    await using var runtime = CliHost.CreateRuntime();
                    var sessionId = parse.GetValue(id)!;
                    if (runtime.Sessions.Load(runtime.Workspace, sessionId) is null
                        && (operation == "create" || runtime.Worktrees.Get(runtime.Workspace.Root, sessionId) is null))
                        throw new ArgumentException("Conversation or managed worktree not found in this workspace.");
                    var result = operation switch
                    {
                        "create" => await runtime.Worktrees.CreateAsync(runtime.Workspace.Root, sessionId, parse.GetValue(revision) ?? "HEAD", ct),
                        "archive" => await runtime.Worktrees.ArchiveAsync(runtime.Workspace.Root, sessionId, ct),
                        _ => await runtime.Worktrees.RestoreAsync(runtime.Workspace.Root, sessionId, ct),
                    };
                    Console.WriteLine(result.ToJsonString());
                    return 0;
                }
                catch (OperationCanceledException) { return 130; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            });
            command.Add(subcommand);
        }
        return command;
    }
}
