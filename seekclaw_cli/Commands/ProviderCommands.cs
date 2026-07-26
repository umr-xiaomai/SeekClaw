using System.CommandLine;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Configuration;
using Spectre.Console;

namespace SeekClaw.Cli.Commands;

public static class ProviderCommands
{
    public static Command Build()
    {
        var command = new Command("provider", "Manage LLM providers (list / add / remove / edit / test / use)");
        command.Add(BuildList());
        command.Add(BuildAdd());
        command.Add(BuildRemove());
        command.Add(BuildEdit());
        command.Add(BuildTest());
        command.Add(BuildUse());
        return command;
    }

    private static Command BuildList()
    {
        var command = new Command("list", "List configured providers");
        command.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;
            var profile = config.GetActiveProfile();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("").AddColumn("Id").AddColumn("Kind").AddColumn("Base URL")
                 .AddColumn("Key").AddColumn("Models").AddColumn("Enabled").AddColumn("Priority");

            foreach (var provider in config.Providers.OrderBy(p => p.Priority))
            {
                var active = string.Equals(profile.Provider, provider.Id, StringComparison.OrdinalIgnoreCase);
                var hasKey = !string.IsNullOrWhiteSpace(provider.ResolveApiKey());
                table.AddRow(
                    active ? "[green]●[/]" : "",
                    Markup.Escape(provider.Id),
                    Markup.Escape(provider.Kind),
                    Markup.Escape(provider.BaseUrl),
                    hasKey ? "[green]set[/]" : "[yellow]missing[/]",
                    provider.Models.Count.ToString(),
                    provider.Enabled ? "[green]yes[/]" : "[red]no[/]",
                    provider.Priority.ToString());
            }

            if (config.Providers.Count == 0)
                AnsiConsole.MarkupLine("[yellow]No providers configured.[/] Run [cyan]seekclaw provider add[/].");
            else
                AnsiConsole.Write(table);
            return 0;
        });
        return command;
    }

    private static Command BuildAdd()
    {
        var idOption = new Option<string?>("--id") { Description = "Provider id (e.g. openai, anthropic, ollama)" };
        var kindOption = new Option<string?>("--kind") { Description = "Wire protocol: openai | anthropic" };
        var baseUrlOption = new Option<string?>("--base-url") { Description = "API base URL" };
        var apiKeyOption = new Option<string?>("--api-key") { Description = "API key (stored in config)" };
        var apiKeyEnvOption = new Option<string?>("--api-key-env") { Description = "Environment variable holding the key" };
        var modelOption = new Option<string[]>("--model") { Description = "Model id to register (repeatable)", AllowMultipleArgumentsPerToken = true };

        var command = new Command("add", "Add a provider (interactive when no options are given)");
        command.Add(idOption); command.Add(kindOption); command.Add(baseUrlOption);
        command.Add(apiKeyOption); command.Add(apiKeyEnvOption); command.Add(modelOption);

        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;

            var id = parse.GetValue(idOption);
            string kind; string baseUrl; string? apiKey; string? apiKeyEnv;
            var models = parse.GetValue(modelOption) ?? [];

            if (string.IsNullOrWhiteSpace(id))
            {
                id = AnsiConsole.Prompt(new TextPrompt<string>("Provider id:"));
                kind = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title("Wire protocol:")
                    .AddChoices("openai", "anthropic"));
                baseUrl = AnsiConsole.Prompt(new TextPrompt<string>("Base URL:")
                    .DefaultValue(kind == "anthropic" ? "https://api.anthropic.com" : "https://api.openai.com/v1"));
                apiKey = AnsiConsole.Prompt(new TextPrompt<string>("API key (empty to use an env var):")
                    .AllowEmpty().Secret());
                apiKeyEnv = string.IsNullOrWhiteSpace(apiKey)
                    ? AnsiConsole.Prompt(new TextPrompt<string>("API key environment variable:").AllowEmpty())
                    : null;

                var modelList = new List<string>();
                while (true)
                {
                    var modelId = AnsiConsole.Prompt(new TextPrompt<string>("Add model id (empty to finish):").AllowEmpty());
                    if (string.IsNullOrWhiteSpace(modelId)) break;
                    modelList.Add(modelId.Trim());
                }
                models = [.. modelList];
            }
            else
            {
                kind = parse.GetValue(kindOption) ?? "openai";
                baseUrl = parse.GetValue(baseUrlOption) ?? "";
                apiKey = parse.GetValue(apiKeyOption);
                apiKeyEnv = parse.GetValue(apiKeyEnvOption);
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    AnsiConsole.MarkupLine("[red]--base-url is required.[/]");
                    return 1;
                }
            }

            if (config.FindProvider(id!) is not null)
            {
                AnsiConsole.MarkupLine($"[red]Provider '{Markup.Escape(id!)}' already exists.[/] Use [cyan]provider edit[/].");
                return 1;
            }

            var provider = new ProviderConfig
            {
                Id = id!.Trim(),
                Kind = kind.Trim().ToLowerInvariant(),
                BaseUrl = baseUrl.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                ApiKeyEnv = string.IsNullOrWhiteSpace(apiKeyEnv) ? null : apiKeyEnv,
                Models = models.Select(m => new ModelConfig { Id = m }).ToList(),
            };
            config.Providers.Add(provider);
            rt.ConfigStore.Save();

            AnsiConsole.MarkupLine($"[green]Added provider '{Markup.Escape(provider.Id)}'[/] with {provider.Models.Count} model(s).");
            if (provider.Models.Count == 0)
                AnsiConsole.MarkupLine("Add models by editing [cyan]~/.seekclaw/config.json[/] (contextWindow, capabilities, pricing…).");
            return 0;
        });
        return command;
    }

    private static Command BuildRemove()
    {
        var idArg = new Argument<string>("id") { Description = "Provider id to remove" };
        var command = new Command("remove", "Remove a provider");
        command.Add(idArg);
        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;
            var provider = config.FindProvider(parse.GetRequiredValue(idArg));
            if (provider is null)
            {
                AnsiConsole.MarkupLine("[red]Provider not found.[/]");
                return 1;
            }
            config.Providers.Remove(provider);
            rt.ConfigStore.Save();
            AnsiConsole.MarkupLine($"[green]Removed '{Markup.Escape(provider.Id)}'.[/]");
            return 0;
        });
        return command;
    }

    private static Command BuildEdit()
    {
        var idArg = new Argument<string>("id");
        var baseUrlOption = new Option<string?>("--base-url");
        var apiKeyOption = new Option<string?>("--api-key");
        var apiKeyEnvOption = new Option<string?>("--api-key-env");
        var enabledOption = new Option<bool?>("--enabled");
        var priorityOption = new Option<int?>("--priority");
        var timeoutOption = new Option<int?>("--timeout") { Description = "Request timeout in seconds" };
        var proxyOption = new Option<string?>("--proxy");

        var command = new Command("edit", "Edit provider fields");
        command.Add(idArg);
        foreach (var opt in new Option[] { baseUrlOption, apiKeyOption, apiKeyEnvOption, enabledOption, priorityOption, timeoutOption, proxyOption })
            command.Add(opt);

        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var provider = rt.ConfigStore.Config.FindProvider(parse.GetRequiredValue(idArg));
            if (provider is null)
            {
                AnsiConsole.MarkupLine("[red]Provider not found.[/]");
                return 1;
            }

            if (parse.GetValue(baseUrlOption) is { } baseUrl) provider.BaseUrl = baseUrl;
            if (parse.GetValue(apiKeyOption) is { } apiKey) provider.ApiKey = apiKey;
            if (parse.GetValue(apiKeyEnvOption) is { } apiKeyEnv) provider.ApiKeyEnv = apiKeyEnv;
            if (parse.GetValue(enabledOption) is { } enabled) provider.Enabled = enabled;
            if (parse.GetValue(priorityOption) is { } priority) provider.Priority = priority;
            if (parse.GetValue(timeoutOption) is { } timeout) provider.TimeoutSeconds = timeout;
            if (parse.GetValue(proxyOption) is { } proxy) provider.Proxy = proxy;

            rt.ConfigStore.Save();
            AnsiConsole.MarkupLine($"[green]Updated '{Markup.Escape(provider.Id)}'.[/]");
            return 0;
        });
        return command;
    }

    private static Command BuildTest()
    {
        var idArg = new Argument<string?>("id") { Description = "Provider id (all providers when omitted)", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("test", "Probe provider endpoints (health + latency)");
        command.Add(idArg);
        command.SetAction(async (parse, ct) =>
        {
            using var rt = CliHost.CreateRuntime();
            var id = parse.GetValue(idArg);
            var providers = rt.ConfigStore.Config.Providers
                .Where(p => id is null || p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (providers.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]No matching providers.[/]");
                return 1;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Provider").AddColumn("Status").AddColumn("Latency").AddColumn("Detail");

            foreach (var provider in providers)
            {
                var report = await rt.Health.CheckAsync(provider, ct);
                table.AddRow(
                    Markup.Escape(provider.Id),
                    report.Online ? "[green]online[/]" : "[red]offline[/]",
                    $"{report.LatencyMs:0} ms",
                    Markup.Escape(report.Detail));
            }
            AnsiConsole.Write(table);
            return 0;
        });
        return command;
    }

    private static Command BuildUse()
    {
        var idArg = new Argument<string>("id");
        var command = new Command("use", "Make a provider the active one for the current profile");
        command.Add(idArg);
        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var config = rt.ConfigStore.Config;
            var provider = config.FindProvider(parse.GetRequiredValue(idArg));
            if (provider is null)
            {
                AnsiConsole.MarkupLine("[red]Provider not found.[/]");
                return 1;
            }

            var profile = config.GetActiveProfile();
            profile.Provider = provider.Id;
            if (profile.Model is not null &&
                !provider.Models.Any(m => m.Id.Equals(profile.Model, StringComparison.OrdinalIgnoreCase)))
                profile.Model = provider.Models.FirstOrDefault()?.Id;
            rt.ConfigStore.Save();

            AnsiConsole.MarkupLine($"[green]Active provider → '{Markup.Escape(provider.Id)}'[/]"
                + (profile.Model is null ? " [yellow](no model selected — run 'seekclaw model use')[/]" : $" model [cyan]{Markup.Escape(profile.Model)}[/]"));
            return 0;
        });
        return command;
    }
}
