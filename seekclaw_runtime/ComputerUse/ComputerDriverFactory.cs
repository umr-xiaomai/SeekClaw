using SeekClaw.Runtime.ComputerUse.Drivers.Linux;
using SeekClaw.Runtime.ComputerUse.Drivers.Mac;
using SeekClaw.Runtime.ComputerUse.Drivers.Universal;
using SeekClaw.Runtime.ComputerUse.Drivers.Windows;

namespace SeekClaw.Runtime.ComputerUse;

/// <summary>
/// Factory that selects, creates, and sandboxes the appropriate computer automation driver.
/// </summary>
public static class ComputerDriverFactory
{
    public static IComputerDriver CreateDriver(ComputerUseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var mode = config.Driver?.Trim().ToLowerInvariant() ?? "auto";

        IComputerDriver inner = mode switch
        {
            "windows" when OperatingSystem.IsWindows() => new WindowsDriver(),
            "linux" when OperatingSystem.IsLinux() => new LinuxUniversalDriver(),
            "mac" or "macos" when OperatingSystem.IsMacOS() => new MacDriver(),
            "vision" => new UniversalVisionDriver(),
            "auto" => DetectBestDriver(),
            _ => DetectBestDriver()
        };

        return new DriverFaultSandbox(inner);
    }

    private static IComputerDriver DetectBestDriver()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDriver();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxUniversalDriver();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacDriver();
        }

        return new UniversalVisionDriver();
    }
}
