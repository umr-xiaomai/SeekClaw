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
            OptimizeDeepSeek = configStore.Config.Routing.DeepSeekOptimizationEnabled,
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
}
