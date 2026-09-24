namespace SeekClaw.Runtime.ComputerUse.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-responsibility contract for display and window capture (Perception).
/// </summary>
public interface IScreenCapture
{
    Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct);
}
