namespace SeekClaw.Runtime.ComputerUse.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Single-responsibility contract for UI accessibility and control tree inspection (Perception).
/// Windows: UI Automation / IAccessible2
/// Linux: AT-SPI2 D-Bus
/// macOS: NSAccessibility / AXUIElement
/// </summary>
public interface IAccessibilityProvider
{
    Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct);
}
