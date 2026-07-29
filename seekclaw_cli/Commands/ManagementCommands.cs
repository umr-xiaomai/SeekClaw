using System.CommandLine;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Daemon;
using Spectre.Console;

namespace SeekClaw.Cli.Commands;

/// <summary>Creates the runtime for command actions. Chat owns its own long-lived instance.</summary>
public static class CliHost
{
    public static SeekClawRuntime CreateRuntime(string? directory = null) => SeekClawRuntime.Create(directory);
}

public static class ProfileCommands
{
    public static Command Build()
    {
        var command = new Command("profile", "Switch whole environments in one command (work / home / local…)");

        var list = new Command("list", "List profiles");
        list.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("").AddColumn("Profile").AddColumn("Provider").AddColumn("Model").AddColumn("Strategy").AddColumn("Temp");
            foreach (var (name, profile) in config.Profiles)
                table.AddRow(
                    name.Equals(config.ActiveProfile, StringComparison.OrdinalIgnoreCase) ? "[green]●[/]" : "",
                    Markup.Escape(name),
                    Markup.Escape(profile.Provider ?? "-"),
                    Markup.Escape(profile.Model ?? "-"),
                    Markup.Escape(profile.Strategy ?? "-"),
                    profile.Temperature?.ToString("0.##") ?? "-");
            AnsiConsole.Write(table);
            return 0;
        });

        var nameArg = new Argument<string>("name");
        var providerOption = new Option<string?>("--provider");
        var modelOption = new Option<string?>("--model");
        var strategyOption = new Option<string?>("--strategy") { Description = "fast | balanced | quality | cheap | offline" };
        var temperatureOption = new Option<double?>("--temperature");

        var create = new Command("create", "Create a profile");
        create.Add(nameArg); create.Add(providerOption); create.Add(modelOption);
        create.Add(strategyOption); create.Add(temperatureOption);
        create.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var name = parse.GetRequiredValue(nameArg);
            rt.ConfigStore.Config.Profiles[name] = new ProfileConfig
            {
                Provider = parse.GetValue(providerOption),
                Model = parse.GetValue(modelOption),
                Strategy = parse.GetValue(strategyOption),
                Temperature = parse.GetValue(temperatureOption),
            };
            rt.ConfigStore.Save();
            AnsiConsole.MarkupLine($"[green]Created profile '{Markup.Escape(name)}'.[/]");
            return 0;
        });

        var useArg = new Argument<string>("name");
        var use = new Command("use", "Activate a profile");
        use.Add(useArg);
        use.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var name = parse.GetRequiredValue(useArg);
            if (!rt.ConfigStore.Config.Profiles.ContainsKey(name))
            {
                AnsiConsole.MarkupLine("[red]Profile not found.[/]");
                return 1;
            }
            rt.ConfigStore.Config.ActiveProfile = name;
            rt.ConfigStore.Save();
            AnsiConsole.MarkupLine($"[green]Active profile → '{Markup.Escape(name)}'.[/]");
            return 0;
        });

        var deleteArg = new Argument<string>("name");
        var delete = new Command("delete", "Delete a profile");
        delete.Add(deleteArg);
        delete.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var name = parse.GetRequiredValue(deleteArg);
            var config = rt.ConfigStore.Config;
            if (name.Equals(config.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[red]Cannot delete the active profile.[/]");
                return 1;
            }
            if (!config.Profiles.Remove(name))
            {
                AnsiConsole.MarkupLine("[red]Profile not found.[/]");
                return 1;
            }
            rt.ConfigStore.Save();
            AnsiConsole.MarkupLine($"[green]Deleted '{Markup.Escape(name)}'.[/]");
            return 0;
        });

        command.Add(list); command.Add(create); command.Add(use); command.Add(delete);
        return command;
    }
}

public static class UsageCommands
{
    public static Command Build()
    {
        var daysOption = new Option<int?>("--days") { Description = "Only include the last N days" };
        var command = new Command("usage", "Token, cost and latency statistics");
        command.Add(daysOption);
        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderUsage(rt, parse.GetValue(daysOption));
            return 0;
        });
        return command;
    }

    internal static void RenderUsage(SeekClawRuntime rt, int? days)
    {
        var since = days is { } d ? DateTimeOffset.UtcNow.AddDays(-d) : (DateTimeOffset?)null;
        var aggregates = rt.Usage.Aggregate(since);
        if (aggregates.Count == 0)
        {
            AnsiConsole.MarkupLine("[gray]No usage recorded yet.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Provider").AddColumn("Model").AddColumn("Calls").AddColumn("Success")
             .AddColumn("In tok").AddColumn("Out tok").AddColumn("Cost").AddColumn("Avg ms");

        foreach (var a in aggregates)
            table.AddRow(
                Markup.Escape(a.Provider), Markup.Escape(a.Model),
                a.Calls.ToString("N0"), $"{a.SuccessRate:P0}",
                a.InputTokens.ToString("N0"), a.OutputTokens.ToString("N0"),
                $"${a.Cost:0.####}", $"{a.AvgLatencyMs:0}");

        table.AddRow("[bold]total[/]", "",
            $"[bold]{aggregates.Sum(a => a.Calls):N0}[/]", "",
            $"[bold]{aggregates.Sum(a => a.InputTokens):N0}[/]",
            $"[bold]{aggregates.Sum(a => a.OutputTokens):N0}[/]",
            $"[bold]${aggregates.Sum(a => a.Cost):0.####}[/]", "");
        AnsiConsole.Write(table);
    }
}

