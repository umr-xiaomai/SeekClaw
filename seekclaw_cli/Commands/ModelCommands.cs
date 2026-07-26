using System.CommandLine;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Providers;
using Spectre.Console;

namespace SeekClaw.Cli.Commands;

public static class ModelCommands
{
    public static Command Build()
    {
        var command = new Command("model", "Manage models (list / use / info / search / test / stats)");
        command.Add(BuildList());
        command.Add(BuildUse());
        command.Add(BuildInfo());
        command.Add(BuildSearch());
        command.Add(BuildTest());
        command.Add(BuildStats());
        return command;
    }

    internal static void RenderModelTable(SeekClawRuntime rt, IReadOnlyList<ModelInfo> models)
    {
        string? activeRef = null;
        try { activeRef = rt.Providers.ResolveActive(rt.Workspace.Config).Ref; }
        catch (Exception) { }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("").AddColumn("Model").AddColumn("Alias").AddColumn("Context")
             .AddColumn("Max out").AddColumn("Capabilities").AddColumn("$/MTok in·out").AddColumn("Tags");

        foreach (var model in models)
        {
            var caps = model.Capabilities;
            var capText = string.Join(" ", new[]
            {
                caps.Streaming ? "stream" : null,
                caps.ToolCalling ? "tools" : null,
                caps.Thinking ? "think" : null,
                caps.Vision ? "vision" : null,
                caps.JsonMode ? "json" : null,
                caps.Reasoning ? "reason" : null,
                caps.Embedding ? "embed" : null,
            }.Where(c => c is not null));

            table.AddRow(
                model.Ref.Equals(activeRef, StringComparison.OrdinalIgnoreCase) ? "[green]●[/]" : "",
                Markup.Escape(model.Ref),
                Markup.Escape(model.Model.Alias ?? ""),
                $"{model.Model.ContextWindow / 1000}k",
                $"{model.Model.MaxOutput / 1000}k",
                Markup.Escape(capText),
                model.Model.InputPricePerMTok > 0 || model.Model.OutputPricePerMTok > 0
                    ? $"{model.Model.InputPricePerMTok:0.##}·{model.Model.OutputPricePerMTok:0.##}"
                    : "-",
                Markup.Escape(string.Join(",", model.Model.Tags ?? [])));
        }
        AnsiConsole.Write(table);
    }

    private static Command BuildList()
    {
        var command = new Command("list", "List all registered models");
        command.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            var models = rt.Models.All(includeDisabledProviders: true);
            if (models.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No models registered.[/] Add providers/models in [cyan]~/.seekclaw/config.json[/].");
                return 0;
            }
            RenderModelTable(rt, models);
            return 0;
        });
        return command;
    }

    private static Command BuildUse()
    {
        var refArg = new Argument<string>("model") { Description = "\"provider/model\", alias, or unique model id" };
        var command = new Command("use", "Make a model the active one for the current profile");
        command.Add(refArg);
        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var model = rt.Models.Resolve(parse.GetRequiredValue(refArg));
            if (model is null)
            {
                AnsiConsole.MarkupLine("[red]Model not found or ambiguous.[/] Try [cyan]seekclaw model search <query>[/].");
                return 1;
            }
            var profile = rt.ConfigStore.Config.GetActiveProfile();
            profile.Provider = model.Provider.Id;
            profile.Model = model.Model.Id;
            rt.ConfigStore.Save();
            AnsiConsole.MarkupLine($"[green]Active model → {Markup.Escape(model.Ref)}[/]");
            return 0;
        });
        return command;
    }

    private static Command BuildInfo()
    {
        var refArg = new Argument<string>("model");
        var command = new Command("info", "Show model details");
        command.Add(refArg);
        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var model = rt.Models.Resolve(parse.GetRequiredValue(refArg));
            if (model is null)
            {
                AnsiConsole.MarkupLine("[red]Model not found.[/]");
                return 1;
            }

            var caps = model.Capabilities;
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[bold]Reference[/]", Markup.Escape(model.Ref));
            grid.AddRow("Provider", $"{Markup.Escape(model.Provider.DisplayName)} ({Markup.Escape(model.Provider.Kind)})");
            grid.AddRow("Base URL", Markup.Escape(model.Provider.BaseUrl));
            grid.AddRow("Alias", Markup.Escape(model.Model.Alias ?? "-"));
            grid.AddRow("Context window", $"{model.Model.ContextWindow:N0} tokens");
            grid.AddRow("Max output", $"{model.Model.MaxOutput:N0} tokens");
            grid.AddRow("Streaming", Yn(caps.Streaming));
            grid.AddRow("Tool calling", Yn(caps.ToolCalling));
            grid.AddRow("Thinking", Yn(caps.Thinking));
            grid.AddRow("Vision", Yn(caps.Vision));
            grid.AddRow("JSON mode", Yn(caps.JsonMode));
            grid.AddRow("Reasoning", Yn(caps.Reasoning));
            grid.AddRow("Embedding", Yn(caps.Embedding));
            grid.AddRow("MCP", Yn(caps.Mcp));
            grid.AddRow("Price in/out", $"${model.Model.InputPricePerMTok}/{model.Model.OutputPricePerMTok} per MTok");
            grid.AddRow("Tags", Markup.Escape(string.Join(", ", model.Model.Tags ?? [])));
            AnsiConsole.Write(new Panel(grid).Header($" {Markup.Escape(model.Model.Id)} ").Border(BoxBorder.Rounded));
            return 0;
        });
        return command;

        static string Yn(bool value) => value ? "[green]yes[/]" : "[gray]no[/]";
    }

    private static Command BuildSearch()
    {
        var queryArg = new Argument<string>("query");
        var command = new Command("search", "Search models by name, alias or tag");
        command.Add(queryArg);
        command.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var matches = rt.Models.Search(parse.GetRequiredValue(queryArg));
            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No models matched.[/]");
                return 0;
            }
            RenderModelTable(rt, matches);
            return 0;
        });
        return command;
    }

    private static Command BuildTest()
    {
        var refArg = new Argument<string>("model");
        var command = new Command("test", "Send a minimal real completion through the model");
        command.Add(refArg);
        command.SetAction(async (parse, ct) =>
        {
            using var rt = CliHost.CreateRuntime();
            var model = rt.Models.Resolve(parse.GetRequiredValue(refArg));
            if (model is null)
            {
                AnsiConsole.MarkupLine("[red]Model not found.[/]");
                return 1;
            }

            (bool Success, string Detail, double LatencyMs) result = default;
            await AnsiConsole.Status().StartAsync($"Testing {model.Ref}…", async _ =>
            {
                result = await rt.Providers.TestModelAsync(model, ct);
            });

            var detail = Markup.Escape(result.Detail ?? "");
            AnsiConsole.MarkupLine(result.Success
                ? $"[green]✓ {Markup.Escape(model.Ref)}[/] answered in {result.LatencyMs:0} ms: {detail}"
                : $"[red]✗ {Markup.Escape(model.Ref)}[/]: {detail}");
            return result.Success ? 0 : 1;
        });
        return command;
    }

    private static Command BuildStats()
    {
        var command = new Command("stats", "Per-model usage statistics");
        command.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            UsageCommands.RenderUsage(rt, null);
            return 0;
        });
        return command;
    }
}
