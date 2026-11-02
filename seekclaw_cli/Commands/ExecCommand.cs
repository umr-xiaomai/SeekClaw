using System.CommandLine;
using System.Text.Json.Nodes;
using Json.Schema;
using SeekClaw.Runtime;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;

namespace SeekClaw.Cli.Commands;

internal sealed record ExecOptions(string Prompt, bool Json = false, string? OutputFile = null,
    string? SchemaFile = null, string? Resume = null, bool Continue = false, bool Worktree = false,
    string BaseRef = "HEAD", string? Model = null, string Mode = "guardrail", int TimeoutSeconds = 1800);

/// <summary>A terminal-free automation surface. stdout is final output or versioned JSONL events.</summary>
public static class ExecCommand
{
    public static Command Build()
    {
        var command = new Command("exec", "Run a non-interactive task for scripts and CI");
        var prompt = new Argument<string[]>("prompt") { Arity = ArgumentArity.ZeroOrMore };
        var json = new Option<bool>("--json") { Description = "Write versioned JSONL events to stdout" };
        var output = new Option<string?>("--output-last-message", "-o");
        var schema = new Option<string?>("--output-schema");
        var resume = new Option<string?>("--resume");
        var latest = new Option<bool>("--continue");
        var worktree = new Option<bool>("--worktree");
        var revision = new Option<string?>("--base-ref");
        var model = new Option<string?>("--model", "-m");
        var mode = new Option<string?>("--mode") { Description = "guardrail (default), readonly, plan, edit, auto, manual, full" };
        var timeout = new Option<int?>("--timeout-seconds");
        command.Add(prompt);
        foreach (var option in new Option[] { json, output, schema, resume, latest, worktree, revision, model, mode, timeout }) command.Add(option);
        command.SetAction(async (parse, ct) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                await using var runtime = CliHost.CreateRuntime();
                return await RunAsync(runtime, new ExecOptions(string.Join(' ', parse.GetValue(prompt) ?? []),
                    parse.GetValue(json), parse.GetValue(output), parse.GetValue(schema), parse.GetValue(resume),
                    parse.GetValue(latest), parse.GetValue(worktree), parse.GetValue(revision) ?? "HEAD",
                    parse.GetValue(model), parse.GetValue(mode) ?? "guardrail", parse.GetValue(timeout) ?? 1800),
                    Console.IsInputRedirected ? Console.In : null, Console.Out, Console.Error, cancel.Token).ConfigureAwait(false);
            }
            finally { Console.CancelKeyPress -= handler; }
        });
        return command;
    }

    internal static async Task<int> RunAsync(SeekClawRuntime runtime, ExecOptions options, TextReader? input,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        string? sessionId = null;
        try
        {
            if (options.Resume is not null && options.Continue) throw new ArgumentException("Choose either --resume or --continue.");
            if (options.TimeoutSeconds is < 1 or > 86_400) throw new ArgumentException("Timeout must be between 1 and 86400 seconds.");
            if (options.Mode is not ("guardrail" or "manual" or "readonly" or "plan" or "edit" or "auto" or "full"))
                throw new ArgumentException("Unknown execution mode.");
            var prompt = options.Prompt == "-" ? "" : options.Prompt;
            if (input is not null)
            {
                var context = await ReadInputAsync(input, ct).ConfigureAwait(false);
                prompt = string.IsNullOrWhiteSpace(prompt) ? context : prompt + "\n\n<piped_context>\n" + context + "\n</piped_context>";
            }
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt or piped input is required.");
            JsonSchema? schema = null;
            if (options.SchemaFile is not null)
            {
                string schemaText;
                try
                {
                    if (new FileInfo(options.SchemaFile).Length > 1024 * 1024) throw new ArgumentException("Output schema exceeds 1 MiB.");
                    schemaText = await File.ReadAllTextAsync(options.SchemaFile, ct).ConfigureAwait(false);
                    schema = JsonSchema.FromText(schemaText);
                    // Build constraints before running the model so invalid schemas and
                    // unresolved references are argument errors, not late output failures.
                    schema.Evaluate(null, new EvaluationOptions { ValidateAgainstMetaSchema = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.Text.Json.JsonException or JsonSchemaException)
                { throw new ArgumentException("Invalid --output-schema: " + ex.Message, ex); }
                prompt += "\n\nReturn the final answer as JSON conforming to this JSON Schema. Do not wrap it in Markdown.\n" + schemaText;
            }
            if (options.Model is { } requested)
            {
                var model = runtime.Models.Resolve(requested) ?? throw new ArgumentException($"Unknown model: {requested}");
                runtime.ConfigStore.Config.Provider = model.Provider.Id; runtime.ConfigStore.Config.Model = model.Model.Id;
            }
            runtime.ConfigStore.Config.Agent.Mode = options.Mode;
            var workspace = runtime.Workspace;
            // Workspace configuration must not override an explicit command-line permission choice.
            if (workspace.Config is not null) workspace.Config.Mode = options.Mode;
            if (workspace.Config is not null && options.Model is not null)
            {
                workspace.Config.Provider = runtime.ConfigStore.Config.Provider;
                workspace.Config.Model = runtime.ConfigStore.Config.Model;
            }
            var session = options.Resume is not null
                ? runtime.Sessions.Load(workspace, options.Resume) ?? throw new ArgumentException("Session not found.")
                : options.Continue ? runtime.Sessions.LoadLatest(workspace) ?? throw new ArgumentException("No session to continue.")
                : runtime.Sessions.Create(workspace, networkEnabled: runtime.ConfigStore.Config.Agent.NetworkEnabled);
            sessionId = session.Header.Id;
            if (options.Worktree && runtime.Worktrees.Get(workspace.Root, session.Header.Id) is null)
                await runtime.Worktrees.CreateAsync(workspace.Root, session.Header.Id, options.BaseRef, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var subscription = runtime.Events.Subscribe();
            if (options.Json) await WriteJsonAsync(output, new JsonObject
            { ["type"] = "thread.started", ["sessionId"] = session.Header.Id,
                ["workspace"] = runtime.Worktrees.Resolve(workspace, session.Header.Id).Root }).ConfigureAwait(false);
            var pump = PumpEventsAsync(subscription, output, error, options.Json);
            var approvalDenied = 0;
            runtime.Approvals.SetHandler((_, _) =>
            {
                Interlocked.Exchange(ref approvalDenied, 1);
                return ValueTask.FromResult(ToolApprovalDecision.Deny("Non-interactive execution requires an explicit mode that permits this action."));
            });
            AgentTurnResult result;
            try
            {
                await runtime.ConnectMcpAsync(timeout.Token).ConfigureAwait(false);
                result = await runtime.Agent.RunTurnAsync(session, workspace, prompt, timeout.Token).ConfigureAwait(false);
                if (result.Maintenance is { } maintenance) await maintenance.ConfigureAwait(false);
            }
            finally { runtime.Approvals.ClearHandler(); subscription.Dispose(); await pump.ConfigureAwait(false); }
            var code = result.Cancelled ? 130 : result.Error is null ? 0 : 1;
            var executionError = result.Error;
            if (code == 0 && approvalDenied != 0)
            { code = 1; executionError = "An action required interactive approval. Select an appropriate --mode for unattended execution."; }
            var final = result.Text;
            if (code == 0 && schema is not null)
            {
                try
                {
                    var node = JsonNode.Parse(final);
                    var validation = schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
                    if (!validation.IsValid) throw new InvalidDataException("Final output does not conform to --output-schema.");
                    final = node?.ToJsonString() ?? "null";
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
                { executionError = ex.Message; code = 3; }
            }
            if (code == 0 && options.OutputFile is not null) await WriteOutputAsync(options.OutputFile, final, ct).ConfigureAwait(false);
            if (options.Json) await WriteJsonAsync(output, new JsonObject
            { ["type"] = code == 0 ? "turn.completed" : "turn.failed", ["sessionId"] = session.Header.Id,
                ["output"] = final, ["exitCode"] = code, ["error"] = executionError }).ConfigureAwait(false);
            else if (code == 0) await output.WriteLineAsync(final).ConfigureAwait(false);
            else await error.WriteLineAsync(executionError ?? (result.Cancelled ? "Task cancelled or timed out." : "Output validation failed.")).ConfigureAwait(false);
            return code;
        }
        catch (OperationCanceledException)
        {
            if (options.Json) await WriteJsonAsync(output, new JsonObject
            { ["type"] = "turn.failed", ["sessionId"] = sessionId, ["exitCode"] = 130, ["error"] = "Task cancelled or timed out." }).ConfigureAwait(false);
            await error.WriteLineAsync("Task cancelled or timed out.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex)
        {
            var code = ex is ArgumentException ? 2 : 1;
            if (options.Json) await WriteJsonAsync(output, new JsonObject
            { ["type"] = "turn.failed", ["sessionId"] = sessionId, ["exitCode"] = code, ["error"] = ex.Message }).ConfigureAwait(false);
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return code;
        }
    }

    private static async Task<string> ReadInputAsync(TextReader input, CancellationToken ct)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > 4 * 1024 * 1024) throw new ArgumentException("Piped input exceeds 4 MiB of text.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static async Task WriteOutputAsync(string path, string text, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, text + Environment.NewLine, ct).ConfigureAwait(false); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task PumpEventsAsync(IEventSubscription subscription, TextWriter output, TextWriter error, bool json)
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var item = ProjectEvent(evt);
            if (json) { if (item is not null) await WriteJsonAsync(output, item).ConfigureAwait(false); }
            else if (evt is ToolCallStartedEvent tool) await error.WriteLineAsync($"> {tool.ToolName}: {tool.ArgumentSummary}").ConfigureAwait(false);
            else if (evt is StatusEvent status) await error.WriteLineAsync(status.Status).ConfigureAwait(false);
            else if (evt is WarningEvent warning) await error.WriteLineAsync(warning.Message).ConfigureAwait(false);
        }
    }

    internal static JsonObject? ProjectEvent(RuntimeEvent evt) => evt switch
    {
        TurnStartedEvent => new() { ["type"] = "turn.started" },
        AssistantTextDeltaEvent delta => new() { ["type"] = "message.delta", ["text"] = delta.Delta },
        AssistantStreamResetEvent reset => new() { ["type"] = "message.reset", ["model"] = reset.ModelRef,
            ["textLength"] = reset.TextLength, ["thinkingLength"] = reset.ThinkingLength },
        ProviderRetryEvent retry => new() { ["type"] = "provider.retry", ["model"] = retry.ModelRef,
            ["attempt"] = retry.Attempt, ["reason"] = retry.Reason, ["delayMs"] = retry.Delay.TotalMilliseconds },
        AssistantMessageCompletedEvent message => new() { ["type"] = "message.completed", ["text"] = message.Text },
        ToolCallStartedEvent tool => new() { ["type"] = "tool.started", ["id"] = tool.CallId, ["name"] = tool.ToolName, ["arguments"] = tool.ArgumentSummary },
        ToolCallProgressEvent progress => new() { ["type"] = "tool.output", ["id"] = progress.CallId, ["text"] = progress.Message },
        ToolCallCompletedEvent tool => new() { ["type"] = "tool.completed", ["id"] = tool.CallId, ["success"] = tool.Success, ["summary"] = tool.ResultSummary },
        FileDiffEvent diff => new() { ["type"] = "file.changed", ["path"] = diff.FilePath, ["diff"] = diff.UnifiedDiff },
        UsageRecordedEvent usage => new() { ["type"] = "usage", ["inputTokens"] = usage.InputTokens, ["outputTokens"] = usage.OutputTokens, ["cost"] = usage.Cost },
        WarningEvent warning => new() { ["type"] = "warning", ["message"] = warning.Message },
        ErrorEvent error => new() { ["type"] = "error", ["message"] = error.Message },
        _ => null,
    };
    private static Task WriteJsonAsync(TextWriter output, JsonObject item)
    { item["version"] = 1; return output.WriteLineAsync(item.ToJsonString()); }
}
