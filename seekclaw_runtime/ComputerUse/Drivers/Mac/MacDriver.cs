namespace SeekClaw.Runtime.ComputerUse.Drivers.Mac;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SeekClaw.Runtime.ComputerUse.Abstractions;

/// <summary>
/// macOS native driver using screencapture and AppleScript System Events / cliclick.
/// </summary>
public sealed class MacDriver : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider
{
    private readonly Universal.UniversalVisionDriver _fallback = new();

    public string PlatformName => "MacNative";

    public IScreenCapture ScreenCapture => _fallback;
    public IInputController InputController => _fallback;
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
        DriverDescription: "macOS driver with AppleScript System Events and screencapture");

    public Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct) =>
        _fallback.CaptureScreenAsync(monitorIndex, ct);

    public async Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return UiHierarchyResult.Failed("Only supported on macOS.");

        try
        {
            var appName = await GetActiveWindowTitleAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(appName))
            {
                return UiHierarchyResult.Ok(appName, [new UiElementInfo("frontmost", appName, "Application", 0, 0, 1920, 1080)]);
            }

            return UiHierarchyResult.Ok("macOS Desktop", []);
        }
        catch (Exception ex)
        {
            return UiHierarchyResult.Failed($"macOS visual inspection failed: {ex.Message}");
        }
    }

    public async Task<string?> GetActiveWindowTitleAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return null;

        var script = "tell application \"System Events\" to get name of first process whose frontmost is true";
        var psi = new ProcessStartInfo("osascript", $"-e \"{script}\"")
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

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
    }

    public async Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(titleOrId))
            return false;

        try
        {
            var script = $"tell application \"{titleOrId}\" to activate";
            var psi = new ProcessStartInfo("osascript", $"-e \"{script}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                return proc.ExitCode == 0;
            }
        }
        catch { }

        return false;
    }

    public Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct) =>
        _fallback.ClickAsync(x, y, button, clickCount, ct);

    public Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct) =>
        _fallback.MoveMouseAsync(x, y, ct);

    public Task<ActionResult> TypeTextAsync(string text, CancellationToken ct) =>
        _fallback.TypeTextAsync(text, ct);

    public Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct) =>
        _fallback.SendKeyAsync(keyCombo, ct);

    public Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct) =>
        _fallback.ScrollAsync(x, y, deltaX, deltaY, ct);

    public async ValueTask DisposeAsync()
    {
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }
}
