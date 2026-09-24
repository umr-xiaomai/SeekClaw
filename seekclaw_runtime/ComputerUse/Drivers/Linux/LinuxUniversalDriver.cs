namespace SeekClaw.Runtime.ComputerUse.Drivers.Linux;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SeekClaw.Runtime.ComputerUse.Abstractions;

/// <summary>
/// Universal Linux driver covering GNOME, KDE Plasma, XFCE, and domestic operating systems like Deepin / UOS.
/// Integrates with X11 / Wayland session detection, window management (wmctrl/xdotool), and desktop portal fallback.
/// </summary>
public sealed class LinuxUniversalDriver : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider
{
    private readonly Universal.UniversalVisionDriver _fallback = new();
    private readonly CompositeLinuxInputController _input = new();

    public string PlatformName => "LinuxUniversal";

    public IScreenCapture ScreenCapture => _fallback;
    public IInputController InputController => _input;
    public IWindowManager WindowManager => this;
    public IAccessibilityProvider AccessibilityProvider => this;

    public DriverCapabilities GetCapabilities() => new(
        CanCapture: true,
        CanInspectUiTree: true,
        CanClick: true,
        CanType: true,
        CanScroll: true,
        IsNonIntrusive: false,
        PlatformName: PlatformName,
        DriverDescription: "Universal Linux driver for GNOME / KDE / UOS with X11/Wayland adaptation");

    public Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct) =>
        _fallback.CaptureScreenAsync(monitorIndex, ct);

    public async Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return UiHierarchyResult.Failed("Only supported on Linux.");

        try
        {
            var windows = await ListWindowsAsync(ct).ConfigureAwait(false);
            if (windows.Count > 0)
            {
                var elements = windows.Select(w => new UiElementInfo(
                    Id: w.Id,
                    Name: w.Title,
                    ControlType: "Window",
                    X: w.X,
                    Y: w.Y,
                    Width: w.Width,
                    Height: w.Height
                )).ToList();

                var active = windows.FirstOrDefault(w => w.IsActive)?.Title ?? windows[0].Title;
                return UiHierarchyResult.Ok(active, elements);
            }

            // Fallback: try xdotool getactivewindow getwindowname
            var activeTitle = await GetActiveWindowTitleAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(activeTitle))
            {
                return UiHierarchyResult.Ok(activeTitle, [new UiElementInfo("active", activeTitle, "Window", 0, 0, 1920, 1080)]);
            }

            return UiHierarchyResult.Ok("Linux Desktop", []);
        }
        catch (Exception ex)
        {
            return UiHierarchyResult.Failed($"Failed to inspect Linux desktop: {ex.Message}");
        }
    }

    public async Task<string?> GetActiveWindowTitleAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return null;

        var psi = new ProcessStartInfo("xdotool", "getactivewindow getwindowname")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var title = (await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                return string.IsNullOrEmpty(title) ? null : title;
            }
        }
        catch { }

        return null;
    }

    public async Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return Array.Empty<WindowInfo>();

        try
        {
            var psi = new ProcessStartInfo("wmctrl", "-l -G -p")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return Array.Empty<WindowInfo>();

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return Array.Empty<WindowInfo>();

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<WindowInfo>();

            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // format: 0x03800003  0 1234  100  200  800  600  hostname Window Title Here
                if (parts.Length >= 8)
                {
                    var id = parts[0];
                    int.TryParse(parts[3], out var x);
                    int.TryParse(parts[4], out var y);
                    int.TryParse(parts[5], out var w);
                    int.TryParse(parts[6], out var h);
                    var title = string.Join(' ', parts.Skip(7));

                    result.Add(new WindowInfo(
                        Id: id,
                        Title: title,
                        ProcessName: parts[2],
                        X: x,
                        Y: y,
                        Width: w,
                        Height: h,
                        IsActive: false
                    ));
                }
            }

            return result;
        }
        catch
        {
            return Array.Empty<WindowInfo>();
        }
    }

    public async Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(titleOrId))
            return false;

        try
        {
            // Try wmctrl -a <titleOrId> first
            var psi = new ProcessStartInfo("wmctrl", $"-a \"{titleOrId}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                if (proc.ExitCode == 0) return true;
            }

            // Fallback: xdotool search --name
            var xdoPsi = new ProcessStartInfo("xdotool", $"search --name \"{titleOrId}\" windowactivate")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var xdoProc = Process.Start(xdoPsi);
            if (xdoProc != null)
            {
                await xdoProc.WaitForExitAsync(ct).ConfigureAwait(false);
                return xdoProc.ExitCode == 0;
            }
        }
        catch { }

        return false;
    }

    public Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct) =>
        _input.ClickAsync(x, y, button, clickCount, ct);

    public Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct) =>
        _input.MoveMouseAsync(x, y, ct);

    public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct) =>
        _input.TypeTextAsync(text, ct);

    public Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct) =>
        _input.SendKeyAsync(keyCombo, ct);

    public Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct) =>
        _input.ScrollAsync(x, y, deltaX, deltaY, ct);

    public async ValueTask DisposeAsync()
    {
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }
}
