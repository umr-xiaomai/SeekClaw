namespace SeekClaw.Runtime.ComputerUse;

using System;
using System.Threading.Tasks;
using SeekClaw.Runtime.Extensions;
using SeekClaw.Runtime.Tools;

/// <summary>
/// Runtime extension providing Computer Use tools and capabilities.
/// Implements IRuntimeExtension so core runtime has zero static coupling to Computer Use.
/// </summary>
public sealed class ComputerUseExtension : IRuntimeExtension
{
    private IComputerDriver? _activeDriver;

    public string Id => "computer_use";
    public string Name => "Computer Use Subsystem";

    public bool IsEnabled(SeekClawRuntime runtime)
    {
        var config = runtime.ConfigStore.Config.ComputerUse;
        return config is not null && config.Enabled;
    }

    public void Initialize(SeekClawRuntime runtime)
    {
        var config = runtime.ConfigStore.Config.ComputerUse;
        if (config is null || !config.Enabled) return;

        _activeDriver = ComputerDriverFactory.CreateDriver(config);
    }

    public void RegisterTools(IToolRegistry registry, SeekClawRuntime runtime)
    {
        if (_activeDriver is null) return;
        var config = runtime.ConfigStore.Config.ComputerUse!;
        registry.Register(new ComputerInspectTool(runtime.Prompts, _activeDriver));
        registry.Register(new ComputerTool(runtime.Prompts, _activeDriver, config));
    }

    public async ValueTask DisposeAsync()
    {
        if (_activeDriver is not null)
        {
            var driver = _activeDriver;
            _activeDriver = null;
            await driver.DisposeAsync().ConfigureAwait(false);
        }
    }
}
