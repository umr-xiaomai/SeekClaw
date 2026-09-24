namespace SeekClaw.Runtime.ComputerUse;

using System;
using System.Threading;
using System.Threading.Tasks;
using SeekClaw.Runtime.ComputerUse.Abstractions;

/// <summary>
/// Composed contract for computer observation and automation.
/// Unifies specialized perception (screen capture, accessibility),
/// action (input control), and window management sub-interfaces.
/// </summary>
public interface IComputerDriver : IAsyncDisposable
{
    string PlatformName { get; }
    DriverCapabilities GetCapabilities();

    IScreenCapture ScreenCapture { get; }
    IInputController InputController { get; }
    IWindowManager WindowManager { get; }
    IAccessibilityProvider AccessibilityProvider { get; }

    // Backward-compatible default delegation methods
    Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct) =>
        ScreenCapture.CaptureScreenAsync(monitorIndex, ct);

    Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct) =>
        AccessibilityProvider.GetVisualElementsAsync(ct);

    Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct) =>
        InputController.ClickAsync(x, y, button, clickCount, ct);

    Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct) =>
        InputController.MoveMouseAsync(x, y, ct);

    Task<ActionResult> TypeTextAsync(string text, CancellationToken ct) =>
        InputController.TypeTextAsync(text, ct);

    Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct) =>
        InputController.SendKeyAsync(keyCombo, ct);

    Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct) =>
        InputController.ScrollAsync(x, y, deltaX, deltaY, ct);
}
