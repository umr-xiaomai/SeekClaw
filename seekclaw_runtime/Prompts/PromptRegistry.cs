namespace SeekClaw.Runtime.Prompts;

/// <summary>Composition order of the final system prompt.</summary>
public enum PromptSlot
{
    System = 0,
    Developer = 1,
    Workspace = 2,
    Skill = 3,
    Tool = 4,
    Memory = 5,
}

/// <summary>Context handed to prompt contributors when the final prompt is assembled.</summary>
public sealed class PromptRenderContext
{
    public required IReadOnlyDictionary<string, string> Variables { get; init; }
    public string? WorkspaceRoot { get; init; }
    public IReadOnlyList<string> ProjectKinds { get; init; } = [];
    public Configuration.WorkspaceConfig? WorkspaceConfig { get; init; }
}

/// <summary>One source of prompt text (system file, skill, MCP server, memory…).</summary>
public sealed record PromptContribution(
    string Id,
    PromptSlot Slot,
    Func<PromptRenderContext, CancellationToken, ValueTask<string?>> Resolver);

/// <summary>
/// Unified registry for System / Developer / Tool / Workflow / Skill / MCP prompts.
/// Skills and MCP servers register contributions here; nothing is hard-coded.
/// </summary>
public interface IPromptRegistry
{
    IDisposable Register(PromptContribution contribution);
    IReadOnlyList<PromptContribution> All { get; }
}

public sealed class PromptRegistry : IPromptRegistry
{
    private readonly object _gate = new();
    private readonly List<PromptContribution> _contributions = [];

    public IReadOnlyList<PromptContribution> All
    {
        get { lock (_gate) return [.. _contributions]; }
    }

    public IDisposable Register(PromptContribution contribution)
    {
        lock (_gate) _contributions.Add(contribution);
        return new Registration(this, contribution);
    }

    private void Remove(PromptContribution contribution)
    {
        lock (_gate) _contributions.Remove(contribution);
    }

    private sealed class Registration(PromptRegistry owner, PromptContribution contribution) : IDisposable
    {
        public void Dispose() => owner.Remove(contribution);
    }
}

/// <summary>Assembles the final system prompt from all registered contributions, in slot order.</summary>
public sealed class PromptComposer(IPromptProvider prompts, IPromptRegistry registry)
{
    public async Task<string> ComposeAsync(PromptRenderContext context, CancellationToken ct = default)
    {
        var parts = new List<string>();
        foreach (var contribution in registry.All.OrderBy(c => (int)c.Slot))
        {
            ct.ThrowIfCancellationRequested();
            string? text;
            try
            {
                text = await contribution.Resolver(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException)
            {
                continue; // a missing optional prompt never breaks the turn
            }

            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(prompts.Render(text.Trim(), context.Variables));
        }
        return string.Join("\n\n", parts);
    }
}
