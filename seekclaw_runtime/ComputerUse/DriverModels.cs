using System.Text.Json.Serialization;

namespace SeekClaw.Runtime.ComputerUse;

public enum MouseButton
{
    Left,
    Right,
    Middle
}

public sealed record DriverCapabilities(
    bool CanCapture,
    bool CanInspectUiTree,
    bool CanClick,
    bool CanType,
    bool CanScroll,
    bool IsNonIntrusive,
    string PlatformName,
    string DriverDescription);

public sealed record UiElementInfo(
    string Id,
    string Name,
    string ControlType,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsEnabled = true,
    string? Value = null,
    IReadOnlyList<UiElementInfo>? Children = null)
{
    public int CenterX => X + (Width / 2);
    public int CenterY => Y + (Height / 2);
}

public sealed record ScreenCapture(
    byte[] Bytes,
    int Width,
    int Height,
    string Format = "image/png")
{
    private string? _base64;
    public string ToBase64() => _base64 ??= Convert.ToBase64String(Bytes);
}

public sealed record ActionResult(
    bool Success,
    string? Message = null,
    string? Action = null,
    int? CoordinateX = null,
    int? CoordinateY = null,
    string? TargetSummary = null,
    ScreenCapture? LatestScreenshot = null)
{
    public static ActionResult Ok(
        string message,
        string? action = null,
        int? x = null,
        int? y = null,
        string? target = null,
        ScreenCapture? screenshot = null) =>
        new(true, message, action, x, y, target, screenshot);

    public static ActionResult Failed(string errorMessage, string? action = null) =>
        new(false, errorMessage, action);
}

public sealed record UiHierarchyResult(
    bool Success,
    string? ActiveWindowTitle,
    IReadOnlyList<UiElementInfo> Elements,
    string? Error = null)
{
    public static UiHierarchyResult Ok(string? activeWindow, IReadOnlyList<UiElementInfo> elements) =>
        new(true, activeWindow, elements);

    public static UiHierarchyResult Failed(string error) =>
        new(false, null, Array.Empty<UiElementInfo>(), error);
}

public sealed record WindowInfo(
    string Id,
    string Title,
    string? ProcessName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsActive);

