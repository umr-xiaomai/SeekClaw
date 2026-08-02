using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Verification;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

public sealed record AgentTurnResult(string Text, bool Cancelled, string? Error);

/// <summary>
/// The agent loop: compose prompt → stream model → execute tools → repeat,
/// with automatic build verification and repair after file mutations.
/// All progress is published to the event bus; the agent never touches the console.
/// </summary>
public sealed class Agent(
    IConfigStore configStore,
    IProviderManager providerManager,
    IToolRegistry toolRegistry,
    IPromptProvider promptProvider,
    PromptComposer promptComposer,
    IWorkspaceManager workspaceManager,
    ISessionStore sessionStore,
    IVerifier verifier,
    IEventBus events,
    IFileLockCoordinator fileLocks,
    FileLockScope lockScope)
{
    private const string CompactionInstruction = """
        You are the memory compaction engine of SeekClaw, a coding agent. The conversation
        below is about to overflow the model's context window; write a concise progress
        summary that can replace it. Cover:
        - The user's goal and any hard constraints.
        - Files read or written (keep concrete paths), commands run, and their outcomes.
        - Decisions made and the current state of the work.
        - What remains to be done next.
        Stay factual; do not invent anything that is not in the conversation. The summary
        will be sent to the model as the start of the history, so prefer compact bullet
        points over prose.
        """;

    public async Task<AgentTurnResult> RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string userInput,
        CancellationToken ct,
        ReasoningLevel? reasoningLevel = null,
        IReadOnlyList<ChatImageAttachment>? images = null,
        AgentSteeringQueue? steering = null)
    {
        var agentConfig = configStore.Config.Agent;
        events.Publish(new TurnStartedEvent(session.Header.Id, userInput));
        var userMessage = ChatMessage.User(userInput, images);
        sessionStore.Append(session, userMessage);

        var mutated = false;
        var repairAttempts = 0;
        var finalText = "";
        string? error = null;
        var cancelled = false;

        try
        {
            // Repair iterations are extra model turns appended after a failed build
            // verification; they must not consume the main task's MaxSteps budget.
            // Reserve one extra step per allowed repair so long multi-step tasks are
            // not cut short by their own repair loop.
            var maxSteps = agentConfig.MaxSteps + Math.Clamp(agentConfig.MaxRepairAttempts, 0, 128);
            var compactedThisTurn = false;
            var truncatedSteps = 0;
            var reachedMaxSteps = false;
            var step = 0;
            while (true)
            {
                step++;
                if (step > maxSteps) { reachedMaxSteps = true; break; }
                ct.ThrowIfCancellationRequested();
                PublishSteering(AppendSteering(session, steering));

                // Only the current user input decides whether this turn needs vision. A
                // text-only follow-up must not be forced onto a vision model, and re-uploading
                // every earlier image would make it slow and force the non-streaming provider path.
                var requiresVision = userMessage.Images is { Count: > 0 };
                var model = requiresVision
                    ? providerManager.BuildCandidates(workspace.Config)
                        .FirstOrDefault(candidate => candidate.Model.Capabilities.Vision)
                      ?? throw new LlmException(
                          "The current routing profile has no model that supports image understanding.",
                          retryable: false)
                    : providerManager.ResolveActive(workspace.Config);
                var tools = ActiveTools(workspace, session.Header.NetworkEnabled);
                var systemPrompt = await ComposeSystemPromptAsync(workspace, model, tools, ct).ConfigureAwait(false);
                var source = requiresVision ? session.Messages : WithoutImages(session.Messages);
                var history = ContextPlanner.FitToWindow(source, model.Model, systemPrompt);
                // Context compaction: when plain trimming would have to drop history, first
                // summarize the old part so long turns keep their memory and can finish.
                // A failed compaction never aborts the turn — it falls back to the trim.
                if (agentConfig.EnableContextCompaction
                    && !compactedThisTurn
                    && history.Count + 4 <= source.Count)
                {
                    await CompactContextAsync(session, workspace, model, source, systemPrompt, ct).ConfigureAwait(false);
                    compactedThisTurn = true;
                    source = requiresVision ? session.Messages : WithoutImages(session.Messages);
                    history = ContextPlanner.FitToWindow(source, model.Model, systemPrompt);
                }

                events.Publish(new StatusEvent("Thinking"));
                if (step == 1 && userMessage.Images is { Count: > 0 })
                    foreach (var image in userMessage.Images)
                        events.Publish(new ImageViewedEvent(image.Id, image.Name, image.MediaType));
                events.Publish(new ModelInvocationStartedEvent(model.Provider.Id, model.Model.Id, step));

                var completion = await StreamOnceAsync(
                    model, workspace, systemPrompt, history, tools,
                    reasoningLevel ?? agentConfig.ReasoningLevel, requiresVision, ct).ConfigureAwait(false);

                var assistant = new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = completion.Text,
                    Thinking = completion.Thinking.Length > 0 ? completion.Thinking : null,
                    ToolCalls = completion.ToolCalls.Count > 0 ? completion.ToolCalls : null,
                    ViewedImages = step == 1
                        ? userMessage.Images?.Select(image => new ChatImageReference(image.Id, image.Name)).ToList()
                        : null,
                };
                sessionStore.Append(session, assistant);
                if (completion.Text.Length > 0)
                {
                    finalText = completion.Text;
                    events.Publish(new AssistantMessageCompletedEvent(completion.Text));
                }

                // Guidance can arrive while the model is streaming. Append it after the
                // current completion and continue with a fresh model step; the in-flight
                // request is never cancelled or rewritten.
                var guidance = AppendSteering(session, steering);

                // StreamOnceAsync preserves partial text on cancellation. Stop the turn after
                // persisting that text instead of reporting the cancelled request as completed.
                ct.ThrowIfCancellationRequested();

                if (completion.ToolCalls.Count > 0)
                {
                    var readOnlyBatch = new List<ToolCallRequest>();
                    foreach (var call in completion.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        var resolved = toolRegistry.Resolve(call.Name);
                        if (resolved is not null && !resolved.Mutating)
                        {
                            readOnlyBatch.Add(call);
                        }
                        else
                        {
                            if (readOnlyBatch.Count > 0)
                            {
                                var batchResults = await Task.WhenAll(readOnlyBatch.Select(c => ExecuteToolAsync(c, workspace, model, ct))).ConfigureAwait(false);
                                foreach (var result in batchResults)
                                {
                                    mutated |= result.ToolMutated;
                                    sessionStore.Append(session, result.Message);
                                }
                                readOnlyBatch.Clear();
                            }

                            var singleResult = await ExecuteToolAsync(call, workspace, model, ct).ConfigureAwait(false);
                            mutated |= singleResult.ToolMutated;
                            sessionStore.Append(session, singleResult.Message);
                        }
                    }

                    if (readOnlyBatch.Count > 0)
                    {
                        var batchResults = await Task.WhenAll(readOnlyBatch.Select(c => ExecuteToolAsync(c, workspace, model, ct))).ConfigureAwait(false);
                        foreach (var result in batchResults)
                        {
                            mutated |= result.ToolMutated;
                            sessionStore.Append(session, result.Message);
                        }
                    }
                    PublishSteering(guidance);
                    continue;
                }

                if (guidance.Count > 0)
                {
                    PublishSteering(guidance);
                    continue;
                }

                // The model ran out of output tokens mid-answer (finish_reason length/max_tokens).
                // That is not "done" — keep the turn going so long generations and long thinking
                // phases can finish instead of silently ending with partial (or empty) output.
                if (IsOutputTruncated(completion.FinishReason))
                {
                    truncatedSteps++;
                    if (truncatedSteps > agentConfig.MaxOutputContinuations)
                    {
                        events.Publish(new WarningEvent(
                            $"输出连续 {agentConfig.MaxOutputContinuations} 次达到长度上限，回合已停止。可在模型配置中调大 maxOutput 或拆分任务。"));
                        break;
                    }
                    if (completion.Text.Length == 0)
                    {
                        var continuationNote = ChatMessage.User(
                            ">>> [output truncated] 上一轮输出因达到单次长度上限被截断，且没有产出任何内容。请直接给出最终回答或调用工具开始执行，不要再进行超长思考。");
                        sessionStore.Append(session, continuationNote);
                    }
                    events.Publish(new StatusEvent("Output truncated; continuing"));
                    PublishSteering(guidance);
                    continue;
                }
                truncatedSteps = 0;

                // Model believes it is done. If it changed files, prove the project still builds.
                if (mutated && ShouldVerify(workspace, agentConfig) && repairAttempts < agentConfig.MaxRepairAttempts)
                {
                    var repairMessage = await RunVerificationAsync(workspace, repairAttempts + 1, ct).ConfigureAwait(false);
                    if (repairMessage is not null)
                    {
                        repairAttempts++;
                        mutated = false; // reset; the repair edits set it again
                        sessionStore.Append(session, ChatMessage.User(repairMessage));
                        continue;
                    }
                }
                var lateGuidance = AppendSteering(session, steering);
                if (lateGuidance.Count > 0)
                {
                    PublishSteering(lateGuidance);
                    continue;
                }
                if (steering is not null && !steering.TryCompleteIfEmpty()) continue;
                break;
            }
            if (reachedMaxSteps && error is null && !cancelled)
            {
                events.Publish(new WarningEvent(
                    $"已达到最大步数 {maxSteps}，任务可能尚未完成。可在 ~/.seekclaw/config.json 中调大 agent.maxSteps。"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (LlmException ex)
        {
            error = ex.Message;
            events.Publish(new ErrorEvent("LLM request failed", ex.Message));
        }

        PublishSteering(AppendSteering(session, steering));
        steering?.Complete();
        events.Publish(new TurnCompletedEvent(session.Header.Id, cancelled, error));
        return new AgentTurnResult(finalText, cancelled, error);
    }

    /// <summary>True when the provider stopped because the output-token cap was hit, not because it finished.</summary>
    private static bool IsOutputTruncated(string? finishReason) =>
        finishReason is "length" or "max_tokens" or "incomplete";

    private IReadOnlyList<ChatMessage> AppendSteering(AgentSession session, AgentSteeringQueue? steering)
    {
        if (steering is null) return [];
        var messages = steering.Drain();
        foreach (var message in messages) sessionStore.Append(session, message);
        return messages;
    }

    private void PublishSteering(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages) events.Publish(new UserSteerEvent(message.Text));
    }

    // ---------------------------------------------------------------- llm streaming

    /// <summary>
    /// History copy with image payloads removed, used for text-only turns so earlier
    /// attachments are not re-uploaded on every follow-up message (slow) and do not
    /// force the non-streaming provider path.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> WithoutImages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.All(message => message.Images is not { Count: > 0 }))
            return messages;
        return messages
            .Select(message => message.Images is not { Count: > 0 }
                ? message
                : new ChatMessage
                {
                    Role = message.Role,
                    Text = message.Text,
                    Thinking = message.Thinking,
                    ViewedImages = message.ViewedImages,
                    ToolCalls = message.ToolCalls,
                    ToolCallId = message.ToolCallId,
                    ToolName = message.ToolName,
                    ToolSuccess = message.ToolSuccess,
                    ToolDiff = message.ToolDiff,
                    ToolFilePath = message.ToolFilePath,
                })
            .ToList();
    }

    private async Task CompactContextAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        ModelInfo model,
        IReadOnlyList<ChatMessage> source,
        string systemPrompt,
        CancellationToken ct)
    {
        var (old, recent) = ContextPlanner.SplitForCompaction(source, model.Model, systemPrompt);
        if (old.Count == 0) return;

        events.Publish(new StatusEvent("Compacting context", $"{old.Count} earlier messages"));
        string? summary;
        try
        {
            summary = await SummarizeContextAsync(model, workspace, old, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Compaction must never break the turn: fall back to the plain window trim.
            events.Publish(new WarningEvent($"Context compaction failed: {ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(summary)) return;

        var summaryMessage = ChatMessage.User(
            ">>> [Context compaction] 之前的对话已被压缩以保持上下文可容纳，以下是阶段性总结：\n\n"
            + summary.Trim());

        session.Messages.Clear();
        session.Messages.Add(summaryMessage);
        session.Messages.AddRange(recent);

        try
        {
            sessionStore.ReplaceHistory(session, session.Messages);
        }
        catch (Exception ex)
        {
            // Keep the in-memory compacted view even if persistence fails.
            events.Publish(new WarningEvent($"Could not persist context compaction: {ex.Message}"));
        }

        events.Publish(new StatusEvent("Context compacted", $"{old.Count} earlier messages summarized"));
    }

    private async Task<string?> SummarizeContextAsync(
        ModelInfo model, WorkspaceInfo workspace, IReadOnlyList<ChatMessage> old, CancellationToken ct)
    {
        // Image payloads are not needed for the summary; dropping them keeps the call small.
        var input = ContextPlanner.FitToWindow(WithoutImages(old), model.Model, CompactionInstruction);
        var completion = await CollectCompletionAsync(
            candidate => new LlmRequest
            {
                Provider = candidate.Provider,
                Model = candidate.Model,
                Messages = input,
                System = CompactionInstruction,
                MaxTokens = 4_096,
                EnableThinking = false,
                ReasoningLevel = ReasoningLevel.None,
            },
            workspace, ct).ConfigureAwait(false);
        return completion?.Text;
    }

    private async Task<LlmCompletion?> CollectCompletionAsync(
        Func<ModelInfo, LlmRequest> requestFactory, WorkspaceInfo workspace, CancellationToken ct)
    {
        LlmCompletion? completion = null;
        await foreach (var evt in providerManager.StreamAsync(requestFactory, workspace.Config, ct).ConfigureAwait(false))
        {
            if (evt is LlmCompleted completed) completion = completed.Completion;
        }
        return completion;
    }

    private async Task<LlmCompletion> StreamOnceAsync(
        ModelInfo activeModel,
        WorkspaceInfo workspace,
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ITool> tools,
        ReasoningLevel reasoningLevel,
        bool requiresVision,
        CancellationToken ct)
    {
        var config = configStore.Config;
        var profile = config.GetActiveProfile();
        var temperature = workspace.Config?.Temperature ?? profile.Temperature;
        var definitions = tools
            .Select(t => new ToolDefinition(t.Name, t.Description, t.ParameterSchema))
            .ToList();

        LlmRequest RequestFor(ModelInfo model) => new()
        {
            Provider = model.Provider,
            Model = model.Model,
            // Re-fit for the concrete candidate because automatic failover models can have
            // a smaller user-declared context window than the initially selected model.
            Messages = ContextPlanner.FitToWindow(history, model.Model, systemPrompt),
            System = systemPrompt,
            Tools = definitions,
            Temperature = temperature,
            MaxTokens = model.Model.MaxOutput,
            EnableThinking = reasoningLevel != ReasoningLevel.None && model.Model.Capabilities.Thinking,
            ThinkingBudgetTokens = config.Agent.ThinkingBudgetTokens,
            ReasoningLevel = reasoningLevel,
        };

        LlmCompletion? completion = null;
        var streamedText = new StringBuilder();
        var streamedThinking = new StringBuilder();
        var thinkingOpen = false;

        try
        {
            Func<ModelInfo, bool>? candidateFilter = requiresVision
                ? static candidate => candidate.Model.Capabilities.Vision
                : null;
            await foreach (var evt in providerManager.StreamAsync(
                               RequestFor, workspace.Config, ct, candidateFilter).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case LlmThinkingDelta thinking:
                        thinkingOpen = true;
                        streamedThinking.Append(thinking.Text);
                        events.Publish(new ThinkingDeltaEvent(thinking.Text));
                        break;
                    case LlmTextDelta text:
                        if (thinkingOpen)
                        {
                            thinkingOpen = false;
                            events.Publish(new ThinkingCompletedEvent());
                        }
                        streamedText.Append(text.Text);
                        events.Publish(new AssistantTextDeltaEvent(text.Text));
                        break;
                    case LlmToolCallStarted started:
                        if (thinkingOpen)
                        {
                            thinkingOpen = false;
                            events.Publish(new ThinkingCompletedEvent());
                        }
                        events.Publish(new StatusEvent("Preparing tool call", started.Name));
                        break;
                    case LlmCompleted done:
                        completion = done.Completion;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && streamedText.Length > 0)
        {
            // Preserve whatever was streamed before the user hit Ctrl+C.
            return new LlmCompletion
            {
                Text = streamedText.ToString(),
                Thinking = streamedThinking.ToString(),
                FinishReason = "cancelled",
            };
        }

        if (thinkingOpen) events.Publish(new ThinkingCompletedEvent());
        return completion ?? new LlmCompletion
        {
            Text = streamedText.ToString(),
            Thinking = streamedThinking.ToString(),
            FinishReason = "incomplete",
        };
    }

    // ---------------------------------------------------------------- tool execution

    private readonly record struct ToolExecution(ChatMessage Message, bool ToolMutated);

    private async Task<ToolExecution> ExecuteToolAsync(
        ToolCallRequest call, WorkspaceInfo workspace, ModelInfo model, CancellationToken ct)
    {
        var tool = toolRegistry.Resolve(call.Name);
        var argsSummary = SummarizeArguments(call.ArgumentsJson);
        events.Publish(new ToolCallStartedEvent(call.Id, call.Name, argsSummary));

        if (tool is null)
        {
            var message = $"Unknown tool: {call.Name}";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);
        if (mode.IsReadOnly() && tool.Mutating)
        {
            var message = $"Tool execution denied: '{tool.Name}' is not allowed in {mode.ToDisplayString()}. Switch mode via '/mode edit' or '/mode auto' to allow file mutations.";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        events.Publish(new StatusEvent(tool.StatusLabel, argsSummary));

        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(call.ArgumentsJson) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            var message = $"Invalid tool arguments: {ex.Message}";
            events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, message, TimeSpan.Zero));
            return new ToolExecution(ChatMessage.ToolResult(call.Id, call.Name, message, false), false);
        }

        var context = new ToolContext
        {
            Workspace = workspace,
            Events = events,
            Agent = configStore.Config.Agent,
            MaxOutputChars = ContextPlanner.ToolOutputBudget(model.Model, configStore.Config.Agent),
            CallId = call.Id,
            Coordinator = fileLocks,
            Owner = lockScope.Owner,
        };

        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(arguments, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{tool.Name} crashed: {ex.Message}");
        }
        stopwatch.Stop();

        events.Publish(new ToolCallCompletedEvent(
            call.Id, call.Name, result.Success,
            result.Summary ?? Firstline(result.Output), stopwatch.Elapsed));

        var filePath = result.FilePath is null
            ? null
            : Path.IsPathRooted(result.FilePath)
                ? Path.GetRelativePath(workspace.Root, result.FilePath)
                : result.FilePath;
        return new ToolExecution(
            ChatMessage.ToolResult(call.Id, call.Name, result.Output, result.Success, result.Diff, filePath),
            tool.Mutating && result.Success);
    }

    // ---------------------------------------------------------------- verification

    private bool ShouldVerify(WorkspaceInfo workspace, AgentConfig agentConfig) =>
        !workspace.IsGlobal && (workspace.Config?.AutoVerify ?? agentConfig.AutoVerify);

    private async Task<string?> RunVerificationAsync(WorkspaceInfo workspace, int attempt, CancellationToken ct)
    {
        var command = verifier.ResolveCommand(workspace);
        if (command is null) return null;

        events.Publish(new StatusEvent("Verifying", command));
        events.Publish(new VerificationStartedEvent(command, attempt));

        var result = await verifier.VerifyAsync(workspace, ct).ConfigureAwait(false);
        events.Publish(new VerificationCompletedEvent(result.Success, Firstline(result.Output), attempt));
        if (result.Success) return null;

        var template = promptProvider.TryGet("builtin/repair")
                       ?? "The verification command failed. Fix the errors.\nCommand: {{command}}\n\n{{error}}";
        return promptProvider.Render(template, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = result.Command,
            ["error"] = result.Output,
        });
    }

    // ---------------------------------------------------------------- prompt composition

    private async Task<string> ComposeSystemPromptAsync(
        WorkspaceInfo workspace, ModelInfo model, IReadOnlyList<ITool> tools, CancellationToken ct)
    {
        var memory = workspaceManager.LoadMemory(workspace);
        var variables = PromptVariables.Build(workspace, model, tools.Select(t => t.Name).ToList(), memory);
        var context = new PromptRenderContext
        {
            Variables = variables,
            WorkspaceRoot = workspace.Root,
            ProjectKinds = workspace.ProjectKinds,
            WorkspaceConfig = workspace.Config,
        };
        var basePrompt = await promptComposer.ComposeAsync(context, ct).ConfigureAwait(false);

        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);

        var modeInstruction = mode switch
        {
            AgentMode.Plan => "\n\n[MODE: PLAN]\nYou are in PLAN MODE. Focus on researching, analyzing code, and outputting structured step-by-step implementation plans. File modifications and write tools are disabled in this mode.",
            AgentMode.ReadOnly => "\n\n[MODE: READ-ONLY]\nYou are in READ-ONLY MODE. You can read, search, and analyze files, but you cannot modify files or execute mutating commands.",
            AgentMode.Auto => "\n\n[MODE: AUTO]\nYou are in AUTO MODE. Take full initiative to perform edits, multi-step repairs, and automatic verification loops.",
            _ => "",
        };

        return basePrompt + modeInstruction;
    }

    private IReadOnlyList<ITool> ActiveTools(WorkspaceInfo workspace, bool networkEnabled)
    {
        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);

        var disabled = workspace.Config?.DisabledTools;
        var available = disabled is not { Count: > 0 }
            ? toolRegistry.All
            : toolRegistry.All.Where(t => !disabled.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        if (workspace.IsGlobal)
            available = available.Where(tool => !tool.RequiresWorkspace).ToList();

        // The per-session "联网" toggle controls every network tool together
        // (web_search + web_fetch); when off the model never sees them.
        if (!networkEnabled)
            available = available.Where(tool => !tool.RequiresNetwork).ToList();

        if (mode.IsReadOnly())
        {
            available = available.Where(t => !t.Mutating).ToList();
        }

        // The complete tool schema is part of the provider's cached prompt prefix. MCP
        // discovery order is not a semantic concern, so canonicalize it for cache stability.
        return available.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- misc

    private static string SummarizeArguments(string argumentsJson)
    {
        try
        {
            if (JsonNode.Parse(argumentsJson) is JsonObject obj)
            {
                var parts = obj
                    .Where(kv => kv.Value is JsonValue)
                    .Take(3)
                    .Select(kv =>
                    {
                        var value = kv.Value!.ToString();
                        if (value.Length > 60) value = value[..60] + "…";
                        return $"{kv.Key}: {value.ReplaceLineEndings(" ")}";
                    });
                return string.Join(", ", parts);
            }
        }
        catch (JsonException) { }
        return "";
    }

    private static string Firstline(string text)
    {
        var line = text.AsSpan().TrimStart();
        var newline = line.IndexOf('\n');
        var result = newline >= 0 ? line[..newline] : line;
        return result.Length > 160 ? string.Concat(result[..160], "…") : result.ToString();
    }
}
