namespace SeekClaw.Runtime.SubAgents;

public interface ISubAgentRegistry
{
    void Register(SubAgentDefinition definition);
    SubAgentDefinition? Resolve(string role);
    IReadOnlyList<SubAgentDefinition> All { get; }
}

public sealed class SubAgentRegistry : ISubAgentRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SubAgentDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public SubAgentRegistry()
    {
        RegisterDefaults();
    }

    private void RegisterDefaults()
    {
        Register(new SubAgentDefinition(
            Role: "researcher",
            Description: "Read-only research specialist. Deeply searches and reads code, inspects project structure, or consults documentation without modifying files. Returns a structured report.",
            PromptKey: "subagent/researcher",
            AllowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "read_file", "list_dir", "glob", "grep", "web_search", "web_fetch"
            },
            AllowMutating: false,
            MaxSteps: 10));

        Register(new SubAgentDefinition(
            Role: "coder",
            Description: "Focused implementation specialist. Performs surgical code edits and creates new files within its assigned task scope, adhering to file locks and style.",
            PromptKey: "subagent/coder",
            AllowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "read_file", "write_file", "edit_file", "list_dir", "glob", "grep"
            },
            AllowMutating: true,
            MaxSteps: 12));

        Register(new SubAgentDefinition(
            Role: "tester",
            Description: "Test and diagnostics specialist. Runs tests or build tools, examines error logs and stack traces, and reports diagnostic findings.",
            PromptKey: "subagent/tester",
            AllowedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "read_file", "list_dir", "glob", "grep", "bash"
            },
            AllowMutating: false,
            MaxSteps: 10));

        Register(new SubAgentDefinition(
            Role: "general",
            Description: "General-purpose delegated sub-agent for autonomous multi-step subtasks.",
            PromptKey: "subagent/general",
            AllowedTools: null,
            AllowMutating: true,
            MaxSteps: 12));
    }

    public void Register(SubAgentDefinition definition)
    {
        lock (_gate)
        {
            _definitions[definition.Role] = definition;
        }
    }

    public SubAgentDefinition? Resolve(string role)
    {
        lock (_gate)
        {
            return _definitions.GetValueOrDefault(role);
        }
    }

    public IReadOnlyList<SubAgentDefinition> All
    {
        get
        {
            lock (_gate)
            {
                return _definitions.Values.ToList();
            }
        }
    }
}
