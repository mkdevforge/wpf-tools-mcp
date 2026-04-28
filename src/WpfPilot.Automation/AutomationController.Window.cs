using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    public async Task<SetWindowBoundsResponse> SetWindowBoundsAsync(
        SetWindowBoundsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("set_window_bounds");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            var window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            var hwnd = window.Properties.NativeWindowHandle.Value;
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Window handle is not available.");
            }

            if (!GetWindowRect(hwnd, out var previousRect) || previousRect.Width <= 0 || previousRect.Height <= 0)
            {
                throw new InvalidOperationException($"GetWindowRect failed: {Marshal.GetLastWin32Error()}");
            }

            var previous = new Rect(previousRect.Left, previousRect.Top, previousRect.Width, previousRect.Height);

            var desiredX = request.X ?? previous.X;
            var desiredY = request.Y ?? previous.Y;
            var desiredW = request.Width ?? previous.Width;
            var desiredH = request.Height ?? previous.Height;

            desiredW = Math.Max(1, desiredW);
            desiredH = Math.Max(1, desiredH);

            var wasClamped = false;
            if (request.ClampToVirtualScreen)
            {
                var virtualScreen = DisplayDiagnostics.GetVirtualScreenBounds();
                var clamped = DisplayDiagnostics.ClampBoundsToVirtualScreen(
                    new Rect(desiredX, desiredY, desiredW, desiredH),
                    virtualScreen,
                    out wasClamped);
                desiredX = clamped.X;
                desiredY = clamped.Y;
                desiredW = clamped.Width;
                desiredH = clamped.Height;
            }

            // Restore before resizing if minimized/maximized.
            var windowPattern = window.Patterns.Window.PatternOrDefault;
            if (windowPattern is not null &&
                (windowPattern.WindowVisualState == WindowVisualState.Minimized ||
                 windowPattern.WindowVisualState == WindowVisualState.Maximized))
            {
                try
                {
                    windowPattern.SetWindowVisualState(WindowVisualState.Normal);
                }
                catch
                {
                }

                await Task.Delay(Math.Max(UiDelayWindowSettleMs, 100), cancellationToken);
            }
            else
            {
                // Best-effort restore even if WindowPattern is missing.
                _ = ShowWindow(hwnd, SW_RESTORE);
            }

            if (request.EnsureForeground)
            {
                await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);
            }

            var flags = SWP_NOZORDER;
            if (!request.EnsureForeground)
            {
                flags |= SWP_NOACTIVATE;
            }

            if (!SetWindowPos(hwnd, IntPtr.Zero, desiredX, desiredY, desiredW, desiredH, flags))
            {
                throw new InvalidOperationException($"SetWindowPos failed: {Marshal.GetLastWin32Error()}");
            }

            await Task.Delay(UiDelayWindowSettleMs, cancellationToken);

            if (!GetWindowRect(hwnd, out var newRect) || newRect.Width <= 0 || newRect.Height <= 0)
            {
                throw new InvalidOperationException($"GetWindowRect failed after resize: {Marshal.GetLastWin32Error()}");
            }

            var updated = newRect.Left != previousRect.Left ||
                          newRect.Top != previousRect.Top ||
                          newRect.Width != previousRect.Width ||
                          newRect.Height != previousRect.Height;

            var next = new Rect(newRect.Left, newRect.Top, newRect.Width, newRect.Height);

            var response = new SetWindowBoundsResponse(
                Updated: updated,
                WindowHandleUsed: hwnd.ToInt64(),
                PreviousBounds: previous,
                NewBounds: next,
                WasClamped: wasClamped);

            trace?.SetSummary($"{response.WindowHandleUsed} {previous.Width}x{previous.Height} -> {next.Width}x{next.Height} ({next.X},{next.Y}) clamped={response.WasClamped}");
            return response;
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    public async Task<SetWindowStateResponse> SetWindowStateAsync(
        SetWindowStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("set_window_state");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            var window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            var hwnd = window.Properties.NativeWindowHandle.Value;
            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Window handle is not available.");
            }

            var windowPattern = window.Patterns.Window.PatternOrDefault;
            WindowVisualState? previousState = null;
            if (windowPattern is not null)
            {
                try
                {
                    previousState = windowPattern.WindowVisualState;
                }
                catch
                {
                }
            }

            var target = request.State switch
            {
                WindowState.Normal => WindowVisualState.Normal,
                WindowState.Minimized => WindowVisualState.Minimized,
                WindowState.Maximized => WindowVisualState.Maximized,
                _ => throw new ArgumentOutOfRangeException(nameof(request.State), request.State, "Unsupported window state.")
            };

            var updated = true;
            if (previousState is not null)
            {
                updated = previousState.Value != target;
            }

            if (windowPattern is not null)
            {
                try
                {
                    windowPattern.SetWindowVisualState(target);
                }
                catch
                {
                    updated = false;
                }
            }
            else
            {
                var show = request.State switch
                {
                    WindowState.Normal => SW_RESTORE,
                    WindowState.Minimized => SW_MINIMIZE,
                    WindowState.Maximized => SW_MAXIMIZE,
                    _ => SW_RESTORE
                };

                updated = ShowWindow(hwnd, show);
            }

            if (request.EnsureForeground && request.State != WindowState.Minimized)
            {
                await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);
            }
            else
            {
                await Task.Delay(UiDelayWindowSettleMs, cancellationToken);
            }

            var response = new SetWindowStateResponse(
                Updated: updated,
                WindowHandleUsed: hwnd.ToInt64(),
                State: request.State);

            trace?.SetSummary($"{response.WindowHandleUsed} state={response.State} updated={response.Updated}");
            return response;
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
