using System.Text;
using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

public sealed partial class Agent
{
    private async Task<LlmCompletion?> CollectCompletionAsync(
        Func<ModelInfo, LlmRequest> requestFactory, WorkspaceInfo workspace, CancellationToken ct,
        ModelInfo? pinnedModel = null)
    {
        LlmCompletion? completion = null;
        await foreach (var evt in providerManager.StreamAsync(requestFactory, workspace.Config, ct, pinnedModel: pinnedModel).ConfigureAwait(false))
        {
            if (evt is LlmCompleted completed) completion = completed.Completion;
        }
        return completion;
    }

    private async Task<(LlmCompletion Completion, ModelInfo Model, LlmException? Failure)> StreamOnceAsync(
        ModelInfo activeModel,
        WorkspaceInfo workspace,
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ITool> tools,
        ReasoningLevel reasoningLevel,
        bool requiresVision,
        CancellationToken ct,
        ModelInfo? pinnedModel,
        IReadOnlySet<ChatMessage> turnInstructions)
    {
        var invokedModel = activeModel;
        var config = configStore.Config;
        var temperature = workspace.Config?.Temperature ?? config.Temperature;
        var definitions = tools
            .Select(t => new ToolDefinition(t.Name, t.Description, t.ParameterSchema))
            .ToList();

        LlmRequest RequestFor(ModelInfo model)
        {
            invokedModel = model;
            return new LlmRequest
            {
                Provider = model.Provider,
                Model = model.Model,
                // Re-fit for the concrete candidate because automatic failover models can have
                // a smaller user-declared context window than the initially selected model.
                Messages = ContextPlanner.FitToWindow(history, model.Model, systemPrompt, turnInstructions),
                System = systemPrompt,
                Tools = definitions,
                Temperature = temperature,
                MaxTokens = model.Model.EffectiveMaxOutput,
                EnableThinking = model.Capabilities.SupportedReasoningLevels is not null
                    ? ReasoningLevelAdapter.Normalize(model.Provider, model.Model, reasoningLevel) != ReasoningLevel.None
                    : reasoningLevel != ReasoningLevel.None
                        && (model.Capabilities.Thinking
                            || model.Capabilities.Reasoning
                            || ReasoningLevelAdapter.IsDeepSeek(model.Provider, model.Model)),
                ThinkingBudgetTokens = config.Agent.ThinkingBudgetTokens,
                ReasoningLevel = reasoningLevel,
                OptimizeDeepSeek = configStore.Config.Routing.DeepSeekOptimizationEnabled,
            };
        }

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
                               RequestFor, workspace.Config, ct, candidateFilter, pinnedModel).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case LlmStreamReset reset:
                        events.Publish(new AssistantStreamResetEvent(
                            reset.ModelRef, streamedText.Length, streamedThinking.Length));
                        streamedText.Clear();
                        streamedThinking.Clear();
                        thinkingOpen = false;
                        completion = null;
                        break;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Preserve whatever was streamed before the user hit Ctrl+C.
            return (new LlmCompletion
            {
                Text = streamedText.ToString(),
                Thinking = streamedThinking.ToString(),
                FinishReason = "cancelled",
            }, invokedModel, null);
        }
        catch (LlmException ex)
        {
            // Keep partial output for history/UI, but never commit partially streamed
            // tool arguments or mistake the interrupted response for a successful turn.
            return (new LlmCompletion
            {
                Text = streamedText.ToString(),
                Thinking = streamedThinking.ToString(),
                FinishReason = "error",
                ErrorMessage = ex.Message,
            }, invokedModel, ex);
        }
        finally
        {
            if (thinkingOpen) events.Publish(new ThinkingCompletedEvent());
        }

        return (completion ?? new LlmCompletion
        {
            Text = streamedText.ToString(),
            Thinking = streamedThinking.ToString(),
            FinishReason = "incomplete",
        }, invokedModel, null);
    }
}
