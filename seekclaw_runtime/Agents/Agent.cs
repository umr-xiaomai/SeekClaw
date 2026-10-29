using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeekClaw.Runtime.Agents;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Coordination;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Memory;
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
    SeekClaw.Runtime.SubAgents.SubAgentScope? subAgentScope = null)
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
        bool captureCrossSessionMemory = true)
    {
        var agentConfig = configStore.Config.Agent;
        events.Publish(new TurnStartedEvent(session.Header.Id, userInput));
        var userMessage = ChatMessage.User(userInput, images);
        // Everything appended from here on belongs to this turn; earlier entries are history.
        sessionStore.Append(session, userMessage);
        var turnMessageIndex = session.Messages.Count - 1;

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
            var maxTotalSteps = Math.Max(maxSteps, agentConfig.MaxTotalSteps);
            var lastCompactedCount = 0;
            var isContinuation = false;
            var truncatedSteps = 0;
            var preambleNudges = 0;
            var reachedMaxSteps = false;
            var step = 0;
            ModelInfo? activeModel = null;
            string? activeSystemPrompt = null;
            bool activeRequiresVision = false;
            events.Publish(new WorkflowEvent(0, "start", "开始任务"));
            while (true)
            {
                step++;
                if (step > maxSteps)
                {
                    if (agentConfig.AutoExtendSteps && maxSteps < maxTotalSteps)
                    {
                        var extension = Math.Min(agentConfig.MaxSteps, maxTotalSteps - maxSteps);
                        if (extension > 0)
                        {
                            maxSteps += extension;
                            events.Publish(new StatusEvent("Auto-extending steps", $"任务步数已自动延展至 {maxSteps} 步，正在继续完成任务..."));
                            PublishWorkflow(step, "think", "自动续步", $"已执行 {step - 1} 步，自动延展继续执行");
                        }
                        else
                        {
                            reachedMaxSteps = true;
                            break;
                        }
                    }
                    else
                    {
                        reachedMaxSteps = true;
                        break;
                    }
                }
                ct.ThrowIfCancellationRequested();
                PublishSteering(AppendSteering(session, steering));

                // Only messages appended by this turn decide whether vision is needed: the current
                // input, steering guidance that arrives mid-turn (possibly with a screenshot), and
                // tool results such as capture_screen. Attachments from earlier turns must not drag
                // a text-only turn onto a vision model or re-upload images on every follow-up.
                var turnHasImages = session.Messages
                    .Skip(turnMessageIndex)
                    .Any(message => message.Images is { Count: > 0 });
                var model = turnHasImages
                    ? providerManager.BuildCandidates(workspace.Config)
                        .FirstOrDefault(candidate => candidate.Model.Capabilities.Vision)
                      ?? throw new LlmException(
                          "No configured model supports image understanding.",
                          retryable: false)
                    : providerManager.ResolveActive(workspace.Config);
                var requiresVision = turnHasImages && model.Model.Capabilities.Vision;
                var effectiveNetworkEnabled = session.Header.NetworkEnabled && agentConfig.NetworkEnabled;
                var tools = ActiveTools(workspace, model, effectiveNetworkEnabled);
                var systemPrompt = await ComposeSystemPromptAsync(
                    workspace, model, tools, effectiveNetworkEnabled, ct).ConfigureAwait(false);

                activeModel = model;
                activeSystemPrompt = systemPrompt;
                activeRequiresVision = requiresVision;
                var source = requiresVision ? session.Messages : WithoutImages(session.Messages);
                var history = ContextPlanner.FitToWindow(source, model.Model, systemPrompt);
                // Context compaction: when plain trimming would have to drop history, summarize the
                // older messages in-turn (MidTurn compaction) so long multi-step turns never lose their memory.
                if (agentConfig.EnableContextCompaction
                    && history.Count + 4 <= source.Count
                    && source.Count != lastCompactedCount)
                {
                    PublishWorkflow(step, "compact", "压缩记忆");
                    await CompactContextAsync(
                        session, workspace, model, systemPrompt, turnMessageIndex, ct).ConfigureAwait(false);
                    turnMessageIndex = session.Messages.FindIndex(message => ReferenceEquals(message, userMessage));
                    if (turnMessageIndex < 0) turnMessageIndex = 0;
                    lastCompactedCount = session.Messages.Count;
                    source = requiresVision ? session.Messages : WithoutImages(session.Messages);
                    history = ContextPlanner.FitToWindow(source, model.Model, systemPrompt);
                }

                events.Publish(new StatusEvent("Thinking"));
                PublishWorkflow(step, "think", "思考");
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
                    ThinkingSignature = completion.ThinkingSignature,
                    ModelRef = $"{model.Provider.Id}/{model.Model.Id}",
                    ToolCalls = completion.ToolCalls.Count > 0 ? completion.ToolCalls : null,
                    ViewedImages = step == 1
                        ? userMessage.Images?.Select(image => new ChatImageReference(image.Id, image.Name)).ToList()
                        : null,
                };
                sessionStore.Append(session, assistant);
                if (completion.Text.Length > 0)
                {
                    finalText = isContinuation ? finalText + completion.Text : completion.Text;
                    events.Publish(new AssistantMessageCompletedEvent(completion.Text));
                }

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
                                foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                                var batchResults = await Task.WhenAll(readOnlyBatch.Select(c =>
                                    ExecuteToolAsync(c, workspace, model, session.Header.Id, turnRequestId, ct))).ConfigureAwait(false);
                                foreach (var result in batchResults)
                                {
                                    mutated |= result.ToolMutated;
                                    sessionStore.Append(session, result.Message);
                                }
                                readOnlyBatch.Clear();
                            }

                            PublishWorkflow(step, "tool", call.Name);
                            var singleResult = await ExecuteToolAsync(
                                call, workspace, model, session.Header.Id, turnRequestId, ct).ConfigureAwait(false);
                            mutated |= singleResult.ToolMutated;
                            sessionStore.Append(session, singleResult.Message);
                        }
                    }

                    if (readOnlyBatch.Count > 0)
                    {
                        foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                        var batchResults = await Task.WhenAll(readOnlyBatch.Select(c =>
                            ExecuteToolAsync(c, workspace, model, session.Header.Id, turnRequestId, ct))).ConfigureAwait(false);
                        foreach (var result in batchResults)
                        {
                            mutated |= result.ToolMutated;
                            sessionStore.Append(session, result.Message);
                        }
                    }
                    // Steering must be appended only after every tool result for this
                    // assistant turn. A user message between assistant(tool_calls) and
                    // tool(result) breaks provider pairing and fails the next request.
                    PublishSteering(AppendSteering(session, steering));
                    continue;
                }

                var guidance = AppendSteering(session, steering);
                if (guidance.Count > 0)
                {
                    PublishSteering(guidance);
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
                        events.Publish(new WarningEvent(
                            $"输出连续 {agentConfig.MaxOutputContinuations} 次达到长度上限，回合已停止。可在模型配置中调大 maxOutput 或拆分任务。"));
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
                if (mutated && ShouldVerify(workspace, agentConfig) && repairAttempts < agentConfig.MaxRepairAttempts)
                {
                    PublishWorkflow(step, "verify", "构建验证");
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

                        var source = activeRequiresVision ? session.Messages : WithoutImages(session.Messages);
                        var history = ContextPlanner.FitToWindow(source, activeModel.Model, activeSystemPrompt);
                        var finalCompletion = await StreamOnceAsync(
                            activeModel, workspace, activeSystemPrompt, history, [],
                            reasoningLevel ?? agentConfig.ReasoningLevel, false, ct).ConfigureAwait(false);

                        var assistantSummary = new ChatMessage
                        {
                            Role = ChatRole.Assistant,
                            Text = finalCompletion.Text,
                            Thinking = finalCompletion.Thinking.Length > 0 ? finalCompletion.Thinking : null,
                            ThinkingSignature = finalCompletion.ThinkingSignature,
                            ModelRef = $"{activeModel.Provider.Id}/{activeModel.Model.Id}",
                        };
                        sessionStore.Append(session, assistantSummary);
                        if (finalCompletion.Text.Length > 0)
                        {
                            finalText = finalCompletion.Text;
                            events.Publish(new AssistantMessageCompletedEvent(finalCompletion.Text));
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

            if (captureCrossSessionMemory
                && agentConfig.EnableCrossSessionMemory
                && !cancelled
                && error is null
                && !reachedMaxSteps
                && activeModel is not null
                && subAgentScope?.Depth is null or 0)
            {
                await CaptureCrossSessionMemoryAsync(
                    session, workspace, activeModel, userInput, finalText, ct).ConfigureAwait(false);
            }

            events.Publish(new WorkflowEvent(
                step,
                error is null && !cancelled ? (reachedMaxSteps ? "paused" : "done") : "error",
                error is null && !cancelled ? (reachedMaxSteps ? "达到步数上限已暂停" : "任务完成") : "任务失败"));
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

    private void PublishWorkflow(int step, string kind, string label, string? detail = null) =>
        events.Publish(new WorkflowEvent(step, kind, label, detail));

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
