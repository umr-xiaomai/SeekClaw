using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Events;
using SeekClaw.Runtime.Prompts;
using SeekClaw.Runtime.Providers;

namespace SeekClaw.Runtime.Tools.Builtin;

/// <summary>
/// Captures the current operating system screen and attaches it to the conversation context
/// so vision-capable models can inspect what is currently displayed on screen.
/// </summary>
public sealed class CaptureScreenTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "capture_screen";
    public override bool RequiresWorkspace => false;
    public override bool RequiresNetwork => false;
    public override bool RequiresVision => true;
    public override string StatusLabel => "Capturing screen";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("description", ToolSchema.String("Optional reason or description of what to inspect on screen."), false),
        ("monitor_index", ToolSchema.Integer("Monitor index to capture (0 for primary display). Defaults to 0."), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var reason = arguments["description"]?.GetValue<string>();
        var monitorIndex = arguments["monitor_index"]?.GetValue<int>() ?? 0;

        try
        {
            var captureResult = await CaptureScreenBytesAsync(monitorIndex, ct).ConfigureAwait(false);
            if (!captureResult.Success || captureResult.Bytes is null || captureResult.Bytes.Length == 0)
            {
                return ToolResult.Fail(captureResult.Error ?? "Failed to capture screenshot.");
            }

            var imageId = Guid.NewGuid().ToString("N");
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var imageName = $"screenshot_{timestamp}.png";
            var base64Data = Convert.ToBase64String(captureResult.Bytes);

            // Persist copy to workspace cache or temp directory
            string? savedPath = null;
            try
            {
                var targetDir = context.Workspace.IsGlobal
                    ? Path.Combine(Path.GetTempPath(), "seekclaw", "screenshots")
                    : Path.Combine(context.Workspace.CacheDir, "screenshots");
                Directory.CreateDirectory(targetDir);
                savedPath = Path.Combine(targetDir, imageName);
                await File.WriteAllBytesAsync(savedPath, captureResult.Bytes, ct).ConfigureAwait(false);
            }
            catch
            {
                // Non-critical persistence failure
            }

            var attachment = new ChatImageAttachment(
                Id: imageId,
                Name: imageName,
                MediaType: "image/png",
                Data: base64Data,
                SizeBytes: captureResult.Bytes.Length);

            var summary = $"Captured screen ({captureResult.Width}x{captureResult.Height})";
            var output = $"Screenshot captured successfully ({captureResult.Width}x{captureResult.Height} pixels)." +
                         (string.IsNullOrWhiteSpace(reason) ? "" : $"\nPurpose: {reason}") +
                         (savedPath is null ? "" : $"\nSaved to: {savedPath}") +
                         "\nThe screenshot image is attached to this turn context for your visual inspection.";

            return ToolResult.Ok(output, [attachment], summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to capture screen: {ex.Message}");
        }
    }

    private static async Task<ScreenCaptureResult> CaptureScreenBytesAsync(int monitorIndex, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var gdiResult = await Task.Run(() => WindowsScreenCapture.CaptureScreen(monitorIndex), ct).ConfigureAwait(false);
            if (gdiResult.Success && gdiResult.Bytes is { Length: > 0 })
            {
                return gdiResult;
            }
            return await CaptureWindowsPowerShellAsync(ct).ConfigureAwait(false);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await CaptureMacScreenAsync(ct).ConfigureAwait(false);
        }

        if (OperatingSystem.IsLinux())
        {
            return await CaptureLinuxScreenAsync(ct).ConfigureAwait(false);
        }

        return ScreenCaptureResult.Failed("Screen capture is not supported on this operating system.");
    }

    private static async Task<ScreenCaptureResult> CaptureWindowsPowerShellAsync(CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"screen_{Guid.NewGuid():N}.png");
        var script = $"Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $b = New-Object Drawing.Bitmap([Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [Windows.Forms.Screen]::PrimaryScreen.Bounds.Height); $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen(0,0,0,0,$b.Size); $b.Save('{tempFile.Replace("'", "''")}', [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); $g.Dispose();";
        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return ScreenCaptureResult.Failed("Failed to launch PowerShell.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(tempFile))
            {
                var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
                return ScreenCaptureResult.Ok(bytes, 0, 0);
            }
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            return ScreenCaptureResult.Failed($"PowerShell screenshot capture failed: {error}");
        }
        catch (Exception ex)
        {
            return ScreenCaptureResult.Failed($"PowerShell screenshot capture error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task<ScreenCaptureResult> CaptureMacScreenAsync(CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"screen_{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo("screencapture", $"-x \"{tempFile}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return ScreenCaptureResult.Failed("Failed to start screencapture.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tempFile))
            {
                var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                return ScreenCaptureResult.Failed($"screencapture failed with exit code {process.ExitCode}: {error}");
            }
            var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
            return ScreenCaptureResult.Ok(bytes, 0, 0);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task<ScreenCaptureResult> CaptureLinuxScreenAsync(CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"screen_{Guid.NewGuid():N}.png");
        try
        {
            // Try grim (Wayland) then import / scrot (X11)
            var commands = new[]
            {
                ("grim", $"\"{tempFile}\""),
                ("import", $"-window root \"{tempFile}\""),
                ("scrot", $"\"{tempFile}\""),
            };

            foreach (var (tool, args) in commands)
            {
                try
                {
                    var psi = new ProcessStartInfo(tool, args)
                    {
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var process = Process.Start(psi);
                    if (process is null) continue;
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    if (process.ExitCode == 0 && File.Exists(tempFile))
                    {
                        var bytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
                        return ScreenCaptureResult.Ok(bytes, 0, 0);
                    }
                }
                catch
                {
                    // Fallthrough to next command
                }
            }

            return ScreenCaptureResult.Failed("No supported screen capture tool found on Linux (checked grim, import, scrot).");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private sealed record ScreenCaptureResult(bool Success, byte[]? Bytes, int Width, int Height, string? Error)
    {
        public static ScreenCaptureResult Ok(byte[] bytes, int width, int height) =>
            new(true, bytes, width, height, null);

        public static ScreenCaptureResult Failed(string error) =>
            new(false, null, 0, 0, error);
    }

    // ---------------------------------------------------------------- Windows GDI+ Capture

    private static class WindowsScreenCapture
    {
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("gdi32.dll", EntryPoint = "CreateDCA", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        // GDI+ P/Invoke
        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public bool SuppressBackgroundThread;
            public bool SuppressExternalCodecs;

            public static GdiplusStartupInput Default => new() { GdiplusVersion = 1 };
        }

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, out IntPtr output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipSaveImageToStream(IntPtr image, IntPtr stream, ref Guid clsidEncoder, IntPtr encoderParams);

        // OLE / COM Stream for in-memory saving
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool fDeleteOnRelease, out IntPtr ppstm);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int GetHGlobalFromStream(IntPtr pstm, out IntPtr phglobal);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nuint GlobalSize(IntPtr hMem);

        // PNG Encoder GUID: 557cf406-1a04-11d3-9a73-0000f81ef32e
        private static Guid PngEncoderGuid = new("557cf406-1a04-11d3-9a73-0000f81ef32e");

        public static ScreenCaptureResult CaptureScreen(int monitorIndex)
        {
            var width = GetSystemMetrics(SM_CXSCREEN);
            var height = GetSystemMetrics(SM_CYSCREEN);

            if (width <= 0 || height <= 0)
            {
                return ScreenCaptureResult.Failed("Unable to determine screen dimensions.");
            }

            var hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return ScreenCaptureResult.Failed("Failed to acquire desktop DC.");
            }

            var hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, hdcScreen);
                return ScreenCaptureResult.Failed("Failed to create compatible memory DC.");
            }

            var hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
            {
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
                return ScreenCaptureResult.Failed("Failed to create compatible bitmap.");
            }

            var hOld = SelectObject(hdcMem, hBitmap);

            try
            {
                if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY))
                {
                    var err = Marshal.GetLastWin32Error();
                    return ScreenCaptureResult.Failed($"BitBlt screen capture failed (Win32 error: {err}, hdcMem: {hdcMem}, hdcScreen: {hdcScreen}, hBitmap: {hBitmap}, hOld: {hOld}, width: {width}, height: {height}).");
                }

                // Convert HBITMAP to PNG bytes using GDI+
                var startupInput = GdiplusStartupInput.Default;
                var status = GdiplusStartup(out var token, ref startupInput, out _);
                if (status != 0)
                {
                    return ScreenCaptureResult.Failed($"GdiplusStartup failed with code {status}.");
                }

                try
                {
                    status = GdipCreateBitmapFromHBITMAP(hBitmap, IntPtr.Zero, out var pBitmap);
                    if (status != 0 || pBitmap == IntPtr.Zero)
                    {
                        return ScreenCaptureResult.Failed($"GdipCreateBitmapFromHBITMAP failed with code {status}.");
                    }

                    try
                    {
                        var streamStatus = CreateStreamOnHGlobal(IntPtr.Zero, true, out var pStream);
                        if (streamStatus != 0 || pStream == IntPtr.Zero)
                        {
                            return ScreenCaptureResult.Failed("CreateStreamOnHGlobal failed.");
                        }

                        try
                        {
                            var encoderGuid = PngEncoderGuid;
                            status = GdipSaveImageToStream(pBitmap, pStream, ref encoderGuid, IntPtr.Zero);
                            if (status != 0)
                            {
                                return ScreenCaptureResult.Failed($"GdipSaveImageToStream failed with code {status}.");
                            }

                            if (GetHGlobalFromStream(pStream, out var hGlobal) != 0 || hGlobal == IntPtr.Zero)
                            {
                                return ScreenCaptureResult.Failed("GetHGlobalFromStream failed.");
                            }

                            var size = (int)GlobalSize(hGlobal);
                            if (size <= 0)
                            {
                                return ScreenCaptureResult.Failed("Captured PNG stream size is 0.");
                            }

                            var ptr = GlobalLock(hGlobal);
                            if (ptr == IntPtr.Zero)
                            {
                                return ScreenCaptureResult.Failed("GlobalLock failed.");
                            }

                            try
                            {
                                var buffer = new byte[size];
                                Marshal.Copy(ptr, buffer, 0, size);
                                return ScreenCaptureResult.Ok(buffer, width, height);
                            }
                            finally
                            {
                                GlobalUnlock(hGlobal);
                            }
                        }
                        finally
                        {
                            Marshal.Release(pStream);
                        }
                    }
                    finally
                    {
                        GdipDisposeImage(pBitmap);
                    }
                }
                finally
                {
                    GdiplusShutdown(token);
                }
            }
            finally
            {
                SelectObject(hdcMem, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
    }
}
