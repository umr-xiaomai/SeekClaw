using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SeekClaw.Runtime.ComputerUse.Abstractions;

namespace SeekClaw.Runtime.ComputerUse.Drivers.Windows;

/// <summary>
/// Native Windows driver utilizing Win32 APIs, GDI+, and Window enumeration.
/// Provides low-overhead screen capture, high-fidelity input injection, and structural UI inspection.
/// </summary>
public sealed class WindowsDriver : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider
{
    public string PlatformName => "WindowsNative";

    public IScreenCapture ScreenCapture => this;
    public IInputController InputController => this;
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
        DriverDescription: "Windows native Win32 input injection, GDI+ capture, and control-tree inspection");

    public Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<ScreenCapture?>(null);

        return Task.Run(() =>
        {
            var width = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            var height = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            if (width <= 0 || height <= 0) return null;

            var hdcScreen = Win32.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return null;

            var hdcMem = Win32.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
            {
                Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
                return null;
            }

            var hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
            {
                Win32.DeleteDC(hdcMem);
                Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
                return null;
            }

            var hOld = Win32.SelectObject(hdcMem, hBitmap);
            try
            {
                if (!Win32.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, Win32.SRCCOPY))
                {
                    return null;
                }

                var startupInput = Win32.GdiplusStartupInput.Default;
                if (Win32.GdiplusStartup(out var token, ref startupInput, out _) != 0)
                {
                    return null;
                }

                try
                {
                    if (Win32.GdipCreateBitmapFromHBITMAP(hBitmap, IntPtr.Zero, out var pBitmap) != 0 || pBitmap == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        if (Win32.CreateStreamOnHGlobal(IntPtr.Zero, true, out var pStream) != 0 || pStream == IntPtr.Zero)
                        {
                            return null;
                        }

                        try
                        {
                            var encoderGuid = Win32.PngEncoderGuid;
                            if (Win32.GdipSaveImageToStream(pBitmap, pStream, ref encoderGuid, IntPtr.Zero) != 0)
                            {
                                return null;
                            }

                            if (Win32.GetHGlobalFromStream(pStream, out var hGlobal) != 0 || hGlobal == IntPtr.Zero)
                            {
                                return null;
                            }

                            var size = (int)Win32.GlobalSize(hGlobal);
                            if (size <= 0) return null;

                            var ptr = Win32.GlobalLock(hGlobal);
                            if (ptr == IntPtr.Zero) return null;

                            try
                            {
                                var buffer = new byte[size];
                                Marshal.Copy(ptr, buffer, 0, size);
                                return new ScreenCapture(buffer, width, height);
                            }
                            finally
                            {
                                Win32.GlobalUnlock(hGlobal);
                            }
                        }
                        finally
                        {
                            Marshal.Release(pStream);
                        }
                    }
                    finally
                    {
                        Win32.GdipDisposeImage(pBitmap);
                    }
                }
                finally
                {
                    Win32.GdiplusShutdown(token);
                }
            }
            finally
            {
                Win32.SelectObject(hdcMem, hOld);
                Win32.DeleteObject(hBitmap);
                Win32.DeleteDC(hdcMem);
                Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }, ct);
    }

    public Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(UiHierarchyResult.Failed("Only supported on Windows."));

        return Task.Run(() =>
        {
            var fgHwnd = Win32.GetForegroundWindow();
            string? windowTitle = null;
            if (fgHwnd != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                Win32.GetWindowText(fgHwnd, sb, sb.Capacity);
                windowTitle = sb.ToString();
            }

            var elements = new List<UiElementInfo>();

            // Inspect foreground window first, plus any top level interactive windows
            if (fgHwnd != IntPtr.Zero && Win32.IsWindowVisible(fgHwnd))
            {
                Win32.GetWindowRect(fgHwnd, out var winRect);
                var winWidth = winRect.Right - winRect.Left;
                var winHeight = winRect.Bottom - winRect.Top;

                var childElements = new List<UiElementInfo>();

                Win32.EnumChildWindows(fgHwnd, (childHwnd, _) =>
                {
                    if (Win32.IsWindowVisible(childHwnd))
                    {
                        Win32.GetWindowRect(childHwnd, out var childRect);
                        var w = childRect.Right - childRect.Left;
                        var h = childRect.Bottom - childRect.Top;
                        if (w > 0 && h > 0)
                        {
                            var textSb = new StringBuilder(128);
                            Win32.GetWindowText(childHwnd, textSb, textSb.Capacity);
                            var text = textSb.ToString().Trim();

                            var classSb = new StringBuilder(64);
                            Win32.GetClassName(childHwnd, classSb, classSb.Capacity);
                            var className = classSb.ToString();

                            childElements.Add(new UiElementInfo(
                                Id: $"0x{childHwnd.ToInt64():X}",
                                Name: text,
                                ControlType: className,
                                X: childRect.Left,
                                Y: childRect.Top,
                                Width: w,
                                Height: h,
                                IsEnabled: Win32.IsWindowEnabled(childHwnd)));
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                elements.Add(new UiElementInfo(
                    Id: $"0x{fgHwnd.ToInt64():X}",
                    Name: windowTitle ?? "Active Window",
                    ControlType: "Window",
                    X: winRect.Left,
                    Y: winRect.Top,
                    Width: winWidth,
                    Height: winHeight,
                    IsEnabled: true,
                    Children: childElements));
            }

            return UiHierarchyResult.Ok(windowTitle, elements);
        }, ct);
    }

    public async Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ActionResult.Failed("Click is only supported on Windows in WindowsDriver.", "click");

        return await Task.Run(async () =>
        {
            await SmoothMoveCursorAsync(x, y, ct).ConfigureAwait(false);
            await Task.Delay(30, ct).ConfigureAwait(false);

            uint downFlag, upFlag;
            switch (button)
            {
                case MouseButton.Right:
                    downFlag = Win32.MOUSEEVENTF_RIGHTDOWN;
                    upFlag = Win32.MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseButton.Middle:
                    downFlag = Win32.MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = Win32.MOUSEEVENTF_MIDDLEUP;
                    break;
                case MouseButton.Left:
                default:
                    downFlag = Win32.MOUSEEVENTF_LEFTDOWN;
                    upFlag = Win32.MOUSEEVENTF_LEFTUP;
                    break;
            }

            for (var i = 0; i < Math.Max(1, clickCount); i++)
            {
                var inputDown = new Win32.INPUT
                {
                    type = Win32.INPUT_MOUSE,
                    U = new Win32.InputUnion
                    {
                        mi = new Win32.MOUSEINPUT { dwFlags = downFlag }
                    }
                };
                var inputUp = new Win32.INPUT
                {
                    type = Win32.INPUT_MOUSE,
                    U = new Win32.InputUnion
                    {
                        mi = new Win32.MOUSEINPUT { dwFlags = upFlag }
                    }
                };

                Win32.SendInput(1, [inputDown], Marshal.SizeOf<Win32.INPUT>());
                await Task.Delay(25, ct).ConfigureAwait(false);
                Win32.SendInput(1, [inputUp], Marshal.SizeOf<Win32.INPUT>());

                if (i < clickCount - 1)
                {
                    await Task.Delay(60, ct).ConfigureAwait(false);
                }
            }

            var clickDesc = clickCount == 2 ? "Double clicked" : "Clicked";
            return ActionResult.Ok($"{clickDesc} {button} button at ({x}, {y})", "click", x, y);
        }, ct);
    }

    public async Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ActionResult.Failed("MoveMouse is only supported on Windows.", "move");

        await Task.Run(async () =>
        {
            await SmoothMoveCursorAsync(x, y, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return ActionResult.Ok($"Mouse moved to ({x}, {y})", "move", x, y);
    }

    private static async Task SmoothMoveCursorAsync(int targetX, int targetY, CancellationToken ct)
    {
        var startX = targetX;
        var startY = targetY;

        if (Win32.GetCursorPos(out var pt))
        {
            startX = pt.X;
            startY = pt.Y;
        }

        var dx = targetX - startX;
        var dy = targetY - startY;
        var distance = Math.Sqrt((double)dx * dx + (double)dy * dy);

        if (distance <= 4)
        {
            Win32.SetCursorPos(targetX, targetY);
            return;
        }

        const int stepIntervalMs = 10;
        var durationMs = Math.Clamp((int)(distance * 0.22), 70, 240);
        var totalSteps = Math.Max(4, durationMs / stepIntervalMs);

        for (var step = 1; step <= totalSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            var t = (double)step / totalSteps;
            var curX = (int)Math.Round(startX + dx * t);
            var curY = (int)Math.Round(startY + dy * t);

            Win32.SetCursorPos(curX, curY);
            await Task.Delay(stepIntervalMs, ct).ConfigureAwait(false);
        }

        Win32.SetCursorPos(targetX, targetY);
    }

    public async Task<ActionResult> TypeTextAsync(string text, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ActionResult.Failed("TypeText is only supported on Windows.", "type");

        if (string.IsNullOrEmpty(text))
            return ActionResult.Ok("Empty text sent", "type");

        return await Task.Run(async () =>
        {
            foreach (var ch in text)
            {
                ct.ThrowIfCancellationRequested();

                var inputDown = new Win32.INPUT
                {
                    type = Win32.INPUT_KEYBOARD,
                    U = new Win32.InputUnion
                    {
                        ki = new Win32.KEYBDINPUT
                        {
                            wScan = ch,
                            dwFlags = Win32.KEYEVENTF_UNICODE
                        }
                    }
                };

                var inputUp = new Win32.INPUT
                {
                    type = Win32.INPUT_KEYBOARD,
                    U = new Win32.InputUnion
                    {
                        ki = new Win32.KEYBDINPUT
                        {
                            wScan = ch,
                            dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP
                        }
                    }
                };

                Win32.SendInput(1, [inputDown], Marshal.SizeOf<Win32.INPUT>());
                await Task.Delay(10, ct).ConfigureAwait(false);
                Win32.SendInput(1, [inputUp], Marshal.SizeOf<Win32.INPUT>());
                await Task.Delay(15, ct).ConfigureAwait(false);
            }

            return ActionResult.Ok($"Typed {text.Length} characters", "type", target: text);
        }, ct);
    }

    public async Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ActionResult.Failed("SendKey is only supported on Windows.", "key");

        if (string.IsNullOrWhiteSpace(keyCombo))
            return ActionResult.Failed("Key combination cannot be empty.", "key");

        return await Task.Run(async () =>
        {
            var parts = keyCombo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var modifiers = new List<ushort>();
            ushort mainKey = 0;

            foreach (var part in parts)
            {
                var lower = part.ToLowerInvariant();
                switch (lower)
                {
                    case "ctrl":
                    case "control":
                        modifiers.Add(Win32.VK_CONTROL);
                        break;
                    case "alt":
                    case "menu":
                        modifiers.Add(Win32.VK_MENU);
                        break;
                    case "shift":
                        modifiers.Add(Win32.VK_SHIFT);
                        break;
                    case "win":
                    case "super":
                    case "cmd":
                        modifiers.Add(Win32.VK_LWIN);
                        break;
                    default:
                        mainKey = ResolveVirtualKey(lower);
                        break;
                }
            }

            // Press modifiers down
            foreach (var mod in modifiers)
            {
                SendVk(mod, false);
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            if (mainKey != 0)
            {
                SendVk(mainKey, false);
                await Task.Delay(20, ct).ConfigureAwait(false);
                SendVk(mainKey, true);
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            // Release modifiers in reverse order
            for (var i = modifiers.Count - 1; i >= 0; i--)
            {
                SendVk(modifiers[i], true);
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            return ActionResult.Ok($"Executed key combination: {keyCombo}", "key", target: keyCombo);
        }, ct);
    }

    public async Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ActionResult.Failed("Scroll is only supported on Windows.", "scroll");

        return await Task.Run(async () =>
        {
            if (x >= 0 && y >= 0)
            {
                await SmoothMoveCursorAsync(x, y, ct).ConfigureAwait(false);
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            if (deltaY != 0)
            {
                var input = new Win32.INPUT
                {
                    type = Win32.INPUT_MOUSE,
                    U = new Win32.InputUnion
                    {
                        mi = new Win32.MOUSEINPUT
                        {
                            mouseData = unchecked((uint)deltaY),
                            dwFlags = Win32.MOUSEEVENTF_WHEEL
                        }
                    }
                };
                Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
            }

            if (deltaX != 0)
            {
                var input = new Win32.INPUT
                {
                    type = Win32.INPUT_MOUSE,
                    U = new Win32.InputUnion
                    {
                        mi = new Win32.MOUSEINPUT
                        {
                            mouseData = unchecked((uint)deltaX),
                            dwFlags = Win32.MOUSEEVENTF_HWHEEL
                        }
                    }
                };
                Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
            }

            return ActionResult.Ok($"Scrolled deltaX: {deltaX}, deltaY: {deltaY}", "scroll", x, y);
        }, ct);
    }

    private static void SendVk(ushort vk, bool isKeyUp)
    {
        uint flags = 0;
        if (isKeyUp) flags |= Win32.KEYEVENTF_KEYUP;
        if (vk is Win32.VK_LWIN or Win32.VK_RWIN or Win32.VK_UP or Win32.VK_DOWN or Win32.VK_LEFT or Win32.VK_RIGHT)
        {
            flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        }

        var input = new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = flags
                }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    private static ushort ResolveVirtualKey(string key) => key switch
    {
        "enter" or "return" => Win32.VK_RETURN,
        "escape" or "esc" => Win32.VK_ESCAPE,
        "backspace" or "back" => Win32.VK_BACK,
        "tab" => Win32.VK_TAB,
        "space" => Win32.VK_SPACE,
        "delete" or "del" => Win32.VK_DELETE,
        "up" or "arrowup" => Win32.VK_UP,
        "down" or "arrowdown" => Win32.VK_DOWN,
        "left" or "arrowleft" => Win32.VK_LEFT,
        "right" or "arrowright" => Win32.VK_RIGHT,
        "home" => Win32.VK_HOME,
        "end" => Win32.VK_END,
        "pageup" or "pgup" => Win32.VK_PRIOR,
        "pagedown" or "pgdn" => Win32.VK_NEXT,
        "f1" => Win32.VK_F1,
        "f2" => Win32.VK_F2,
        "f3" => Win32.VK_F3,
        "f4" => Win32.VK_F4,
        "f5" => Win32.VK_F5,
        "f6" => Win32.VK_F6,
        "f7" => Win32.VK_F7,
        "f8" => Win32.VK_F8,
        "f9" => Win32.VK_F9,
        "f10" => Win32.VK_F10,
        "f11" => Win32.VK_F11,
        "f12" => Win32.VK_F12,
        { Length: 1 } single when single[0] >= 'a' && single[0] <= 'z' => (ushort)(single[0] - 'a' + 0x41),
        { Length: 1 } digit when digit[0] >= '0' && digit[0] <= '9' => (ushort)(digit[0] - '0' + 0x30),
        _ => 0
    };

    public Task<string?> GetActiveWindowTitleAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<string?>(null);
        var hWnd = Win32.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return Task.FromResult<string?>(null);
        var sb = new StringBuilder(512);
        Win32.GetWindowText(hWnd, sb, sb.Capacity);
        return Task.FromResult<string?>(sb.ToString());
    }

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());

        var list = new List<WindowInfo>();
        var activeHwnd = Win32.GetForegroundWindow();

        Win32.EnumWindows((hWnd, lParam) =>
        {
            if (Win32.IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(256);
                Win32.GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    Win32.GetWindowRect(hWnd, out var rect);
                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width > 0 && height > 0)
                    {
                        list.Add(new WindowInfo(
                            Id: hWnd.ToString(),
                            Title: title,
                            ProcessName: string.Empty,
                            X: rect.Left,
                            Y: rect.Top,
                            Width: width,
                            Height: height,
                            IsActive: hWnd == activeHwnd));
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return Task.FromResult<IReadOnlyList<WindowInfo>>(list);
    }

    public Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(titleOrId))
            return Task.FromResult(false);

        if (nint.TryParse(titleOrId, out var parsedHwnd))
        {
            var res = Win32.SetForegroundWindow(parsedHwnd);
            return Task.FromResult(res);
        }

        IntPtr foundHwnd = IntPtr.Zero;
        Win32.EnumWindows((hWnd, lParam) =>
        {
            if (Win32.IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(256);
                Win32.GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.Contains(titleOrId, StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (foundHwnd != IntPtr.Zero)
        {
            var res = Win32.SetForegroundWindow(foundHwnd);
            return Task.FromResult(res);
        }

        return Task.FromResult(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static class Win32
    {
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        public const int SRCCOPY = 0x00CC0020;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const ushort VK_BACK = 0x08;
        public const ushort VK_TAB = 0x09;
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_SHIFT = 0x10;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_MENU = 0x12;
        public const ushort VK_ESCAPE = 0x1B;
        public const ushort VK_SPACE = 0x20;
        public const ushort VK_PRIOR = 0x21;
        public const ushort VK_NEXT = 0x22;
        public const ushort VK_END = 0x23;
        public const ushort VK_HOME = 0x24;
        public const ushort VK_LEFT = 0x25;
        public const ushort VK_UP = 0x26;
        public const ushort VK_RIGHT = 0x27;
        public const ushort VK_DOWN = 0x28;
        public const ushort VK_DELETE = 0x2E;
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_RWIN = 0x5C;
        public const ushort VK_F1 = 0x70;
        public const ushort VK_F2 = 0x71;
        public const ushort VK_F3 = 0x72;
        public const ushort VK_F4 = 0x73;
        public const ushort VK_F5 = 0x74;
        public const ushort VK_F6 = 0x75;
        public const ushort VK_F7 = 0x76;
        public const ushort VK_F8 = 0x77;
        public const ushort VK_F9 = 0x78;
        public const ushort VK_F10 = 0x79;
        public const ushort VK_F11 = 0x7A;
        public const ushort VK_F12 = 0x7B;

        public static Guid PngEncoderGuid = new("557cf406-1a04-11d3-9a73-0000f81ef32e");

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public bool SuppressBackgroundThread;
            public bool SuppressExternalCodecs;

            public static GdiplusStartupInput Default => new() { GdiplusVersion = 1 };
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, out IntPtr output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdiplusShutdown(nuint token);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipSaveImageToStream(IntPtr image, IntPtr stream, ref Guid clsidEncoder, IntPtr encoderParams);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool fDeleteOnRelease, out IntPtr ppstm);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int GetHGlobalFromStream(IntPtr pstm, out IntPtr phglobal);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern nuint GlobalSize(IntPtr hMem);
    }
}
