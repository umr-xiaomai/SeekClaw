namespace SeekClaw.Runtime.Extensions;

using SeekClaw.Runtime.Tools;

/// <summary>
/// Pluggable runtime capability extension contract.
/// Extensions can register custom tools, prompt contributions, and lifecycle hooks
/// without introducing hard dependencies from core runtime to the extension module.
/// </summary>
public interface IRuntimeExtension : IAsyncDisposable
{
    /// <summary>Unique identifier of the extension (e.g. "computer_use").</summary>
    string Id { get; }

    /// <summary>Human-readable display name of the extension.</summary>
    string Name { get; }

    /// <summary>Whether this extension is currently enabled in configuration.</summary>
    bool IsEnabled(SeekClawRuntime runtime);

    /// <summary>Initializes services and configurations for this extension.</summary>
    void Initialize(SeekClawRuntime runtime);

    /// <summary>Registers tools provided by this extension into the tool registry.</summary>
    void RegisterTools(IToolRegistry registry, SeekClawRuntime runtime);
}
