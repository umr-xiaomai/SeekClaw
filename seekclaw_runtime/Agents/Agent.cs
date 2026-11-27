using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Goals;
using SeekClaw.Runtime.Mcp;
using SeekClaw.Runtime.Memory;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Sessions;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Verification;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

/// <summary>
/// Outcome of one turn. <paramref name="Maintenance"/> holds optional post-turn work
/// (cross-session memory extraction) that performs an extra model round trip. Callers
/// report the turn as finished first and await it afterwards, so the terminal `done`
/// signal never waits for memory maintenance.
/// </summary>
public sealed record AgentTurnResult(string Text, bool Cancelled, string? Error, Task? Maintenance = null);

/// <summary>
/// The agent loop: compose prompt → stream model → execute tools → repeat,
/// with automatic build verification and repair after file mutations.
/// All progress is published to the event bus; the agent never touches the console.
/// </summary>
public sealed partial class Agent(
    IConfigStore configStore,
    IProviderManager providerManager,
    IToolRegistry toolRegistry,
    IPromptProvider promptProvider,
    PromptComposer promptComposer,
    IWorkspaceManager workspaceManager,
    ISessionStore sessionStore,
    ICrossSessionMemoryStore memoryStore,
    IVerifier verifier,
    IEventBus events,
    IFileLockCoordinator fileLocks,
    FileLockScope lockScope,
    IToolApprovalGate approvalGate,
    IModelRegistry modelRegistry,
    SeekClaw.Runtime.SubAgents.SubAgentScope? subAgentScope = null,
    SeekClaw.Runtime.Execution.SessionTurnCoordinator? turns = null,
    WorktreeManager? worktrees = null,
    SeekClaw.Runtime.Skills.SkillManager? skills = null,
    SessionGoalStore? goals = null,
    IMcpManager? mcp = null)
{
    public async Task<AgentTurnResult> RunTurnAsync(
        AgentSession session,
        WorkspaceInfo workspace,
        string userInput,
        CancellationToken ct,
        ReasoningLevel? reasoningLevel = null,
        IReadOnlyList<ChatImageAttachment>? images = null,
        AgentSteeringQueue? steering = null,
        string? turnRequestId = null,
        bool captureCrossSessionMemory = true,
        bool pinSessionModel = false,
        Action<ModelInfo?>? onModelRouteReady = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>>? askUser = null,
        bool allowGoalManagement = true,
        bool resumeGoal = false,
        WorkspaceInfo? sessionStorageWorkspace = null)
    {
        var sessionWorkspace = sessionStorageWorkspace ?? workspace;
        using var turnLease = turns is null ? null : await turns.AcquireAsync(
            session.FilePath + "|" + session.Scope + "|" + session.Header.Id, ct).ConfigureAwait(false);
        // Load only after acquiring the lease: a scheduled turn may have updated the history while we waited.
        if (turns is not null && sessionStore.Load(sessionWorkspace, session.Header.Id) is { } latest
            && !ReferenceEquals(latest, session))
        {
            session.Messages.Clear();
            session.Messages.AddRange(latest.Messages);
            session.MessageIds.Clear();
            session.MessageIds.AddRange(latest.MessageIds);
            session.ContextCheckpoint = latest.ContextCheckpoint;
            session.Header.NetworkEnabled &= latest.Header.NetworkEnabled;
            session.Header.Title = latest.Header.Title;
            session.Header.Archived = latest.Header.Archived;
            session.Header.ReasoningLevel = latest.Header.ReasoningLevel;
            session.Header.ModelRef = latest.Header.ModelRef;
            session.Header.LlmRounds = latest.Header.LlmRounds;
            session.Header.ExecutionSteps = latest.Header.ExecutionSteps;
            session.Header.InputTokens = latest.Header.InputTokens;
            session.Header.TotalInputTokens = latest.Header.TotalInputTokens;
            session.Header.CachedInputTokens = latest.Header.CachedInputTokens;
            session.Header.OutputTokens = latest.Header.OutputTokens;
            session.Header.OutputElapsedMs = latest.Header.OutputElapsedMs;
        }
        using var executionLease = worktrees is null ? null
            : await worktrees.AcquireExecutionAsync(workspace.Root, session.Header.Id, ct).ConfigureAwait(false);
        workspace = worktrees?.Resolve(workspace, session.Header.Id) ?? workspace;
        promptProvider.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        skills?.Attach(workspace);
        var agentConfig = configStore.Config.Agent;
        var canManageGoal = allowGoalManagement && subAgentScope?.Depth is null or 0
            && !session.Header.Archived
            && workspace.Config?.DisabledTools?.Contains(GoalTool.ToolName, StringComparer.OrdinalIgnoreCase) != true;
        var goal = canManageGoal && goals is not null
            ? new GoalExecution(goals, sessionWorkspace, session.Header.Id, events)
            : null;
        if (resumeGoal && goal is null)
            throw new InvalidOperationException("Goal execution is disabled for this conversation.");
        goal?.Start(resumeGoal);
        events.Publish(new TurnStartedEvent(session.Header.Id, userInput));
        var userMessage = resumeGoal
            ? ChatMessage.User(">>> [System Directive: The user resumed the stored conversation goal. Continue from the saved progress.]")
            : ChatMessage.User(userInput, images);
        // Everything appended from here on belongs to this turn; earlier entries are history.
        sessionStore.Append(session, userMessage);
        var turnMessageIndex = session.Messages.Count - 1;
        var turnInstructions = new HashSet<ChatMessage> { userMessage };

        var mutated = false;
        var repairAttempts = 0;
        string? lastFailedVerifySignature = null;
        var finalText = "";
        var error = (string?)null;
        var cancelled = false;
        var reachedMaxSteps = false;
        var commandLoop = new CommandLoopGuard();
        var step = 0;
        Task? maintenance = null;
        string? firstTurnModelRef = null;

        try
        {
            if (mcp is not null)
            {
                await mcp.PrepareAsync(sessionWorkspace, ct).ConfigureAwait(false);
                mcp.BeginTurn(session.Header.Id);
            }
            ModelInfo? pinnedModel = null;
            if (pinSessionModel)
            {
                if (session.Header.ModelRef is null && session.Messages.LastOrDefault(message =>
                        message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.ModelRef))?.ModelRef is { } historicalRef)
                {
                    session.Header.ModelRef = sessionStore.UpdateMetadata(sessionWorkspace, session.Header.Id,
                        modelRef: historicalRef, setModelRefIfMissing: true).ModelRef;
                }
                if (session.Header.ModelRef is { } modelRef)
                {
                    pinnedModel = modelRegistry.Resolve(modelRef) is { Provider.Enabled: true } model
                        ? model
                        : throw new LlmException($"Session model '{modelRef}' is not configured or enabled.", retryable: false);
                }
            }
            onModelRouteReady?.Invoke(pinnedModel);

            // Repair iterations are extra model turns appended after a failed build
            // verification; they must not consume the main task's MaxSteps budget.
            // Reserve one extra step per allowed repair so long multi-step tasks are
            // not cut short by their own repair loop.
            var maxSteps = agentConfig.MaxSteps + Math.Clamp(agentConfig.MaxRepairAttempts, 0, 128);
            var maxTotalSteps = Math.Max(maxSteps, agentConfig.MaxTotalSteps);
            var lastCompactedCount = 0;
            var pendingContextCompaction = false;
            var isContinuation = false;
            var truncatedSteps = 0;
            var preambleNudges = 0;
            string? lastRepeatToolName = null;
            string? lastRepeatToolArgs = null;
            var consecutiveRepeatToolCount = 0;
            ModelInfo? activeModel = null;
            string? activeSystemPrompt = null;
            bool activeRequiresVision = false;
            events.Publish(new WorkflowEvent(0, "start", "开始任务"));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                goal?.Refresh();
                if (goal?.IsPaused == true) break;
                var nextGuidance = AppendSteering(session, steering, turnInstructions, goal);
                PublishSteering(nextGuidance);
                if (nextGuidance.Count > 0) commandLoop.Reset();
                // Let a completion request get its tool-free final response at the step limit.
                var finalizingGoal = goal?.HasCompletionRequest == true;
                step++;
                if (step > maxSteps && !finalizingGoal)
                {
                    var extension = agentConfig.AutoExtendSteps && maxSteps < maxTotalSteps
                        ? Math.Min(agentConfig.MaxSteps, maxTotalSteps - maxSteps)
                        : 0;
                    if (extension <= 0 && goal?.ExtendAtStepLimit() == true)
                    {
                        extension = Math.Min(Math.Max(1, agentConfig.MaxSteps), int.MaxValue - maxSteps);
                        events.Publish(new StatusEvent("Continuing goal", "已从保存的会话进度续步"));
                    }
                    if (extension <= 0)
                    {
                        reachedMaxSteps = true;
                        steering?.Complete();
                        break;
                    }
                    maxSteps += extension;
                    if (maxSteps <= maxTotalSteps)
                        events.Publish(new StatusEvent("Auto-extending steps", $"任务步数已自动延展至 {maxSteps} 步，正在继续完成任务..."));
                    PublishWorkflow(step, "think", "自动续步", $"已执行 {step - 1} 步，自动延展继续执行");
                }

                // Only messages appended by this turn decide whether vision is needed: the current
                // input, steering guidance that arrives mid-turn (possibly with a screenshot), and
                // tool results such as capture_screen. Attachments from earlier turns must not drag
                // a text-only turn onto a vision model or re-upload images on every follow-up.
                var turnHasImages = session.Messages
                    .Skip(turnMessageIndex)
                    .Any(message => message.Images is { Count: > 0 });
                var model = pinnedModel ?? (turnHasImages
                    ? providerManager.BuildCandidates(workspace.Config)
                        .FirstOrDefault(candidate => candidate.Model.Capabilities.Vision)
                      ?? throw new LlmException(
                          "No configured model supports image understanding.",
                          retryable: false)
                    : providerManager.ResolveActive(workspace.Config));
                if (turnHasImages && !model.Capabilities.Vision)
                    throw new LlmException($"Session model '{model.Ref}' does not support image understanding.", retryable: false);
                var requiresVision = turnHasImages && model.Model.Capabilities.Vision;
                var effectiveNetworkEnabled = session.Header.NetworkEnabled && agentConfig.NetworkEnabled;
                var tools = ActiveTools(workspace, model, effectiveNetworkEnabled, session.Header.Id);
                if (finalizingGoal)
                    tools = [];
                else if (goal is null)
                    tools = tools.Where(tool => tool.Name != GoalTool.ToolName).ToList();
                if (askUser is null)
                    tools = tools.Where(tool => tool.Name != Tools.Builtin.AskUserTool.ToolName).ToList();
                var systemPrompt = await ComposeSystemPromptAsync(
                    workspace, model, tools, effectiveNetworkEnabled, ct).ConfigureAwait(false);
                systemPrompt = goal?.SupplementPrompt(systemPrompt) ?? systemPrompt;

                activeModel = model;
                activeSystemPrompt = systemPrompt;
                activeRequiresVision = requiresVision;
                var source = BuildModelHistory(session, requiresVision, turnInstructions);
                if (agentConfig.EnableContextCompaction
                    && session.Messages.Count != lastCompactedCount
                    && source.Sum(ContextPlanner.EstimateTokens) > ContextPlanner.WindowBudget(model.Model, systemPrompt))
                {
                    var previouslyCovered = session.ContextCheckpoint?.CoveredMessageCount ?? 0;
                    source = await CompactContextAsync(session, workspace, model, systemPrompt, source,
                        turnInstructions, step, ct).ConfigureAwait(false);
                    pendingContextCompaction |= session.ContextCheckpoint?.CoveredMessageCount > previouslyCovered;
                    lastCompactedCount = session.Messages.Count;
                }
                var history = ContextPlanner.FitToWindow(source, model.Model, systemPrompt, turnInstructions);

                events.Publish(new StatusEvent("Thinking"));
                PublishWorkflow(step, "think", "思考");
                if (step == 1 && userMessage.Images is { Count: > 0 })
                    foreach (var image in userMessage.Images)
                        events.Publish(new ImageViewedEvent(image.Id, image.Name, image.MediaType));
                events.Publish(new ModelInvocationStartedEvent(model.Provider.Id, model.Model.Id, step));

                var (completion, invokedModel, streamFailure) = await StreamOnceAsync(
                    model, workspace, systemPrompt, history, tools,
                    reasoningLevel ?? agentConfig.ReasoningLevel, requiresVision, ct, pinnedModel, turnInstructions).ConfigureAwait(false);
                if (streamFailure is { Retryable: true })
                {
                    goal?.Refresh();
                    if (goal?.RecoveryDelay(streamFailure.RetryAfter) is { } delay)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (completion.Text.Length > 0 || completion.Thinking.Length > 0)
                            events.Publish(new AssistantStreamResetEvent(invokedModel.Ref,
                                completion.Text.Length, completion.Thinking.Length));
                        events.Publish(new StatusEvent("Recovering goal", "模型连接中断，正在重试当前请求"));
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        step--;
                        continue;
                    }
                }
                var assistant = new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Text = completion.Text,
                    Thinking = completion.Thinking.Length > 0 ? completion.Thinking : null,
                    ThinkingSignature = completion.ThinkingSignature,
                    ModelRef = invokedModel.Ref,
                    FinishReason = completion.FinishReason,
                    ErrorMessage = completion.ErrorMessage,
                    ToolCalls = completion.ToolCalls.Count > 0 ? completion.ToolCalls : null,
                    ContextCompacted = pendingContextCompaction,
                    ViewedImages = step == 1
                        ? userMessage.Images?.Select(image => new ChatImageReference(image.Id, image.Name)).ToList()
                        : null,
                };
                sessionStore.Append(session, assistant);
                pendingContextCompaction = false;
                if (completion.ErrorMessage is null && completion.FinishReason is not ("cancelled" or "incomplete"))
                    firstTurnModelRef = invokedModel.Ref;
                if (completion.ErrorMessage is { } completionError)
                {
                    finalText = isContinuation ? finalText + completion.Text : completion.Text;
                    throw streamFailure ?? new LlmException(completionError, retryable: false);
                }

                // StreamOnceAsync preserves partial text on cancellation. Stop the turn after
                // persisting that text instead of reporting the cancelled request as completed.
                ct.ThrowIfCancellationRequested();

                if (completion.Text.Length > 0)
                {
                    finalText = isContinuation ? finalText + completion.Text : completion.Text;
                    events.Publish(new AssistantMessageCompletedEvent(completion.Text));
                }

                if (completion.ToolCalls.Count > 0 && goal is not null)
                {
                    goal.Refresh();
                    if (goal.IsPaused || finalizingGoal)
                    {
                        var reason = goal.IsPaused
                            ? "Tool execution was skipped because the conversation goal was paused."
                            : "Tools are disabled for the goal's final summary.";
                        foreach (var call in completion.ToolCalls)
                            sessionStore.Append(session, ChatMessage.ToolResult(call.Id, call.Name, reason, false));
                        if (finalizingGoal) goal.Pause("模型未能输出目标的最终总结，可继续执行目标。");
                        break;
                    }
                }

                if (completion.ToolCalls.Count > 0 && IsOutputTruncated(completion.FinishReason))
                {
                    foreach (var call in completion.ToolCalls)
                    {
                        const string truncatedMsg =
                            "Tool call arguments were truncated because the model output reached max_tokens. Retry the tool call with complete valid JSON arguments.";
                        events.Publish(new ToolCallStartedEvent(call.Id, call.Name, ""));
                        events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, truncatedMsg, TimeSpan.Zero));
                        sessionStore.Append(session, ChatMessage.ToolResult(call.Id, call.Name, truncatedMsg, false));
                    }
                }
                else if (completion.ToolCalls.Count > 0)
                {
                    if (completion.ToolCalls.Count > 1 && completion.ToolCalls.Any(call => call.Name == Tools.Builtin.AskUserTool.ToolName))
                    {
                        foreach (var call in completion.ToolCalls)
                            sessionStore.Append(session, ChatMessage.ToolResult(call.Id, call.Name,
                                "No tools were executed. Call ask_user on its own before proceeding.", false));
                        continue;
                    }

                    if (completion.ToolCalls is [{ Name: Tools.Builtin.AskUserTool.ToolName } questionCall])
                    {
                        PublishWorkflow(step, "tool", questionCall.Name);
                        try
                        {
                            var answer = await ExecuteToolAsync(questionCall, workspace, sessionWorkspace, model,
                                session.Header.Id, effectiveNetworkEnabled, turnRequestId, askUser, ct, goal, tools).ConfigureAwait(false);
                            sessionStore.Append(session, answer.Message);
                            if (answer.Message.ToolSuccess)
                            {
                                turnInstructions.Add(assistant);
                                turnInstructions.Add(answer.Message);
                                commandLoop.Reset();
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            sessionStore.Append(session, ChatMessage.ToolResult(questionCall.Id, questionCall.Name,
                                "The question was cancelled before the user answered.", false));
                            throw;
                        }
                        var questionGuidance = AppendSteering(session, steering, turnInstructions, goal);
                        PublishSteering(questionGuidance);
                        if (questionGuidance.Count > 0) commandLoop.Reset();
                        ResetRepeatToolTracking(ref lastRepeatToolName, ref lastRepeatToolArgs, ref consecutiveRepeatToolCount);
                        truncatedSteps = 0;
                        isContinuation = false;
                        continue;
                    }

                    var readOnlyBatch = new List<ToolCallRequest>();
                    foreach (var call in completion.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (commandLoop.StopReason is not null)
                        {
                            // Supply a result for every requested ID, including calls
                            // skipped when an earlier call in this batch stopped the turn.
                            SkipToolCall(session, call, "Tool not executed because this turn stopped after repeated command attempts without progress.");
                            continue;
                        }
                        var resolved = toolRegistry.Resolve(call.Name);
                        if (resolved is not null
                            && !resolved.Mutating
                            && !resolved.Name.Equals("browser", StringComparison.OrdinalIgnoreCase)
                            && !resolved.Name.Equals("subagent", StringComparison.OrdinalIgnoreCase)
                            && resolved is not (GoalTool or McpToolSearchTool))
                        {
                            readOnlyBatch.Add(call);
                        }
                        else
                        {
                            if (readOnlyBatch.Count > 0)
                            {
                                foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                                var batchResults = await Task.WhenAll(readOnlyBatch.Select(c =>
                                    ExecuteToolAsync(c, workspace, sessionWorkspace, model, session.Header.Id, effectiveNetworkEnabled, turnRequestId, askUser, ct, goal, tools))).ConfigureAwait(false);
                                for (var i = 0; i < batchResults.Length; i++)
                                {
                                    var result = batchResults[i];
                                    mutated |= result.ToolMutated;
                                    var message = AppendRepeatToolReminderIfNeeded(
                                        readOnlyBatch[i], result.Message, model,
                                        ref lastRepeatToolName, ref lastRepeatToolArgs, ref consecutiveRepeatToolCount);
                                    sessionStore.Append(session, message);
                                }
                                readOnlyBatch.Clear();
                            }

                            var preparedCall = commandLoop.Prepare(call, out var rejection);
                            if (rejection is not null)
                            {
                                SkipToolCall(session, call, rejection);
                                continue;
                            }
                            PublishWorkflow(step, "tool", call.Name);
                            var started = Stopwatch.GetTimestamp();
                            var singleResult = await ExecuteToolAsync(
                                preparedCall, workspace, sessionWorkspace, model, session.Header.Id, effectiveNetworkEnabled, turnRequestId, askUser, ct, goal, tools).ConfigureAwait(false);
                            var elapsed = Stopwatch.GetElapsedTime(started);
                            mutated |= singleResult.ToolMutated;
                            var singleMessage = AppendRepeatToolReminderIfNeeded(
                                call, singleResult.Message, model,
                                ref lastRepeatToolName, ref lastRepeatToolArgs, ref consecutiveRepeatToolCount);
                            sessionStore.Append(session, singleMessage);
                            commandLoop.Observe(preparedCall, singleResult.Message, elapsed);
                            if (singleResult.ToolMutated && singleResult.Message.ToolFilePath is not null
                                && !string.IsNullOrEmpty(singleResult.Message.ToolDiff))
                                commandLoop.RecordWorkspaceChange();
                        }
                    }

                    if (readOnlyBatch.Count > 0)
                    {
                        foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                        var batchResults = await Task.WhenAll(readOnlyBatch.Select(c =>
                            ExecuteToolAsync(c, workspace, sessionWorkspace, model, session.Header.Id, effectiveNetworkEnabled, turnRequestId, askUser, ct, goal, tools))).ConfigureAwait(false);
                        for (var i = 0; i < batchResults.Length; i++)
                        {
                            var result = batchResults[i];
                            mutated |= result.ToolMutated;
                            var message = AppendRepeatToolReminderIfNeeded(
                                readOnlyBatch[i], result.Message, model,
                                ref lastRepeatToolName, ref lastRepeatToolArgs, ref consecutiveRepeatToolCount);
                            sessionStore.Append(session, message);
                        }
                    }
                    // Steering must be appended only after every tool result for this
                    // assistant turn. A user message between assistant(tool_calls) and
                    // tool(result) breaks provider pairing and fails the next request.
                    var toolSteering = AppendSteering(session, steering, turnInstructions, goal);
                    PublishSteering(toolSteering);
                    if (toolSteering.Count > 0)
                    {
                        commandLoop.Reset();
                        ResetRepeatToolTracking(ref lastRepeatToolName, ref lastRepeatToolArgs, ref consecutiveRepeatToolCount);
                    }
                    else if (commandLoop.StopReason is { } stopReason)
                    {
                        goal?.Pause(stopReason);
                        finalText = error = stopReason;
                        sessionStore.Append(session, ChatMessage.Assistant(stopReason));
                        events.Publish(new AssistantMessageCompletedEvent(stopReason));
                        break;
                    }
                    truncatedSteps = 0;
                    isContinuation = false;
                    continue;
                }

                ResetRepeatToolTracking(ref lastRepeatToolName, ref lastRepeatToolArgs, ref consecutiveRepeatToolCount);
                var guidance = AppendSteering(session, steering, turnInstructions, goal);
                if (guidance.Count > 0)
                {
                    PublishSteering(guidance);
                    commandLoop.Reset();
                    truncatedSteps = 0;
                    isContinuation = false;
                    continue;
                }

                // The model ran out of output tokens mid-answer (finish_reason length/max_tokens/incomplete).
                // That is not "done" — keep the turn going so long generations and long thinking
                // phases can finish instead of silently ending with partial (or empty) output.
                if (IsOutputTruncated(completion.FinishReason))
                {
                    truncatedSteps++;
                    if (truncatedSteps > agentConfig.MaxOutputContinuations)
                    {
                        goal?.Pause("输出多次达到模型长度上限，可继续执行目标。");
                        events.Publish(new WarningEvent(
                            $"输出连续 {agentConfig.MaxOutputContinuations} 次达到长度上限，回合已停止。可在模型配置中调大 maxOutput 或拆分任务。"));
                        steering?.Complete();
                        break;
                    }

                    if (completion.Text.Length == 0)
                    {
                        var continuationNote = ChatMessage.User(
                            ">>> [output truncated] [System Directive: 上一轮输出因达到单次长度上限被截断，且没有产出正文内容。请直接给出最终回答或调用工具开始执行，不要再进行长篇思考。]");
                        sessionStore.Append(session, continuationNote);
                    }
                    else
                    {
                        var continuationNote = ChatMessage.User(
                            ">>> [output truncated] [System Directive: 你的上一轮输出因达到模型单次 Output Token 长度上限被截断。请紧接着截断处的最后一个字符继续往下写，严禁重复前文已经写出的内容。]");
                        sessionStore.Append(session, continuationNote);
                    }

                    isContinuation = true;
                    events.Publish(new StatusEvent("Output truncated; auto-continuing"));
                    PublishWorkflow(step, "think", "自动续写");
                    continue;
                }
                truncatedSteps = 0;
                isContinuation = false;

                // If the model merely stated an intent/preamble to perform actions (e.g. "I will remove...",
                // "Next, I'll update...", "好的，我来修改...") but called 0 tools while tools are available,
                // nudge it to invoke the necessary tools instead of stopping prematurely on an unfulfilled promise.
                if (completion.ToolCalls.Count == 0
                    && tools.Count > 0
                    && !mutated
                    && preambleNudges < 1
                    && IsStandaloneActionPreamble(completion.Text))
                {
                    preambleNudges++;
                    var nudgeDirective = ChatMessage.User(
                        ">>> [System Directive: 你刚才说明了将要执行的操作，但尚未调用任何工具。如果你正要为用户执行该操作，请立即调用相应工具（如 write_file、edit_file 等）开始执行；如果你是在提供方案等待用户确认，请忽略本提示并等待用户回复。 / You stated an action without calling tools. If you are executing the requested change, please invoke the relevant tool now; if you are awaiting confirmation, ignore this and wait for the user.]");
                    sessionStore.Append(session, nudgeDirective);
                    events.Publish(new StatusEvent("Auto-executing promised actions..."));
                    PublishWorkflow(step, "tool", "自动执行工具");
                    PublishSteering(guidance);
                    continue;
                }

                // Model believes it is done. If it changed files, prove the project still builds.
                if ((mutated || (finalizingGoal && lastFailedVerifySignature is not null))
                    && ShouldVerify(workspace, agentConfig))
                {
                    PublishWorkflow(step, "verify", "构建验证");
                    var verification = await RunVerificationAsync(workspace, repairAttempts + 1, ct).ConfigureAwait(false);
                    var sameFailure = verification.FailureSignature is not null
                        && string.Equals(verification.FailureSignature, lastFailedVerifySignature, StringComparison.Ordinal);
                    lastFailedVerifySignature = verification.FailureSignature;
                    if (lastFailedVerifySignature is not null)
                    {
                        mutated = false; // reset; the repair edits set it again
                        goal?.WithdrawCompletion();
                        if (verification.RepairMessage is { } repairMessage)
                        {
                            if (repairAttempts >= agentConfig.MaxRepairAttempts)
                                events.Publish(new WarningEvent("构建验证未通过，已达自动修复次数上限。"));
                            else if (sameFailure)
                                events.Publish(new StatusEvent("Verification unchanged; stopping auto-repair"));
                            else
                            {
                                repairAttempts++;
                                sessionStore.Append(session, ChatMessage.User(repairMessage));
                                continue;
                            }
                        }
                        goal?.Pause("构建验证未通过，修复后可继续执行目标。");
                    }
                }
                var lateGuidance = AppendSteering(session, steering, turnInstructions, goal);
                if (lateGuidance.Count > 0)
                {
                    PublishSteering(lateGuidance);
                    commandLoop.Reset();
                    continue;
                }
                goal?.Refresh();
                if (goal?.HasCompletionRequest != true && goal?.ContinueAfterAnswer() == true)
                {
                    sessionStore.Append(session, ChatMessage.User(
                        ">>> [System Directive: The goal still needs work. Execute the next step, record verified completion or save a concrete blocker.]"));
                    continue;
                }
                if (steering is not null && !steering.TryCompleteIfEmpty()) continue;
                ct.ThrowIfCancellationRequested();
                goal?.CompleteRequestedGoal();
                break;
            }
            if (reachedMaxSteps && goal?.IsPaused != true && error is null && !cancelled)
            {
                if (goal?.IsCompleted != true)
                    events.Publish(new WarningEvent(
                        $"已达到单回合最大步数上限（{maxSteps} 步），本回合执行暂停。可在 ~/.seekclaw/config.json 中调大 agent.maxTotalSteps。"));

                // Graceful wrap-up: if the turn ended after executing tools, allow the model one final
                // completion with tools disabled so it can summarize completed work and remaining tasks
                // instead of leaving the conversation stranded on a tool result.
                if (activeModel is not null && activeSystemPrompt is not null)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        PublishWorkflow(step, "think", "总结进度");
                        events.Publish(new StatusEvent("Summarizing progress", "已达到最大步数上限，正在总结当前执行成果..."));
                        var wrapUpNotice = ChatMessage.User(
                            $">>> [System Directive: 本回合执行已达到允许的最大步数上限（{maxSteps} 步）。已禁用工具调用。请根据前面执行的所有操作与工具结果，详细总结你已经完成的改动、当前系统状态以及尚未完成的事项。]");
                        sessionStore.Append(session, wrapUpNotice);

                        var source = BuildModelHistory(session, activeRequiresVision, turnInstructions);
                        var history = ContextPlanner.FitToWindow(source, activeModel.Model, activeSystemPrompt, turnInstructions);
                        var (finalCompletion, wrapUpModel, _) = await StreamOnceAsync(
                            activeModel, workspace, activeSystemPrompt, history, [],
                            reasoningLevel ?? agentConfig.ReasoningLevel, activeRequiresVision, ct, pinnedModel, turnInstructions).ConfigureAwait(false);
                        if (finalCompletion.ErrorMessage is null && finalCompletion.FinishReason is not ("cancelled" or "incomplete"))
                            firstTurnModelRef = wrapUpModel.Ref;

                        var assistantSummary = new ChatMessage
                        {
                            Role = ChatRole.Assistant,
                            Text = finalCompletion.Text,
                            Thinking = finalCompletion.Thinking.Length > 0 ? finalCompletion.Thinking : null,
                            ThinkingSignature = finalCompletion.ThinkingSignature,
                            ModelRef = wrapUpModel.Ref,
                            FinishReason = finalCompletion.FinishReason,
                            ErrorMessage = finalCompletion.ErrorMessage,
                            ContextCompacted = pendingContextCompaction,
                        };
                        sessionStore.Append(session, assistantSummary);
                        ct.ThrowIfCancellationRequested();
                        error = finalCompletion.ErrorMessage;
                        if (error is not null) events.Publish(new ErrorEvent("LLM request failed", error));
                        if (finalCompletion.Text.Length > 0)
                        {
                            finalText = finalCompletion.Text;
                            if (error is null) events.Publish(new AssistantMessageCompletedEvent(finalCompletion.Text));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (string.IsNullOrWhiteSpace(finalText))
                        {
                            finalText = $"已达到最大步数上限（{maxSteps} 步），本回合已暂停。";
                            events.Publish(new AssistantMessageCompletedEvent(finalText));
                        }
                    }
                }
            }

            if (pinSessionModel && pinnedModel is null && firstTurnModelRef is not null)
            {
                // Keep the first turn's routing intact, including failover. Save the model
                // that answered without overwriting an explicit update made during the turn.
                session.Header.ModelRef = sessionStore.UpdateMetadata(sessionWorkspace, session.Header.Id,
                    modelRef: firstTurnModelRef, setModelRefIfMissing: true).ModelRef;
            }

            if (captureCrossSessionMemory
                && agentConfig.EnableCrossSessionMemory
                && !cancelled
                && error is null
                && !reachedMaxSteps
                && goal?.IsPaused != true
                && !resumeGoal
                && activeModel is not null
                && subAgentScope?.Depth is null or 0)
            {
                // Memory distillation costs a full extra model round trip. Start it here so it
                // overlaps the turn's terminal signal, but hand the task to the caller instead of
                // awaiting it: desktop clients unfreeze on `done`, and blocking that response on
                // an extra completion made finished answers look stuck for seconds.
                maintenance = CaptureCrossSessionMemoryAsync(
                    session, sessionWorkspace, pinnedModel, userInput, finalText, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            goal?.Refresh();
            goal?.Pause("执行已中止，可在原会话继续。");
        }
        catch (LlmException ex)
        {
            goal?.Refresh();
            goal?.Pause(ex.Retryable ? "模型连接暂时不可用，自动重试已停止。" : "模型请求失败，处理错误后可继续。");
            error = ex.Message;
            events.Publish(new ErrorEvent("LLM request failed", ex.Message));
        }
        catch (Exception ex)
        {
            goal?.Refresh();
            goal?.Pause("执行发生错误，处理错误后可继续。");
            error = ex.Message;
            events.Publish(new ErrorEvent("Turn failed", ex.Message));
        }

        var paused = goal?.IsPaused == true || (reachedMaxSteps && goal?.IsCompleted != true);
        events.Publish(error is not null || cancelled
            ? new WorkflowEvent(step, "error", "任务失败")
            : paused
                ? new WorkflowEvent(step, "paused", goal?.IsPaused == true ? "任务已暂停" : "达到步数上限已暂停")
                : new WorkflowEvent(step, "done", "任务完成"));
        if (!cancelled && error is null)
            PublishSteering(AppendSteering(session, steering, turnInstructions, goal));
        steering?.Complete();
        events.Publish(new TurnCompletedEvent(session.Header.Id, cancelled, error));
        return new AgentTurnResult(finalText, cancelled, error, maintenance);
    }

    private void SkipToolCall(AgentSession session, ToolCallRequest call, string reason)
    {
        events.Publish(new ToolCallStartedEvent(call.Id, call.Name, ""));
        events.Publish(new ToolCallCompletedEvent(call.Id, call.Name, false, reason, TimeSpan.Zero));
        sessionStore.Append(session, ChatMessage.ToolResult(call.Id, call.Name, reason, false));
    }

    private void PublishWorkflow(int step, string kind, string label, string? detail = null) =>
        events.Publish(new WorkflowEvent(step, kind, label, detail));

    /// <summary>True when the provider stopped because the output-token cap was hit, not because it finished.</summary>
    private static bool IsOutputTruncated(string? finishReason) =>
        finishReason is "length" or "max_tokens" or "incomplete";

    private IReadOnlyList<ChatMessage> AppendSteering(AgentSession session, AgentSteeringQueue? steering,
        HashSet<ChatMessage> turnInstructions, GoalExecution? goal)
    {
        if (steering is null) return [];
        var messages = steering.Drain();
        // New guidance may change the work that the completion evidence covers.
        if (messages.Count > 0) goal?.WithdrawCompletion();
        foreach (var message in messages)
        {
            sessionStore.Append(session, message);
            turnInstructions.Add(message);
        }
        return messages;
    }

    private void PublishSteering(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages) events.Publish(new UserSteerEvent(message.Text));
    }

    private static void ResetRepeatToolTracking(ref string? lastToolName, ref string? lastToolArgs, ref int count)
    {
        lastToolName = null;
        lastToolArgs = null;
        count = 0;
    }

    private ChatMessage AppendRepeatToolReminderIfNeeded(
        ToolCallRequest call,
        ChatMessage resultMessage,
        ModelInfo model,
        ref string? lastToolName,
        ref string? lastToolArgs,
        ref int consecutiveCount)
    {
        if (!configStore.Config.Routing.DeepSeekOptimizationEnabled
            || !DeepSeekOptimizationPolicy.IsDeepSeek(model.Provider, model.Model))
            return resultMessage;

        if (call.Name.Equals("update_plan", StringComparison.OrdinalIgnoreCase))
            return resultMessage;

        var canonicalArgs = DeepSeekOptimizationPolicy.CanonicalizeArgumentsJson(call.ArgumentsJson);
        if (string.Equals(lastToolName, call.Name, StringComparison.Ordinal)
            && string.Equals(lastToolArgs, canonicalArgs, StringComparison.Ordinal))
        {
            consecutiveCount++;
        }
        else
        {
            lastToolName = call.Name;
            lastToolArgs = canonicalArgs;
            consecutiveCount = 1;
        }

        var reminder = DeepSeekOptimizationPolicy.FormatRepeatToolReminder(call.Name, canonicalArgs, consecutiveCount);
        if (reminder is null)
            return resultMessage;

        return ChatMessage.ToolResult(
            resultMessage.ToolCallId ?? call.Id,
            resultMessage.ToolName ?? call.Name,
            resultMessage.Text + reminder,
            resultMessage.ToolSuccess,
            resultMessage.ToolDiff,
            resultMessage.ToolFilePath,
            resultMessage.Images,
            resultMessage.ToolContent,
            resultMessage.ToolStructuredContent,
            resultMessage.ToolMetadata,
            resultMessage.ToolWidgets);
    }

    [GeneratedRegex(@"^(?:\s*(?:ok|okay|sure|alright|great)[!.,\s]*)?\s*(?:i will|i'll|i am going to|i'm going to|next,?\s*i(?:'ll|\s*will)|let me)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishActionPreambleRegex();

    [GeneratedRegex(@"^(?:\s*(?:好的|收到|明白)[！!，,\s]*)?\s*(?:我来|我将|我马上|接下来我将|现在我来|我这就|让我来)", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseActionPreambleRegex();

    internal static bool IsStandaloneActionPreamble(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 150) return false;
        var trimmed = text.Trim();
        if (trimmed.Contains('?') || trimmed.Contains('？') || trimmed.Contains("```")) return false;
        if (trimmed.Contains('\n')) return false;

        // If the statement is waiting for user confirmation, proposing a plan, or asking a question without '?', it is NOT a standalone action preamble.
        if (trimmed.Contains("如确认") || trimmed.Contains("若确认") || trimmed.Contains("请确认") || trimmed.Contains("如果确认") || trimmed.Contains("如认可")) return false;
        if (trimmed.Contains("是否") || trimmed.Contains("可否") || trimmed.Contains("请问") || trimmed.Contains("建议") || trimmed.Contains("方案") || trimmed.Contains("推进步骤")) return false;
        if (trimmed.Contains("would you like", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("if you confirm", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("please confirm", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("let me know", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("should i", StringComparison.OrdinalIgnoreCase)) return false;

        return EnglishActionPreambleRegex().IsMatch(trimmed) || ChineseActionPreambleRegex().IsMatch(trimmed);
    }
}
