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
public sealed partial class Agent(
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
            events.Publish(new WorkflowEvent(0, "start", "开始任务"));
            while (true)
            {
                step++;
                if (step > maxSteps) { reachedMaxSteps = true; break; }
                ct.ThrowIfCancellationRequested();
                PublishSteering(AppendSteering(session, steering));

                // Only the current user input or turn tool results decide whether this turn needs vision. A
                // text-only turn must not be forced onto a vision model, and re-uploading
                // every earlier image would make it slow and force the non-streaming provider path.
                var hasTurnImages = userMessage.Images is { Count: > 0 }
                    || session.Messages.Any(message => message.Images is { Count: > 0 });
                var model = hasTurnImages
                    ? providerManager.BuildCandidates(workspace.Config)
                        .FirstOrDefault(candidate => candidate.Model.Capabilities.Vision)
                      ?? throw new LlmException(
                          "The current routing profile has no model that supports image understanding.",
                          retryable: false)
                    : providerManager.ResolveActive(workspace.Config);
                var requiresVision = hasTurnImages && model.Model.Capabilities.Vision;
                var tools = ActiveTools(workspace, model, session.Header.NetworkEnabled);
                var systemPrompt = await ComposeSystemPromptAsync(
                    workspace, model, tools, session.Header.NetworkEnabled, ct).ConfigureAwait(false);
                var source = requiresVision ? session.Messages : WithoutImages(session.Messages);
                var history = ContextPlanner.FitToWindow(source, model.Model, systemPrompt);
                // Context compaction: when plain trimming would have to drop history, first
                // summarize the old part so long turns keep their memory and can finish.
                // A failed compaction never aborts the turn — it falls back to the trim.
                if (agentConfig.EnableContextCompaction
                    && !compactedThisTurn
                    && history.Count + 4 <= source.Count)
                {
                    PublishWorkflow(step, "compact", "压缩记忆");
                    await CompactContextAsync(session, workspace, model, source, systemPrompt, ct).ConfigureAwait(false);
                    compactedThisTurn = true;
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
                    ModelRef = $"{model.Provider.Id}/{model.Model.Id}",
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
                                foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
                                var batchResults = await Task.WhenAll(readOnlyBatch.Select(c => ExecuteToolAsync(c, workspace, model, ct))).ConfigureAwait(false);
                                foreach (var result in batchResults)
                                {
                                    mutated |= result.ToolMutated;
                                    sessionStore.Append(session, result.Message);
                                }
                                readOnlyBatch.Clear();
                            }

                            PublishWorkflow(step, "tool", call.Name);
                            var singleResult = await ExecuteToolAsync(call, workspace, model, ct).ConfigureAwait(false);
                            mutated |= singleResult.ToolMutated;
                            sessionStore.Append(session, singleResult.Message);
                        }
                    }

                    if (readOnlyBatch.Count > 0)
                    {
                        foreach (var readOnly in readOnlyBatch) PublishWorkflow(step, "tool", readOnly.Name);
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
                    PublishWorkflow(step, "think", "续写");
                    PublishSteering(guidance);
                    continue;
                }
                truncatedSteps = 0;

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
            events.Publish(new WorkflowEvent(
                step,
                error is null && !cancelled ? "done" : "error",
                error is null && !cancelled ? "任务完成" : "任务失败"));
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
}
