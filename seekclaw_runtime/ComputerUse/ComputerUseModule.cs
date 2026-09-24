namespace SeekClaw.Runtime.ComputerUse;

/// <summary>
/// Modular lifecycle and registration facade for the Computer Use subsystem.
/// Enables completely decoupled plugging and unplugging without leaving residue in core runtime.
/// </summary>
public static class ComputerUseModule
{
    private static IComputerDriver? _activeDriver;

    /// <summary>
    /// Registers computer use tools if enabled in configuration.
    /// If disabled (default), performs zero work, registers nothing, and has zero runtime footprint.
    /// </summary>
    public static void Initialize(SeekClawRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var config = runtime.ConfigStore.Config.ComputerUse;
        if (config is null || !config.Enabled)
        {
            return;
        }

        var driver = ComputerDriverFactory.CreateDriver(config);
        _activeDriver = driver;

        runtime.Tools.Register(new ComputerInspectTool(runtime.Prompts, driver));
        runtime.Tools.Register(new ComputerTool(runtime.Prompts, driver, config));

    }

    /// <summary>
    /// Gracefully releases any active driver resources.
    /// </summary>
    public static async ValueTask DisposeAsync()
    {
        if (_activeDriver is not null)
        {
            var driver = _activeDriver;
            _activeDriver = null;
            await driver.DisposeAsync().ConfigureAwait(false);
        }
    }
}
