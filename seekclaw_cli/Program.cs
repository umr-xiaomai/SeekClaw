using System.CommandLine;
using SeekClaw.Cli;
using SeekClaw.Cli.Commands;
using SeekClaw.Cli.Ui;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch (IOException) { /* redirected or headless output */ }

var root = new RootCommand("SeekClaw — industrial-grade AI agent runtime with a modern terminal front end");

var promptArg = new Argument<string[]>("prompt")
{
    Arity = ArgumentArity.ZeroOrMore,
    Description = "One-shot prompt; omit to read piped input or start interactive chat",
};
var continueOption = new Option<bool>("--continue", "-c") { Description = "Continue the most recent session" };
var resumeOption = new Option<string?>("--resume") { Description = "Resume a session by id" };
var modelOption = new Option<string?>("--model", "-m") { Description = "Override the active model for this run (provider/model)" };

root.Add(promptArg);
root.Add(continueOption);
root.Add(resumeOption);
root.Add(modelOption);
root.SetAction((parse, ct) => RunChatAsync(
    parse.GetValue(promptArg) ?? [],
    parse.GetValue(continueOption),
    parse.GetValue(resumeOption),
    parse.GetValue(modelOption), ct));

var chat = new Command("chat", "Interactive chat (the default command)");
chat.Add(continueOption);
chat.Add(resumeOption);
chat.Add(modelOption);
chat.SetAction((parse, ct) => RunChatAsync(
    [],
    parse.GetValue(continueOption),
    parse.GetValue(resumeOption),
    parse.GetValue(modelOption), ct));
root.Add(chat);

root.Add(ProviderCommands.Build());
root.Add(ModelCommands.Build());
root.Add(UsageCommands.Build());
root.Add(DoctorCommand.Build());
root.Add(SwitchCommand.Build());
root.Add(InitCommand.Build());
root.Add(SkillCommands.Build());
root.Add(McpCommands.Build());
root.Add(SessionCommands.Build());
root.Add(DaemonCommand.Build());
root.Add(ExecCommand.Build());
root.Add(WorktreeCommands.Build());

return await root.Parse(args).InvokeAsync();

static async Task<int> RunChatAsync(string[] promptWords, bool continueLast, string? resumeId, string? modelOverride, CancellationToken ct)
{
    var prompt = string.Join(' ', promptWords).Trim();
    var input = Console.IsInputRedirected ? Console.In : null;
    var useLiveTerminal = input is null && VirtualTerminal.TryEnable();
    if (!useLiveTerminal && prompt.Length == 0 && input is null)
    {
        Console.Error.WriteLine("A prompt or piped input is required when interactive terminal output is unavailable.");
        return 2;
    }

    await using var runtime = CliHost.CreateRuntime();

    if (modelOverride is not null)
    {
        var model = runtime.Models.Resolve(modelOverride);
        if (model is null)
        {
            Console.Error.WriteLine(Ansi.StripControls($"Unknown model: {modelOverride}"));
            return 1;
        }
        // In-memory override only — the saved config is untouched.
        var config = runtime.ConfigStore.Config;
        config.Provider = model.Provider.Id;
        config.Model = model.Model.Id;
    }

    var loop = new ChatLoop(runtime);
    if (!useLiveTerminal)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            return await loop.RunPlainAsync(prompt, continueLast, resumeId, input, Console.Out, Console.Error, cancel.Token);
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    return prompt.Length > 0
        ? await loop.RunOneShotAsync(prompt, continueLast, resumeId, ct)
        : await loop.RunInteractiveAsync(continueLast, resumeId, ct);
}
