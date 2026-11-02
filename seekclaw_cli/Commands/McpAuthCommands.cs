using System.CommandLine;
using System.Diagnostics;

namespace SeekClaw.Cli.Commands;

internal static class McpAuthCommands
{
    public static void AddTo(Command parent)
    {
        foreach (var operation in new[] { "login", "logout" })
        {
            var command = new Command(operation, $"{operation} for a remote MCP server");
            var name = new Argument<string>("server");
            var noBrowser = new Option<bool>("--no-browser") { Description = "Print the authorization URL without opening a browser" };
            command.Add(name);
            if (operation == "login") command.Add(noBrowser);
            command.SetAction(async (parse, ct) =>
            {
                try
                {
                    await using var runtime = CliHost.CreateRuntime();
                    var serverName = parse.GetValue(name)!;
                    if (!runtime.Mcp.LoadServerConfigs(runtime.Workspace).TryGetValue(serverName, out var server) || server.Transport == "stdio")
                        throw new ArgumentException("Remote MCP server not found.");
                    if (operation == "logout") await runtime.McpAuth.LogoutAsync(serverName, server, ct);
                    else
                    {
                        var login = await runtime.McpAuth.BeginLoopbackAsync(serverName, server, ct);
                        var url = login["authorizationUrl"]!.GetValue<string>();
                        Console.Error.WriteLine(url);
                        if (!parse.GetValue(noBrowser))
                        {
                            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                            { Console.Error.WriteLine("Open the URL above in a browser to finish login."); }
                        }
                        await runtime.McpAuth.WaitForLoginAsync(login["state"]!.GetValue<string>(), ct);
                    }
                    Console.WriteLine(operation == "login" ? "Authenticated." : "Logged out.");
                    return 0;
                }
                catch (OperationCanceledException) { return 130; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            });
            parent.Add(command);
        }
    }
}