public static class DoctorCommand
{
    public static Command Build()
    {
        var command = new Command("doctor", "Diagnose configuration, providers and workspace health");
        command.SetAction(async (_, ct) =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Check").AddColumn("Status").AddColumn("Detail");

            table.AddRow("Config", "[green]ok[/]", Markup.Escape(SeekClawPaths.ConfigFile));
            table.AddRow("Workspace", "[green]ok[/]",
                Markup.Escape($"{rt.Workspace.Root} [{string.Join(", ", rt.Workspace.ProjectKinds)}]"));

            var promptsFound = rt.Prompts.TryGet("system/default") is not null;
            table.AddRow("Prompts", promptsFound ? "[green]ok[/]" : "[red]missing[/]",
                promptsFound ? "system/default resolved" : "system/default.txt not found in any prompts/ root");

            if (config.Providers.Count == 0)
            {
                table.AddRow("Providers", "[yellow]none[/]", "run 'seekclaw provider add'");
            }
            else
            {
                foreach (var provider in config.Providers.Where(p => p.Enabled))
                {
                    var report = await rt.Health.CheckAsync(provider, ct);
                    var keyState = string.IsNullOrWhiteSpace(provider.ResolveApiKey()) ? " [yellow](no api key)[/]" : "";
                    table.AddRow(
                        $"Provider {Markup.Escape(provider.Id)}",
                        report.Online ? "[green]online[/]" : "[red]offline[/]",
                        $"{report.LatencyMs:0} ms — {Markup.Escape(report.Detail)}{keyState}");
                }
            }

            try
            {
                var active = rt.Providers.ResolveActive(rt.Workspace.Config);
                table.AddRow("Active model", "[green]ok[/]", Markup.Escape(active.Ref));
            }
            catch (Exception ex)
            {
                table.AddRow("Active model", "[red]none[/]", Markup.Escape(ex.Message));
            }

            AnsiConsole.Write(table);
            return 0;
        });
        return command;
    }
}

public static class SwitchCommand
{
    public static Command Build()
    {
        var command = new Command("switch", "Interactively switch provider, model and routing strategy");
        command.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;
            var enabledProviders = config.Providers.Where(p => p.Enabled && p.Models.Count > 0).ToList();
            if (enabledProviders.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No providers with models configured.[/] Run [cyan]seekclaw provider add[/].");
                return 1;
            }

            var providerId = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Provider:")
                .AddChoices(enabledProviders.Select(p => p.Id)));
            var provider = config.FindProvider(providerId)!;

            var modelId = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Model:")
                .AddChoices(provider.Models.Select(m => m.Id)));

            var strategy = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Routing strategy (used when the model is unavailable):")
                .AddChoices("balanced", "fast", "quality", "cheap", "offline"));

            var profile = config.GetActiveProfile();
            profile.Provider = providerId;
            profile.Model = modelId;
            profile.Strategy = strategy;
            rt.ConfigStore.Save();

            AnsiConsole.MarkupLine($"[green]Switched to {Markup.Escape(providerId)}/{Markup.Escape(modelId)}[/] (strategy: {strategy})");
            return 0;
        });
        return command;
    }
}

public static class InitCommand
{
    public static Command Build()
    {
        var command = new Command("init", "Bootstrap the workspace (.cache, .session, logs, skills, mcp, docs, .gitignore)");
        command.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var created = rt.Workspaces.Bootstrap(rt.Workspace);
            if (created.Count == 0)
                AnsiConsole.MarkupLine("[gray]Workspace already initialized.[/]");
            else
                foreach (var item in created)
                    AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(item)}");
            AnsiConsole.MarkupLine($"Workspace: [cyan]{Markup.Escape(rt.Workspace.Root)}[/] {Markup.Escape($"[{string.Join(", ", rt.Workspace.ProjectKinds)}]")}");
            return 0;
        });
        return command;
    }
}

