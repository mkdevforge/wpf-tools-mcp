using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

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
                        Error: "window_not_found");
                }

                if (request.EnsureForeground)
                {
                    await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);
                }
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
                            Error: "client_origin_unavailable");
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
            switch (request.ClickType)
            {
                case MouseClickType.Single:
                    Mouse.Click(point, mouseButton);
                    break;
                case MouseClickType.Double:
                    Mouse.DoubleClick(point, mouseButton);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.ClickType), request.ClickType, "Unsupported click type.");
            }

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            trace?.SetSummary($"clicked=true x={xScreen} y={yScreen} space={coordSpaceUsed} button={request.Button} type={request.ClickType}");
            return new MouseClickResponse(
                Clicked: true,
                XScreen: xScreen,
                YScreen: yScreen,
                CoordSpaceUsed: coordSpaceUsed);
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
}

