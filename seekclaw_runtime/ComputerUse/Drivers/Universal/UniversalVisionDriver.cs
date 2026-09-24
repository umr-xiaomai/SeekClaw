using System.Diagnostics;
using SeekClaw.Runtime.ComputerUse.Abstractions;

namespace SeekClaw.Runtime.ComputerUse.Drivers.Universal;

/// <summary>
/// Universal vision-based driver designed for cross-platform fallback (Linux / macOS / Windows / Containers).
/// Does not depend on platform-specific C/C++ or Win32 headers; relies on standard OS CLI utilities.
/// </summary>
public sealed class UniversalVisionDriver : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider
{
    public string PlatformName => "UniversalVision";

    public IScreenCapture ScreenCapture => this;
    public IInputController InputController => this;
    public IWindowManager WindowManager => this;
    public IAccessibilityProvider AccessibilityProvider => this;

    private ScreenCapture? _lastCapture;
    public ScreenCapture? LastCapture => _lastCapture;

    public DriverCapabilities GetCapabilities() => new(
        CanCapture: true,
        CanInspectUiTree: false,
        CanClick: true,
        CanType: true,
        CanScroll: true,
        IsNonIntrusive: false,
        PlatformName: PlatformName,
        DriverDescription: "Universal cross-platform vision driver using standard system screenshot and input tools");

