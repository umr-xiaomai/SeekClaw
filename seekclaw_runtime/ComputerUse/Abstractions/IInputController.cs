namespace SeekClaw.Runtime.ComputerUse.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-responsibility contract for mouse and keyboard simulation (Action).
/// </summary>
public interface IInputController
{
    Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct);
    Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct);
    Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct);
    Task<ActionResult> TypeTextAsync(string text, CancellationToken ct);
    Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct);
}
