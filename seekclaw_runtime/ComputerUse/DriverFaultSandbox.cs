namespace SeekClaw.Runtime.ComputerUse;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SeekClaw.Runtime.ComputerUse.Abstractions;

/// <summary>
/// Dual-layer fault-isolation sandbox wrapping any IComputerDriver.
/// Guarantees that no driver crash, unhandled P/Invoke exception, or OS failure
/// will ever propagate up or crash the SeekClaw runtime, daemon, or desktop.
/// Automatically trips a circuit breaker upon repetitive severe failures.
/// </summary>
public sealed class DriverFaultSandbox(IComputerDriver inner, int maxConsecutiveFailures = 5) 
    : IComputerDriver, IScreenCapture, IInputController, IWindowManager, IAccessibilityProvider
{
    private readonly IComputerDriver _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly int _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
    private int _consecutiveFailures;
    private int _totalFailures;
    private bool _isCircuitTripped;

    public string PlatformName => _inner.PlatformName;
    public int ConsecutiveFailures => _consecutiveFailures;
    public int TotalFailures => _totalFailures;
    public bool IsCircuitTripped => _isCircuitTripped;

    public IScreenCapture ScreenCapture => this;
    public IInputController InputController => this;
    public IWindowManager WindowManager => this;
    public IAccessibilityProvider AccessibilityProvider => this;

    public DriverCapabilities GetCapabilities()
    {
        try
        {
            return _inner.GetCapabilities();
        }
        catch (Exception ex)
        {
            RecordFailure();
            return new DriverCapabilities(
                CanCapture: false,
                CanInspectUiTree: false,
                CanClick: false,
                CanType: false,
                CanScroll: false,
                IsNonIntrusive: true,
                PlatformName: _inner.PlatformName,
                DriverDescription: $"Sandboxed driver (error reading capabilities: {ex.Message})");
        }
    }

    public ScreenMetrics GetScreenMetrics()
    {
        try
        {
            return _inner.ScreenCapture.GetScreenMetrics();
        }
        catch
        {
            return ScreenMetrics.Default;
        }
    }

    public ScreenCapture? LastCapture
    {
        get
        {
            try
            {
                return _inner.ScreenCapture.LastCapture;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<ScreenCapture?> CaptureScreenAsync(int monitorIndex, CancellationToken ct)
    {
        if (_isCircuitTripped) return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _inner.ScreenCapture.CaptureScreenAsync(monitorIndex, timeoutCts.Token).ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            RecordFailure();
            return null;
        }
    }

    public async Task<UiHierarchyResult> GetVisualElementsAsync(CancellationToken ct)
    {
        if (_isCircuitTripped)
        {
            return UiHierarchyResult.Failed("Driver circuit breaker is open due to repeated failures.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _inner.AccessibilityProvider.GetVisualElementsAsync(timeoutCts.Token).ConfigureAwait(false);
            if (result.Success)
            {
                RecordSuccess();
            }
            else
            {
                RecordFailure();
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return UiHierarchyResult.Failed($"Driver sandbox captured inspection exception: {ex.Message}");
        }
    }

    public async Task<ActionResult> ClickAsync(int x, int y, MouseButton button, int clickCount, CancellationToken ct)
    {
        if (_isCircuitTripped)
        {
            return ActionResult.Failed("Driver circuit breaker is open.", "click");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _inner.InputController.ClickAsync(x, y, button, clickCount, timeoutCts.Token).ConfigureAwait(false);
            if (result.Success) RecordSuccess(); else RecordFailure();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return ActionResult.Failed($"Click failed due to driver exception: {ex.Message}", "click");
        }
    }

    public async Task<ActionResult> MoveMouseAsync(int x, int y, CancellationToken ct)
    {
        if (_isCircuitTripped)
        {
            return ActionResult.Failed("Driver circuit breaker is open.", "move");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await _inner.InputController.MoveMouseAsync(x, y, timeoutCts.Token).ConfigureAwait(false);
            if (result.Success) RecordSuccess(); else RecordFailure();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return ActionResult.Failed($"Mouse move failed due to driver exception: {ex.Message}", "move");
        }
    }

    public async Task<ActionResult> TypeTextAsync(string text, CancellationToken ct)
    {
        if (_isCircuitTripped)
        {
            return ActionResult.Failed("Driver circuit breaker is open.", "type");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await _inner.InputController.TypeTextAsync(text, timeoutCts.Token).ConfigureAwait(false);
            if (result.Success) RecordSuccess(); else RecordFailure();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return ActionResult.Failed($"Text typing failed due to driver exception: {ex.Message}", "type");
        }
    }

    public async Task<ActionResult> SendKeyAsync(string keyCombo, CancellationToken ct)
    {
        if (_isCircuitTripped)
        {
            return ActionResult.Failed("Driver circuit breaker is open.", "key");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _inner.InputController.SendKeyAsync(keyCombo, timeoutCts.Token).ConfigureAwait(false);
            if (result.Success) RecordSuccess(); else RecordFailure();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return ActionResult.Failed($"Key send failed due to driver exception: {ex.Message}", "key");
        }
    }

    public async Task<ActionResult> ScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken ct)
    {
        if (_isCircuitTripped)
        {
            return ActionResult.Failed("Driver circuit breaker is open.", "scroll");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _inner.InputController.ScrollAsync(x, y, deltaX, deltaY, timeoutCts.Token).ConfigureAwait(false);
            if (result.Success) RecordSuccess(); else RecordFailure();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            return ActionResult.Failed($"Scroll failed due to driver exception: {ex.Message}", "scroll");
        }
    }

    public async Task<string?> GetActiveWindowTitleAsync(CancellationToken ct)
    {
        if (_isCircuitTripped) return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await _inner.WindowManager.GetActiveWindowTitleAsync(timeoutCts.Token).ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            RecordFailure();
            return null;
        }
    }

    public async Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        if (_isCircuitTripped) return Array.Empty<WindowInfo>();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await _inner.WindowManager.ListWindowsAsync(timeoutCts.Token).ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            RecordFailure();
            return Array.Empty<WindowInfo>();
        }
    }

    public async Task<bool> FocusWindowAsync(string titleOrId, CancellationToken ct)
    {
        if (_isCircuitTripped) return false;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await _inner.WindowManager.FocusWindowAsync(titleOrId, timeoutCts.Token).ConfigureAwait(false);
            if (result) RecordSuccess(); else RecordFailure();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            RecordFailure();
            return false;
        }
    }

    public void ResetCircuit()
    {
        _consecutiveFailures = 0;
        _isCircuitTripped = false;
    }

    private void RecordSuccess()
    {
        _consecutiveFailures = 0;
    }

    private void RecordFailure()
    {
        _consecutiveFailures++;
        _totalFailures++;
        if (_consecutiveFailures >= _maxConsecutiveFailures)
        {
            _isCircuitTripped = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Silently absorb disposal failures in sandbox
        }
    }
}