    public async Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"seekclaw_capture_{Guid.NewGuid():N}.png");
        try
        {
            var captured = false;

            if (OperatingSystem.IsMacOS())
            {
                captured = await RunProcessAsync("screencapture", $"-x \"{tempFile}\"", ct).ConfigureAwait(false);
            }
            else if (OperatingSystem.IsLinux())
            {
                // Try Wayland (grim) first, then X11 tools (maim, scrot, import)
                captured = await RunProcessAsync("grim", $"\"{tempFile}\"", ct).ConfigureAwait(false)
                        || await RunProcessAsync("maim", $"\"{tempFile}\"", ct).ConfigureAwait(false)
                        || await RunProcessAsync("scrot", $"\"{tempFile}\"", ct).ConfigureAwait(false)
                        || await RunProcessAsync("import", $"-window root \"{tempFile}\"", ct).ConfigureAwait(false);
            }
            else if (OperatingSystem.IsWindows())
            {
                var script = $"Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $b = New-Object Drawing.Bitmap([Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [Windows.Forms.Screen]::PrimaryScreen.Bounds.Height); $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen(0,0,0,0,$b.Size); $b.Save('{tempFile.Replace("'", "''")}', [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); $g.Dispose();";
                captured = await RunProcessAsync("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"", ct).ConfigureAwait(false);
            }

            if (captured && File.Exists(tempFile))
            {
                var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
                var (width, height) = ParsePngDimensions(bytes);
                var capture = new ScreenCapture(bytes, width, height);
                _lastCapture = capture;
                return capture;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    public Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct)
    {
        return Task.FromResult(UiHierarchyResult.Failed("Visual element inspection is not supported in UniversalVision driver. Please use screen coordinates or install platform-native drivers."));
    }

    public async Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var btnNum = button switch
            {
                MouseButton.Right => 3,
                MouseButton.Middle => 2,
                _ => 1
            };
            var success = await RunProcessAsync("xdotool", $"mousemove {x} {y} click --repeat {clickCount} {btnNum}", ct).ConfigureAwait(false)
                       || await RunProcessAsync("ydotool", $"mousemove {x} {y} click 0x110", ct).ConfigureAwait(false);

            return success
                ? ActionResult.Ok($"Clicked at ({x}, {y})", "click", x, y)
                : ActionResult.Failed("Click failed. Ensure 'xdotool' or 'ydotool' is installed on Linux.", "click");
        }

        if (OperatingSystem.IsMacOS())
        {
            var success = await RunProcessAsync("cliclick", $"c:{x},{y}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Clicked at ({x}, {y}) via cliclick", "click", x, y)
                : ActionResult.Failed("Click failed on macOS. Ensure 'cliclick' is installed (`brew install cliclick`).", "click");
        }

        return ActionResult.Failed("Direct click is not implemented in UniversalVision driver for this OS.", "click");
    }

    public async Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var success = await RunProcessAsync("xdotool", $"mousemove {x} {y}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Mouse moved to ({x}, {y})", "move", x, y)
                : ActionResult.Failed("Mouse move failed. Check xdotool installation.", "move");
        }

        if (OperatingSystem.IsMacOS())
        {
            var success = await RunProcessAsync("cliclick", $"m:{x},{y}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Mouse moved to ({x}, {y})", "move", x, y)
                : ActionResult.Failed("Mouse move failed on macOS.", "move");
        }

        return ActionResult.Failed("MoveMouse not supported on this platform.", "move");
    }

    public async Task<ActionResult> TypeTextAsync(string text, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var success = await RunProcessAsync("xdotool", $"type --delay 20 -- \"{text.Replace("\"", "\\\"")}\"", ct).ConfigureAwait(false)
                       || await RunProcessAsync("ydotool", $"type \"{text.Replace("\"", "\\\"")}\"", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Typed text: {text}", "type", target: text)
                : ActionResult.Failed("TypeText failed on Linux. Check xdotool / ydotool.", "type");
        }

        if (OperatingSystem.IsMacOS())
        {
            var script = $"tell application \"System Events\" to keystroke \"{text.Replace("\"", "\\\"")}\"";
            var success = await RunProcessAsync("osascript", $"-e \"{script}\"", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Typed text: {text}", "type", target: text)
                : ActionResult.Failed("TypeText failed on macOS.", "type");
        }

        return ActionResult.Failed("TypeText not supported on this platform in UniversalVision driver.", "type");
    }

    public async Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var success = await RunProcessAsync("xdotool", $"key {keyCombo}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Sent key combo: {keyCombo}", "key", target: keyCombo)
                : ActionResult.Failed("SendKey failed on Linux.", "key");
        }

        if (OperatingSystem.IsMacOS())
        {
            var success = await RunProcessAsync("cliclick", $"kd:{keyCombo} ku:{keyCombo}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Sent key combo: {keyCombo}", "key", target: keyCombo)
                : ActionResult.Failed("SendKey failed on macOS.", "key");
        }

        return ActionResult.Failed("SendKey not supported on this platform.", "key");
    }

    public async Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var button = deltaY > 0 ? 4 : 5; // 4=up, 5=down in X11
            var count = Math.Max(1, Math.Abs(deltaY) / 120);
            var success = await RunProcessAsync("xdotool", $"click --repeat {count} {button}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Scrolled {deltaY}", "scroll", x, y)
                : ActionResult.Failed("Scroll failed on Linux.", "scroll");
        }

        return ActionResult.Failed("Scroll not supported on this platform.", "scroll");
    }

    private static async Task<bool> RunProcessAsync(string command, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return false;

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetActiveWindowTitleAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var psi = new ProcessStartInfo("xdotool", "getactivewindow getwindowname")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var title = (await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                    return string.IsNullOrEmpty(title) ? null : title;
                }
            }
            catch { }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var script = "tell application \"System Events\" to get name of first process whose frontmost is true";
            var psi = new ProcessStartInfo("osascript", $"-e \"{script}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var title = (await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                    return string.IsNullOrEmpty(title) ? null : title;
                }
            }
            catch { }
        }

        return null;
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
    }

    public async Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            return await RunProcessAsync("xdotool", $"search --name \"{titleOrId}\" windowactivate", ct).ConfigureAwait(false);
        }
        if (OperatingSystem.IsMacOS())
        {
            var script = $"tell application \"{titleOrId}\" to activate";
            return await RunProcessAsync("osascript", $"-e \"{script}\"", ct).ConfigureAwait(false);
        }
        return false;
    }

    private static (int width, int height) ParsePngDimensions(byte[] bytes)
    {
        if (bytes.Length >= 24 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
        {
            var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return (w, h);
        }
        return (0, 0);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
