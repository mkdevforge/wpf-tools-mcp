using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public async Task<MouseClickResponse> MouseClickAsync(
        MouseClickRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("mouse_click");
        try
        {
            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var effects = new InteractionEffectTracker();
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            var windowHandleUsed = request.WindowHandle
                ?? FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();

            Window? window = null;
            if (request.EnsureForeground || request.CoordSpace == MouseCoordinateSpace.Client)
            {
                try
                {
                    window = FindWindowByHandle(application, automation, windowHandleUsed);
                }
                catch
                {
                    trace?.SetSummary("clicked=false reason=window_not_found");
                    return new MouseClickResponse(
                        Clicked: false,
                        XScreen: request.X,
                        YScreen: request.Y,
                        CoordSpaceUsed: request.CoordSpace,
                        Error: "window_not_found",
                        MethodUsed: "mouse",
                        Effects: effects.ToContract());
                }

                if (request.EnsureForeground)
                {
                    await PrepareWindowForPhysicalInputAsync(
                        window,
                        operation: "mouse_click",
                        policy,
                        effects,
                        semanticAlternative: "Use click_element for an element-targeted semantic action when available.",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (!request.EnsureForeground)
            {
                EnsurePhysicalInputAllowed(
                    operation: "mouse_click",
                    policy,
                    semanticAlternative: "Use click_element for an element-targeted semantic action when available.");
            }

            int xScreen;
            int yScreen;
            var coordSpaceUsed = request.CoordSpace;

            switch (request.CoordSpace)
            {
                case MouseCoordinateSpace.Screen:
                    xScreen = request.X;
                    yScreen = request.Y;
                    break;
                case MouseCoordinateSpace.Client:
                    var hwnd = new IntPtr(windowHandleUsed);
                    if (!TryGetClientTopLeftScreen(hwnd, out var clientTopLeft))
                    {
                        trace?.SetSummary("clicked=false reason=client_origin_unavailable");
                        return new MouseClickResponse(
                            Clicked: false,
                            XScreen: request.X,
                            YScreen: request.Y,
                            CoordSpaceUsed: coordSpaceUsed,
                            Error: "client_origin_unavailable",
                            MethodUsed: "mouse",
                            Effects: effects.ToContract());
                    }

                    xScreen = clientTopLeft.X + request.X;
                    yScreen = clientTopLeft.Y + request.Y;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.CoordSpace), request.CoordSpace, "Unsupported coordinate space.");
            }

            var mouseButton = request.Button switch
            {
                MouseButtonKind.Left => MouseButton.Left,
                MouseButtonKind.Right => MouseButton.Right,
                MouseButtonKind.Middle => MouseButton.Middle,
                _ => throw new ArgumentOutOfRangeException(nameof(request.Button), request.Button, "Unsupported mouse button.")
            };

            var point = new Point(xScreen, yScreen);
            EnsureMouseClickWillNotActivateForeground(
                request.CoordSpace,
                new IntPtr(windowHandleUsed),
                point,
                policy);

            var foregroundBeforeInput = GetForegroundWindow();
            switch (request.ClickType)
            {
                case MouseClickType.Single:
                    Mouse.Click(point, mouseButton);
                    effects.MarkMouseInput(cursorMoved: true);
                    break;
                case MouseClickType.Double:
                    Mouse.DoubleClick(point, mouseButton);
                    effects.MarkMouseInput(cursorMoved: true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.ClickType), request.ClickType, "Unsupported click type.");
            }

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            var foregroundAfterInput = GetForegroundWindow();
            if (foregroundAfterInput != IntPtr.Zero && foregroundAfterInput != foregroundBeforeInput)
            {
                effects.MarkForegroundActivated();
            }

            trace?.SetSummary($"clicked=true x={xScreen} y={yScreen} space={coordSpaceUsed} button={request.Button} type={request.ClickType}");
            return new MouseClickResponse(
                Clicked: true,
                XScreen: xScreen,
                YScreen: yScreen,
                CoordSpaceUsed: coordSpaceUsed,
                MethodUsed: "mouse",
                Effects: effects.ToContract());
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

    private static void EnsureMouseClickWillNotActivateForeground(
        MouseCoordinateSpace coordinateSpace,
        IntPtr targetWindow,
        Point screenPoint,
        EffectiveInteractionPolicy policy)
    {
        var foreground = GetForegroundWindow();
        var candidate = WindowFromPoint(new NativeMousePoint(screenPoint.X, screenPoint.Y));
        var foregroundRoot = foreground == IntPtr.Zero ? IntPtr.Zero : GetRootOwnerWindow(foreground);
        var candidateRoot = candidate == IntPtr.Zero ? IntPtr.Zero : GetRootOwnerWindow(candidate);
        var targetRoot = targetWindow == IntPtr.Zero ? IntPtr.Zero : GetRootOwnerWindow(targetWindow);

        if (!policy.AllowForegroundActivation)
        {
            var requestedTargetIsSafe = coordinateSpace != MouseCoordinateSpace.Client ||
                                        (targetRoot != IntPtr.Zero && targetRoot == foregroundRoot);
            var actualTargetIsSafe = candidateRoot != IntPtr.Zero && candidateRoot == foregroundRoot;
            if (requestedTargetIsSafe && actualTargetIsSafe)
            {
                return;
            }

            throw InteractionPolicyResolver.Blocked(
                operation: "mouse_click",
                requiredEffect: "potential foreground activation from physical mouse input",
                policySetting: "allowForegroundActivation",
                alternative: "Use click_element for a semantic action, click within the current foreground window, or retry with interactionPolicy.allowForegroundActivation=true.");
        }

        if (coordinateSpace == MouseCoordinateSpace.Client &&
            (candidateRoot == IntPtr.Zero || candidateRoot != targetRoot))
        {
            throw new InvalidOperationException(
                "mouse_target_occluded: the requested client point is currently covered by another window. " +
                "Use click_element for a semantic action, uncover the target, or foreground it explicitly before retrying.");
        }
    }

    private static IntPtr GetRootOwnerWindow(IntPtr windowHandle)
    {
        var rootOwner = GetAncestor(windowHandle, GetAncestorRootOwner);
        return rootOwner == IntPtr.Zero ? windowHandle : rootOwner;
    }

    private const uint GetAncestorRootOwner = 3;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMousePoint
    {
        internal NativeMousePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        internal readonly int X;
        internal readonly int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMousePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);
}