public static class SkillCommands
{
    public static Command Build()
    {
        var command = new Command("skill", "Manage skills (directory-based agent extensions)");

        var list = new Command("list", "List discovered skills");
        list.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var skills = rt.Skills.Discover(rt.Workspace);
            if (skills.Count == 0)
            {
                AnsiConsole.MarkupLine($"[gray]No skills found.[/] Drop a folder with skill.yaml + prompt.txt into "
                    + $"[cyan]{Markup.Escape(rt.Workspace.SkillsDir)}[/] or [cyan]{Markup.Escape(SeekClawPaths.SkillsDir)}[/].");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Skill").AddColumn("Version").AddColumn("Enabled").AddColumn("Description").AddColumn("Location");
            foreach (var skill in skills)
                table.AddRow(
                    Markup.Escape(skill.Name),
                    Markup.Escape(skill.Manifest.Version ?? "-"),
                    skill.Enabled ? "[green]yes[/]" : "[red]no[/]",
                    Markup.Escape(skill.Manifest.Description ?? ""),
                    Markup.Escape(skill.Directory));
            AnsiConsole.Write(table);
            return 0;
        });

        var enableArg = new Argument<string>("name");
        var enable = new Command("enable", "Enable a skill");
        enable.Add(enableArg);
        enable.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            rt.Skills.SetEnabled(parse.GetRequiredValue(enableArg), true);
            AnsiConsole.MarkupLine("[green]Enabled.[/]");
            return 0;
        });

        var disableArg = new Argument<string>("name");
        var disable = new Command("disable", "Disable a skill");
        disable.Add(disableArg);
        disable.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            rt.Skills.SetEnabled(parse.GetRequiredValue(disableArg), false);
            AnsiConsole.MarkupLine("[yellow]Disabled.[/]");
            return 0;
        });

        command.Add(list); command.Add(enable); command.Add(disable);
        return command;
    }
}

public static class McpCommands
{
    public static Command Build()
    {
        var command = new Command("mcp", "Manage MCP servers");

        var list = new Command("list", "List configured MCP servers");
        list.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var servers = rt.Mcp.LoadServerConfigs(rt.Workspace);
            if (servers.Count == 0)
            {
                AnsiConsole.MarkupLine("[gray]No MCP servers configured.[/] Add them under [cyan]mcp.servers[/] in "
                    + "~/.seekclaw/config.json or <workspace>/mcp/servers.json.");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Server").AddColumn("Transport").AddColumn("Target").AddColumn("Enabled");
            foreach (var (name, server) in servers)
                table.AddRow(
                    Markup.Escape(name),
                    Markup.Escape(server.Transport),
                    Markup.Escape(server.Url ?? $"{server.Command} {string.Join(' ', server.Args ?? [])}"),
                    server.Enabled ? "[green]yes[/]" : "[red]no[/]");
            AnsiConsole.Write(table);
            return 0;
        });

        var test = new Command("test", "Connect to every server and list its tools");
        test.SetAction(async (_, ct) =>
        {
            await using var rt = CliHost.CreateRuntime();
            var statuses = await rt.ConnectMcpAsync(ct);
            if (statuses.Count == 0)
            {
                AnsiConsole.MarkupLine("[gray]No MCP servers configured.[/]");
                return 0;
            }
            foreach (var status in statuses)
                AnsiConsole.MarkupLine(status.Connected
                    ? $"[green]✓ {Markup.Escape(status.Name)}[/] ({status.Transport}) — {status.ToolCount} tools"
                    : $"[red]✗ {Markup.Escape(status.Name)}[/] ({status.Transport}) — {Markup.Escape(status.Error ?? "")}");
            return 0;
        });

        command.Add(list); command.Add(test);
        return command;
    }
}

public static class SessionCommands
{
    public static Command Build()
    {
        var command = new Command("sessions", "List saved sessions for this workspace");
        command.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var sessions = rt.Sessions.List(rt.Workspace);
            if (sessions.Count == 0)
            {
                AnsiConsole.MarkupLine("[gray]No sessions yet.[/]");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Id").AddColumn("Created").AddColumn("Title");
            foreach (var session in sessions.Take(30))
                table.AddRow(
                    Markup.Escape(session.Id),
                    session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Markup.Escape(session.Title ?? "-"));
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("Resume with [cyan]seekclaw chat --resume <id>[/].");
            return 0;
        });
        return command;
    }
}

public static class DaemonCommand
{
    public static Command Build()
    {
        var command = new Command("daemon", "Run SeekClaw as a background service (named pipe / unix socket)");
        command.SetAction(async (_, ct) =>
        {
            await using var rt = CliHost.CreateRuntime();
            var mcpConnection = rt.ConnectMcpAsync(ct);
            var endpoint = OperatingSystem.IsWindows()
                ? $@"\\.\pipe\{DaemonServer.PipeName}"
                : DaemonServer.SocketPath;
            AnsiConsole.MarkupLine($"[green]SeekClaw daemon listening[/] on [cyan]{Markup.Escape(endpoint)}[/] (ctrl+c to stop)");
            var daemon = new DaemonServer(rt).RunAsync(ct);
            try { await Task.WhenAll(daemon, mcpConnection); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return 0;
        });
        return command;
    }
}
