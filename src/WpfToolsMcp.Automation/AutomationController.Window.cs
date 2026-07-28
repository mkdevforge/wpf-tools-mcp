using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

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
            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var effects = new InteractionEffectTracker();
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

            if (request.EnsureForeground)
            {
                await EnsureWindowForegroundAsync(
                    window,
                    operation: "set_window_bounds",
                    policy,
                    effects,
                    settleDelayMs: UiDelayWindowSettleMs,
                    cancellationToken).ConfigureAwait(false);
            }

            // Restore before resizing if minimized/maximized.
            var windowPattern = window.Patterns.Window.PatternOrDefault;
            WindowVisualState? currentState = null;
            if (windowPattern is not null)
            {
                try
                {
                    currentState = windowPattern.WindowVisualState;
                }
                catch
                {
                }
            }

            var nativeState = GetNativeWindowState(hwnd);
            var needsRestore = currentState is WindowVisualState.Minimized or WindowVisualState.Maximized ||
                               nativeState != WindowState.Normal;
            if (needsRestore)
            {
                var restoredWithWindowPattern = false;
                if (request.EnsureForeground && windowPattern is not null)
                {
                    try
                    {
                        windowPattern.SetWindowVisualState(WindowVisualState.Normal);
                        effects.MarkSemantic();
                        restoredWithWindowPattern = true;
                    }
                    catch
                    {
                    }
                }

                if (!restoredWithWindowPattern)
                {
                    // SW_SHOWNOACTIVATE restores the normal bounds without stealing foreground.
                    _ = ShowWindow(hwnd, request.EnsureForeground ? SW_RESTORE : SW_SHOWNOACTIVATE);
                }

                await Task.Delay(Math.Max(UiDelayWindowSettleMs, 100), cancellationToken);
                if (GetNativeWindowState(hwnd) != WindowState.Normal)
                {
                    throw new InvalidOperationException(
                        $"window_state_change_failed: set_window_bounds could not restore window {hwnd.ToInt64()} to normal state.");
                }

                effects.MarkWindowRestored();
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
                WasClamped: wasClamped,
                Effects: effects.ToContract());

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
            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var effects = new InteractionEffectTracker();
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
            WindowVisualState? previousVisualState = null;
            if (windowPattern is not null)
            {
                try
                {
                    previousVisualState = windowPattern.WindowVisualState;
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
            var previousState = previousVisualState switch
            {
                WindowVisualState.Normal => WindowState.Normal,
                WindowVisualState.Minimized => WindowState.Minimized,
                WindowVisualState.Maximized => WindowState.Maximized,
                _ => GetNativeWindowState(hwnd)
            };

            if (request.EnsureForeground && request.State != WindowState.Minimized)
            {
                await EnsureWindowForegroundAsync(
                    window,
                    operation: "set_window_state",
                    policy,
                    effects,
                    settleDelayMs: UiDelayWindowSettleMs,
                    cancellationToken).ConfigureAwait(false);
            }

            var stateChanged = previousState != request.State;
            var foregroundBeforeStateChange = GetForegroundWindow();
            if (stateChanged &&
                request.State == WindowState.Maximized &&
                foregroundBeforeStateChange != hwnd &&
                !policy.AllowForegroundActivation)
            {
                throw InteractionPolicyResolver.Blocked(
                    operation: "set_window_state",
                    requiredEffect: "foreground activation for a maximize transition",
                    policySetting: "allowForegroundActivation",
                    alternative: "Retry with interactionPolicy.allowForegroundActivation=true, or foreground the target window explicitly before maximizing it.");
            }

            if (stateChanged)
            {
                var useNonActivatingNativePath = !request.EnsureForeground &&
                                                 foregroundBeforeStateChange != hwnd &&
                                                 request.State is WindowState.Normal or WindowState.Minimized;
                if (useNonActivatingNativePath || windowPattern is null)
                {
                    var show = request.State switch
                    {
                        WindowState.Normal => request.EnsureForeground ? SW_RESTORE : SW_SHOWNOACTIVATE,
                        WindowState.Minimized => SW_SHOWMINNOACTIVE,
                        WindowState.Maximized => SW_MAXIMIZE,
                        _ => throw new ArgumentOutOfRangeException(nameof(request.State), request.State, "Unsupported window state.")
                    };

                    _ = ShowWindow(hwnd, show);
                }
                else
                {
                    try
                    {
                        windowPattern.SetWindowVisualState(target);
                        effects.MarkSemantic();
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"window_state_change_failed: UI Automation could not set window {hwnd.ToInt64()} to {request.State}.",
                            ex);
                    }
                }
            }

            if (stateChanged)
            {
                await Task.Delay(UiDelayWindowSettleMs, cancellationToken);
            }

            var actualState = GetNativeWindowState(hwnd);
            var updated = stateChanged && actualState == request.State;
            if (updated && previousState != WindowState.Normal && request.State == WindowState.Normal)
            {
                effects.MarkWindowRestored();
            }

            if (foregroundBeforeStateChange != hwnd &&
                GetForegroundWindow() == hwnd)
            {
                effects.MarkForegroundActivated();
            }

            var response = new SetWindowStateResponse(
                Updated: updated,
                WindowHandleUsed: hwnd.ToInt64(),
                State: request.State,
                Effects: effects.ToContract());

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

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const int SW_RESTORE = 9;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    private static WindowState GetNativeWindowState(IntPtr hwnd) =>
        IsIconic(hwnd)
            ? WindowState.Minimized
            : IsZoomed(hwnd)
                ? WindowState.Maximized
                : WindowState.Normal;
}
