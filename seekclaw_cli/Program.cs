using System.CommandLine;
using SeekClaw.Cli;
using SeekClaw.Cli.Commands;
using Spectre.Console;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch (IOException) { /* redirected or headless output */ }

var root = new RootCommand("SeekClaw — industrial-grade AI agent runtime with a modern terminal front end");

var promptArg = new Argument<string[]>("prompt")
{
    Arity = ArgumentArity.ZeroOrMore,
    Description = "One-shot prompt; omit to start interactive chat",
};
var continueOption = new Option<bool>("--continue", "-c") { Description = "Continue the most recent session" };
var resumeOption = new Option<string?>("--resume") { Description = "Resume a session by id" };
var modelOption = new Option<string?>("--model", "-m") { Description = "Override the active model for this run (provider/model)" };

root.Add(promptArg);
root.Add(continueOption);
root.Add(resumeOption);
root.Add(modelOption);
root.SetAction((parse, _) => RunChatAsync(
    parse.GetValue(promptArg) ?? [],
    parse.GetValue(continueOption),
    parse.GetValue(resumeOption),
    parse.GetValue(modelOption)));

var chat = new Command("chat", "Interactive chat (the default command)");
chat.Add(continueOption);
chat.Add(resumeOption);
chat.Add(modelOption);
chat.SetAction((parse, _) => RunChatAsync(
    [],
    parse.GetValue(continueOption),
    parse.GetValue(resumeOption),
    parse.GetValue(modelOption)));
root.Add(chat);

root.Add(ProviderCommands.Build());
root.Add(ModelCommands.Build());
root.Add(ProfileCommands.Build());
root.Add(UsageCommands.Build());
root.Add(DoctorCommand.Build());
root.Add(SwitchCommand.Build());
root.Add(InitCommand.Build());
root.Add(SkillCommands.Build());
root.Add(McpCommands.Build());
root.Add(SessionCommands.Build());
root.Add(DaemonCommand.Build());

return await root.Parse(args).InvokeAsync();

static async Task<int> RunChatAsync(string[] promptWords, bool continueLast, string? resumeId, string? modelOverride)
{
    await using var runtime = CliHost.CreateRuntime();

    if (modelOverride is not null)
    {
        var model = runtime.Models.Resolve(modelOverride);
        if (model is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown model:[/] {Markup.Escape(modelOverride)}");
            return 1;
        }
        // In-memory override only — the saved profile is untouched.
        var profile = runtime.ConfigStore.Config.GetActiveProfile();
        profile.Provider = model.Provider.Id;
        profile.Model = model.Model.Id;
    }

    var loop = new ChatLoop(runtime);
    var prompt = string.Join(' ', promptWords).Trim();
    return prompt.Length > 0
        ? await loop.RunOneShotAsync(prompt, continueLast)
        : await loop.RunInteractiveAsync(continueLast, resumeId);
}
