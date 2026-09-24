namespace SeekClaw.Runtime.ComputerUse.Drivers.Linux;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SeekClaw.Runtime.ComputerUse.Abstractions;

public enum LinuxInputBackend
{
    Unknown,
    DevUinput,   // ydotool / /dev/uinput
    X11Xtest,    // xdotool / XTest
    WaylandPortal, // org.freedesktop.portal.RemoteDesktop / screencast
    Fallback
}

/// <summary>
/// Composite Linux input controller with intelligent environment probing.
/// Detects Wayland vs X11 sessions, permissions for /dev/uinput, and CLI automation tools (ydotool, xdotool).
/// Caches the active backend to eliminate per-action probing overhead.
/// </summary>
public sealed class CompositeLinuxInputController : IInputController
{
    private LinuxInputBackend _backend = LinuxInputBackend.Unknown;
    private readonly object _probeLock = new();
    private bool _probed;

    public LinuxInputBackend DetectedBackend
    {
        get
        {
            EnsureProbed();
            return _backend;
        }
    }

    public void Probe()
    {
        lock (_probeLock)
        {
            if (_probed) return;
            _probed = true;

            if (!OperatingSystem.IsLinux())
            {
                _backend = LinuxInputBackend.Fallback;
                return;
            }

            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.ToLowerInvariant();
            var isWayland = sessionType == "wayland" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

            // Check if ydotool is present and /dev/uinput is accessible
            var hasUinput = File.Exists("/dev/uinput");
            var hasYdotool = IsCommandAvailable("ydotool");

            // Check if xdotool is present
            var hasXdotool = IsCommandAvailable("xdotool");

            if (isWayland)
            {
                if (hasYdotool && hasUinput)
                {
                    _backend = LinuxInputBackend.DevUinput;
                }
                else if (hasXdotool)
                {
                    // Many Wayland compositors support XWayland with xdotool for legacy apps
                    _backend = LinuxInputBackend.X11Xtest;
                }
                else
                {
                    _backend = LinuxInputBackend.Fallback;
                }
            }
            else
            {
                // X11 session
                if (hasXdotool)
                {
                    _backend = LinuxInputBackend.X11Xtest;
                }
                else if (hasYdotool)
                {
                    _backend = LinuxInputBackend.DevUinput;
                }
                else
                {
                    _backend = LinuxInputBackend.Fallback;
                }
            }
        }
    }

    private void EnsureProbed()
    {
        if (!_probed)
        {
            Probe();
        }
    }

    public async Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct)
    {
        EnsureProbed();

        var btnNum = button switch
        {
            MouseButton.Right => 3,
            MouseButton.Middle => 2,
            _ => 1
        };

        if (_backend == LinuxInputBackend.DevUinput)
        {
            // ydotool click 0x110 (left), 0x111 (right), 0x112 (middle)
            var ydoCode = button switch
            {
                MouseButton.Right => "0x111",
                MouseButton.Middle => "0x112",
                _ => "0x110"
            };
            var success = await RunProcessAsync("ydotool", $"mousemove {x} {y} click {ydoCode}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Clicked at ({x}, {y}) via ydotool", "click", x, y)
                : ActionResult.Failed("ydotool click failed", "click");
        }

        // X11 / XTest / Fallback via xdotool
        var xdoSuccess = await RunProcessAsync("xdotool", $"mousemove {x} {y} click --repeat {clickCount} {btnNum}", ct).ConfigureAwait(false);
        return xdoSuccess
            ? ActionResult.Ok($"Clicked at ({x}, {y}) via xdotool", "click", x, y)
            : ActionResult.Failed("Linux click failed. Ensure 'xdotool' or 'ydotool' is installed.", "click");
    }

    public async Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct)
    {
        EnsureProbed();

        var tool = _backend == LinuxInputBackend.DevUinput ? "ydotool" : "xdotool";
        var success = await RunProcessAsync(tool, $"mousemove {x} {y}", ct).ConfigureAwait(false);
        return success
            ? ActionResult.Ok($"Mouse moved to ({x}, {y}) via {tool}", "move", x, y)
            : ActionResult.Failed($"Mouse move failed via {tool}.", "move");
    }

    public async Task<ActionResult> TypeTextAsync(string text, CancellationToken ct)
    {
        EnsureProbed();

        if (string.IsNullOrEmpty(text))
            return ActionResult.Ok("Empty text sent", "type");

        var escaped = text.Replace("\"", "\\\"");
        if (_backend == LinuxInputBackend.DevUinput)
        {
            var success = await RunProcessAsync("ydotool", $"type \"{escaped}\"", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Typed text via ydotool: {text}", "type", target: text)
                : ActionResult.Failed("ydotool typing failed.", "type");
        }

        var xdoSuccess = await RunProcessAsync("xdotool", $"type --delay 20 -- \"{escaped}\"", ct).ConfigureAwait(false);
        return xdoSuccess
            ? ActionResult.Ok($"Typed text via xdotool: {text}", "type", target: text)
            : ActionResult.Failed("xdotool typing failed.", "type");
    }

    public async Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct)
    {
        EnsureProbed();

        if (_backend == LinuxInputBackend.DevUinput)
        {
            var success = await RunProcessAsync("ydotool", $"key {keyCombo}", ct).ConfigureAwait(false);
            return success
                ? ActionResult.Ok($"Key combo sent via ydotool: {keyCombo}", "key", target: keyCombo)
                : ActionResult.Failed("ydotool key send failed.", "key");
        }

        var xdoSuccess = await RunProcessAsync("xdotool", $"key {keyCombo}", ct).ConfigureAwait(false);
        return xdoSuccess
            ? ActionResult.Ok($"Key combo sent via xdotool: {keyCombo}", "key", target: keyCombo)
            : ActionResult.Failed("xdotool key send failed.", "key");
    }

    public async Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct)
    {
        EnsureProbed();

        var button = deltaY > 0 ? 4 : 5; // 4=up, 5=down in X11
        var count = Math.Max(1, Math.Abs(deltaY) / 120);
        var success = await RunProcessAsync("xdotool", $"click --repeat {count} {button}", ct).ConfigureAwait(false);
        return success
            ? ActionResult.Ok($"Scrolled {deltaY}", "scroll", x, y)
            : ActionResult.Failed("Scroll failed on Linux.", "scroll");
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("which", command)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
}
