using SeekClaw.Runtime.Configuration;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;
using SeekClaw.Runtime.Tools;
using SeekClaw.Runtime.Workspaces;

namespace SeekClaw.Runtime.Agents;

public sealed partial class Agent
{
    // ---------------------------------------------------------------- prompt composition

    private async Task<string> ComposeSystemPromptAsync(
        WorkspaceInfo workspace, ModelInfo model, IReadOnlyList<ITool> tools, bool networkEnabled, CancellationToken ct)
    {
        var memory = workspaceManager.LoadMemory(workspace);
        var agentsMd = workspaceManager.LoadAgentInstructions(workspace);
        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);
        var modeName = mode switch
        {
            AgentMode.Plan => "plan",
            AgentMode.ReadOnly => "readonly",
            AgentMode.Auto => "auto",
            _ => "edit",
        };
        var personality = workspace.Config?.Personality ?? configStore.Config.Agent.Personality;
        if (string.IsNullOrWhiteSpace(personality)) personality = "pragmatic";
        var autoVerify = ShouldVerify(workspace, configStore.Config.Agent);
        var variables = PromptVariables.Build(
            workspace,
            model,
            tools.Select(t => t.Name).ToList(),
            memory is null ? "" : ContextPlanner.FitInjectedText(memory),
            modeName,
            networkEnabled,
            autoVerify,
            personality);
        if (!string.IsNullOrWhiteSpace(agentsMd))
            variables["agents_md"] = ContextPlanner.FitInjectedText(agentsMd);
        var context = new PromptRenderContext
        {
            Variables = variables,
            WorkspaceRoot = workspace.Root,
            ProjectKinds = workspace.ProjectKinds,
            WorkspaceConfig = workspace.Config,
        };
        var basePrompt = await promptComposer.ComposeAsync(context, ct).ConfigureAwait(false);
        return basePrompt;
    }

    private IReadOnlyList<ITool> ActiveTools(WorkspaceInfo workspace, ModelInfo model, bool networkEnabled)
    {
        var rawMode = workspace.Config?.Mode ?? configStore.Config.Agent.Mode;
        var mode = AgentModeExtensions.Parse(rawMode);

        var disabled = workspace.Config?.DisabledTools;
        var available = disabled is not { Count: > 0 }
            ? toolRegistry.All
            : toolRegistry.All.Where(t => !disabled.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        // The per-session "联网" toggle controls every network tool together
        // (web_search + web_fetch); when off the model never sees them.
        if (!networkEnabled)
            available = available.Where(tool => !tool.RequiresNetwork).ToList();

        if (!model.Model.Capabilities.Vision)
            available = available.Where(tool => !tool.RequiresVision).ToList();

        if (mode.IsReadOnly())
        {
            available = available.Where(t => !t.Mutating).ToList();
        }

        // The complete tool schema is part of the provider's cached prompt prefix. MCP
        // discovery order is not a semantic concern, so canonicalize it for cache stability.
        return available.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }
}
