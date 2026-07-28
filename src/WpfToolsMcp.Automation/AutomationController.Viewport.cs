using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public async Task<SetWindowViewportResponse> SetWindowViewportAsync(
        SetWindowViewportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateViewportRequest(request);

        var trace = BeginTraceSpan("set_window_viewport");
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

            var originalOuterRect = RunInPerMonitorV2DpiContext(() =>
            {
                if (!GetWindowRect(hwnd, out var rect))
                {
                    throw new InvalidOperationException($"GetWindowRect failed: {Marshal.GetLastWin32Error()}");
                }

                return rect;
            });

            var originalState = GetNativeWindowState(hwnd);
            if (request.EnsureForeground)
            {
                await EnsureWindowForegroundAsync(
                    window,
                    operation: "set_window_viewport",
                    policy,
                    effects,
                    settleDelayMs: UiDelayWindowSettleMs,
                    cancellationToken).ConfigureAwait(false);
            }

            await RestoreWindowForViewportSizingAsync(
                window,
                hwnd,
                request.EnsureForeground,
                effects,
                cancellationToken).ConfigureAwait(false);

            var baseline = CaptureViewportConditions(hwnd);
            ViewportResizePlan? finalPlan = null;
            ViewportResizeApplicationResult? finalApplication = null;
            var totalResizeAttempts = 0;
            var correctionWasClamped = false;
            for (var pass = 0; pass < 3; pass++)
            {
                var candidatePlan = BuildViewportResizePlan(hwnd, request, baseline);
                var candidateApplication = await ApplyViewportResizePlanAsync(
                    hwnd,
                    candidatePlan,
                    request.EnsureForeground,
                    cancellationToken).ConfigureAwait(false);
                totalResizeAttempts += candidateApplication.ResizeAttempts;
                correctionWasClamped |= candidateApplication.CorrectionWasClamped;

                if (!RequiresViewportReplan(candidatePlan, candidateApplication.Actual))
                {
                    finalPlan = candidatePlan;
                    finalApplication = candidateApplication with
                    {
                        ResizeAttempts = totalResizeAttempts,
                        CorrectionWasClamped = correctionWasClamped
                    };
                    break;
                }

                baseline = candidateApplication.Actual;
            }

            var plan = finalPlan ?? throw new InvalidOperationException(
                "viewport_conditions_unstable: monitor or DPI conditions changed during 3 consecutive sizing passes.");
            var applied = finalApplication!;

            var constraints = plan.Constraints.ToList();
            var actualPixels = new PixelDimensions(
                applied.Actual.ClientBoundsPhysicalPixels.Width,
                applied.Actual.ClientBoundsPhysicalPixels.Height);
            var physicalDelta = new ViewportSize(
                actualPixels.Width - plan.RequestedPhysicalPixels.Width,
                actualPixels.Height - plan.RequestedPhysicalPixels.Height);
            var dipDelta = new ViewportSize(
                applied.Actual.ClientSizeWpfDips.Width - plan.RequestedDips.Width,
                applied.Actual.ClientSizeWpfDips.Height - plan.RequestedDips.Height);

            if (actualPixels != plan.AppliedPhysicalPixels)
            {
                AddConstraint(constraints, ViewportConstraint.ApplicationConstraint);
            }

            var wasClamped = plan.WasClamped || applied.CorrectionWasClamped;
            if (request.ClampToWorkArea)
            {
                var actualOuter = applied.Actual.OuterBoundsPhysicalPixels;
                var positionedOuter = ViewportGeometryCalculator.ClampOuterPosition(actualOuter, plan.WorkArea);
                var exceedsWorkArea = ViewportGeometryCalculator.OuterSizeExceedsWorkArea(actualOuter, plan.WorkArea);
                var remainsOutsideWorkArea = positionedOuter.X != actualOuter.X || positionedOuter.Y != actualOuter.Y;
                if (exceedsWorkArea || remainsOutsideWorkArea)
                {
                    wasClamped = true;
                    if (plan.MinimumExceedsWorkArea)
                    {
                        AddConstraint(constraints, ViewportConstraint.MinimumExceedsWorkArea);
                    }
                    else
                    {
                        AddConstraint(constraints, ViewportConstraint.ApplicationConstraint);
                    }
                }
            }

            if (wasClamped)
            {
                AddConstraint(constraints, ViewportConstraint.WorkAreaClamped);
            }

            var exactMatch = request.Unit switch
            {
                ViewportUnit.PhysicalPixels =>
                    actualPixels == plan.RequestedPhysicalPixels,
                ViewportUnit.WpfDips =>
                    ViewportGeometryCalculator.NearlyEqual(dipDelta.Width, 0) &&
                    ViewportGeometryCalculator.NearlyEqual(dipDelta.Height, 0),
                _ => false
            };

            var updated = originalState != WindowState.Normal ||
                          applied.Actual.OuterBoundsPhysicalPixels.X != originalOuterRect.Left ||
                          applied.Actual.OuterBoundsPhysicalPixels.Y != originalOuterRect.Top ||
                          applied.Actual.OuterBoundsPhysicalPixels.Width != originalOuterRect.Width ||
                          applied.Actual.OuterBoundsPhysicalPixels.Height != originalOuterRect.Height;

            var response = new SetWindowViewportResponse(
                Updated: updated,
                WindowHandleUsed: hwnd.ToInt64(),
                Requested: plan.Requested,
                Actual: applied.Actual,
                Adjustment: new ViewportAdjustment(
                    AppliedClientSizePhysicalPixels: ToViewportSize(plan.AppliedPhysicalPixels),
                    ClientSizeDeltaPhysicalPixels: physicalDelta,
                    ClientSizeDeltaWpfDips: dipDelta,
                    ExactMatch: exactMatch,
                    WasClamped: wasClamped,
                    MinimumSizeConstrained: plan.MinimumSizeConstrained,
                    ResizeAttempts: applied.ResizeAttempts,
                    Constraints: constraints),
                Effects: effects.ToContract());

            trace?.SetSummary(
                $"{response.WindowHandleUsed} requested={request.ClientWidth}x{request.ClientHeight} {request.Unit} " +
                $"actual={actualPixels.Width}x{actualPixels.Height}px monitorDpi={response.Actual.Dpi.MonitorDpiX} " +
                $"windowDpi={response.Actual.Dpi.WindowDpiX} " +
                $"exact={response.Adjustment.ExactMatch} attempts={response.Adjustment.ResizeAttempts}");
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

    private static void ValidateViewportRequest(SetWindowViewportRequest request)
    {
        if (!Enum.IsDefined(request.Unit))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Unit), request.Unit, "Unsupported viewport unit.");
        }

        if (!double.IsFinite(request.ClientWidth) || request.ClientWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ClientWidth), request.ClientWidth, "Client width must be finite and greater than zero.");
        }

        if (!double.IsFinite(request.ClientHeight) || request.ClientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ClientHeight), request.ClientHeight, "Client height must be finite and greater than zero.");
        }

        if (request.Unit == ViewportUnit.PhysicalPixels &&
            (request.ClientWidth != Math.Truncate(request.ClientWidth) ||
             request.ClientHeight != Math.Truncate(request.ClientHeight)))
        {
            throw new ArgumentException("Physical-pixel viewport dimensions must be whole numbers.");
        }

        if (request.ClientWidth > int.MaxValue || request.ClientHeight > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Viewport dimensions exceed the supported range.");
        }
    }

    private static async Task RestoreWindowForViewportSizingAsync(
        Window window,
        IntPtr hwnd,
        bool ensureForeground,
        InteractionEffectTracker effects,
        CancellationToken cancellationToken)
    {
        if (GetNativeWindowState(hwnd) == WindowState.Normal)
        {
            return;
        }

        var restoredWithWindowPattern = false;
        if (ensureForeground)
        {
            var windowPattern = window.Patterns.Window.PatternOrDefault;
            if (windowPattern is not null)
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
        }

        if (!restoredWithWindowPattern)
        {
            _ = ShowWindow(hwnd, ensureForeground ? SW_RESTORE : SW_SHOWNOACTIVATE);
        }

        await Task.Delay(Math.Max(UiDelayWindowSettleMs, 100), cancellationToken).ConfigureAwait(false);
        if (GetNativeWindowState(hwnd) != WindowState.Normal)
        {
            throw new InvalidOperationException(
                $"window_state_change_failed: set_window_viewport could not restore window {hwnd.ToInt64()} to normal state.");
        }

        effects.MarkWindowRestored();
    }

    private static ViewportResizePlan BuildViewportResizePlan(
        IntPtr hwnd,
        SetWindowViewportRequest request,
        ViewportConditions baseline) =>
        RunInPerMonitorV2DpiContext(() => BuildViewportResizePlanInDpiContext(hwnd, request, baseline));

    private static ViewportResizePlan BuildViewportResizePlanInDpiContext(
        IntPtr hwnd,
        SetWindowViewportRequest request,
        ViewportConditions baseline)
    {
        var windowDpiX = baseline.Dpi.WindowDpiX;
        var windowDpiY = baseline.Dpi.WindowDpiY;
        var monitorDpiX = baseline.Dpi.MonitorDpiX;
        var requestedPhysical = request.Unit switch
        {
            ViewportUnit.PhysicalPixels => new PixelDimensions(
                checked((int)request.ClientWidth),
                checked((int)request.ClientHeight)),
            ViewportUnit.WpfDips => ConvertTargetLogicalExtentToPhysical(
                hwnd,
                new PixelDimensions(
                    ViewportGeometryCalculator.DipsToPhysicalPixels(request.ClientWidth, windowDpiX),
                    ViewportGeometryCalculator.DipsToPhysicalPixels(request.ClientHeight, windowDpiY)),
                baseline.ClientBoundsPhysicalPixels,
                windowDpiX,
                windowDpiY,
                baseline.Dpi.MonitorDpiX,
                baseline.Dpi.MonitorDpiY),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Unit), request.Unit, "Unsupported viewport unit.")
        };
        var requestedDips = request.Unit == ViewportUnit.WpfDips
            ? new ViewportSize(request.ClientWidth, request.ClientHeight)
            : ConvertPhysicalClientExtentToWpfDips(
                hwnd,
                baseline.ClientBoundsPhysicalPixels with
                {
                    Width = requestedPhysical.Width,
                    Height = requestedPhysical.Height
                },
                windowDpiX,
                windowDpiY,
                baseline.Dpi.MonitorDpiX,
                baseline.Dpi.MonitorDpiY);
        var roundTrippedRequestedDips = ConvertPhysicalClientExtentToWpfDips(
            hwnd,
            baseline.ClientBoundsPhysicalPixels with
            {
                Width = requestedPhysical.Width,
                Height = requestedPhysical.Height
            },
            windowDpiX,
            windowDpiY,
            baseline.Dpi.MonitorDpiX,
            baseline.Dpi.MonitorDpiY);

        var frame = GetAdjustedFrameInsets(hwnd, requestedPhysical, monitorDpiX);
        var minimumOuter = GetMinimumOuterSize(
            hwnd,
            baseline.OuterBoundsPhysicalPixels,
            windowDpiX,
            baseline.Dpi.MonitorDpiX);
        var minimumClient = new PixelDimensions(
            Math.Max(1, minimumOuter.Width - frame.Left - frame.Right),
            Math.Max(1, minimumOuter.Height - frame.Top - frame.Bottom));
        var appliedPhysical = new PixelDimensions(
            Math.Max(requestedPhysical.Width, minimumClient.Width),
            Math.Max(requestedPhysical.Height, minimumClient.Height));
        var minimumSizeConstrained = appliedPhysical != requestedPhysical;
        var constraints = new List<ViewportConstraint>();
        if (minimumSizeConstrained)
        {
            constraints.Add(ViewportConstraint.MinimumSize);
        }

        if (request.Unit == ViewportUnit.WpfDips &&
            (!ViewportGeometryCalculator.NearlyEqual(
                 roundTrippedRequestedDips.Width,
                 request.ClientWidth) ||
             !ViewportGeometryCalculator.NearlyEqual(
                 roundTrippedRequestedDips.Height,
                 request.ClientHeight)))
        {
            constraints.Add(ViewportConstraint.DpiRounding);
        }

        var wasClamped = false;
        var minimumExceedsWorkArea = false;
        if (request.ClampToWorkArea)
        {
            var maximumClient = new PixelDimensions(
                Math.Max(1, baseline.Monitor.WorkAreaPhysicalPixels.Width - frame.Left - frame.Right),
                Math.Max(1, baseline.Monitor.WorkAreaPhysicalPixels.Height - frame.Top - frame.Bottom));
            minimumExceedsWorkArea = minimumClient.Width > maximumClient.Width ||
                                     minimumClient.Height > maximumClient.Height;
            if (minimumExceedsWorkArea)
            {
                AddConstraint(constraints, ViewportConstraint.MinimumExceedsWorkArea);
            }

            var clamped = ViewportGeometryCalculator.ClampClientSizeToWorkArea(
                appliedPhysical,
                frame,
                baseline.Monitor.WorkAreaPhysicalPixels,
                out var sizeWasClamped);
            if (minimumExceedsWorkArea)
            {
                clamped = new PixelDimensions(
                    minimumClient.Width > maximumClient.Width ? minimumClient.Width : clamped.Width,
                    minimumClient.Height > maximumClient.Height ? minimumClient.Height : clamped.Height);
            }

            appliedPhysical = clamped;
            wasClamped = sizeWasClamped;
        }

        var outerSize = ViewportGeometryCalculator.ExpandClientToOuter(
            appliedPhysical.Width,
            appliedPhysical.Height,
            frame);
        var outerBounds = new Rect(
            baseline.OuterBoundsPhysicalPixels.X,
            baseline.OuterBoundsPhysicalPixels.Y,
            checked((int)outerSize.Width),
            checked((int)outerSize.Height));
        if (request.ClampToWorkArea)
        {
            var positioned = ViewportGeometryCalculator.ClampOuterPosition(
                outerBounds,
                baseline.Monitor.WorkAreaPhysicalPixels);
            wasClamped |= positioned.X != outerBounds.X || positioned.Y != outerBounds.Y;
            outerBounds = positioned;
        }

        if (wasClamped)
        {
            AddConstraint(constraints, ViewportConstraint.WorkAreaClamped);
        }

        var requested = new ViewportRequest(
            Unit: request.Unit,
            ClientSize: new ViewportSize(request.ClientWidth, request.ClientHeight),
            ClientBoundsPhysicalPixels: new Rect(
                baseline.ClientBoundsPhysicalPixels.X,
                baseline.ClientBoundsPhysicalPixels.Y,
                requestedPhysical.Width,
                requestedPhysical.Height),
            ClientSizePhysicalPixels: ToViewportSize(requestedPhysical),
            ClientSizeWpfDips: requestedDips);

        return new ViewportResizePlan(
            Requested: requested,
            RequestedPhysicalPixels: requestedPhysical,
            RequestedDips: requestedDips,
            AppliedPhysicalPixels: appliedPhysical,
            OuterBounds: outerBounds,
            MonitorBounds: baseline.Monitor.BoundsPhysicalPixels,
            WorkArea: baseline.Monitor.WorkAreaPhysicalPixels,
            MonitorDeviceName: baseline.Monitor.DeviceName,
            WindowDpiX: windowDpiX,
            WindowDpiY: windowDpiY,
            MonitorDpiX: baseline.Dpi.MonitorDpiX,
            MonitorDpiY: baseline.Dpi.MonitorDpiY,
            ClampToWorkArea: request.ClampToWorkArea,
            WasClamped: wasClamped,
            MinimumSizeConstrained: minimumSizeConstrained,
            MinimumExceedsWorkArea: minimumExceedsWorkArea,
            Constraints: constraints);
    }

    private static async Task<ViewportResizeApplicationResult> ApplyViewportResizePlanAsync(
        IntPtr hwnd,
        ViewportResizePlan plan,
        bool ensureForeground,
        CancellationToken cancellationToken)
    {
        var nextOuter = plan.OuterBounds;
        var attempts = 0;
        var correctionWasClamped = false;
        ViewportConditions actual = CaptureViewportConditions(hwnd);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            SetViewportWindowPosition(hwnd, nextOuter, ensureForeground);

            attempts++;
            actual = await WaitForStableViewportAsync(hwnd, cancellationToken).ConfigureAwait(false);
            var actualPixels = new PixelDimensions(
                actual.ClientBoundsPhysicalPixels.Width,
                actual.ClientBoundsPhysicalPixels.Height);
            var corrected = actual.OuterBoundsPhysicalPixels;
            if (actualPixels != plan.AppliedPhysicalPixels)
            {
                var correctedSize = ViewportGeometryCalculator.CorrectOuterSize(
                    actual.OuterBoundsPhysicalPixels.Width,
                    actual.OuterBoundsPhysicalPixels.Height,
                    plan.AppliedPhysicalPixels.Width,
                    plan.AppliedPhysicalPixels.Height,
                    actualPixels.Width,
                    actualPixels.Height);
                corrected = corrected with
                {
                    Width = checked((int)correctedSize.Width),
                    Height = checked((int)correctedSize.Height)
                };
            }

            if (plan.ClampToWorkArea)
            {
                if (!plan.MinimumExceedsWorkArea)
                {
                    var width = Math.Min(corrected.Width, plan.WorkArea.Width);
                    var height = Math.Min(corrected.Height, plan.WorkArea.Height);
                    correctionWasClamped |= width != corrected.Width || height != corrected.Height;
                    corrected = corrected with { Width = width, Height = height };
                }

                var positioned = ViewportGeometryCalculator.ClampOuterPosition(corrected, plan.WorkArea);
                correctionWasClamped |= positioned.X != corrected.X || positioned.Y != corrected.Y;
                corrected = positioned;
            }

            if (corrected == actual.OuterBoundsPhysicalPixels ||
                (corrected == nextOuter && actualPixels != plan.AppliedPhysicalPixels))
            {
                break;
            }

            nextOuter = corrected;
        }

        if (plan.ClampToWorkArea)
        {
            var positionedActual = ViewportGeometryCalculator.ClampOuterPosition(
                actual.OuterBoundsPhysicalPixels,
                plan.WorkArea);
            if (positionedActual.X != actual.OuterBoundsPhysicalPixels.X ||
                positionedActual.Y != actual.OuterBoundsPhysicalPixels.Y)
            {
                correctionWasClamped = true;
                SetViewportWindowPosition(hwnd, positionedActual, ensureForeground);
                attempts++;
                actual = await WaitForStableViewportAsync(hwnd, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ViewportResizeApplicationResult(actual, attempts, correctionWasClamped);
    }

    private static void SetViewportWindowPosition(IntPtr hwnd, Rect outerBounds, bool ensureForeground) =>
        RunInPerMonitorV2DpiContext(() =>
        {
            var flags = SWP_NOZORDER | (ensureForeground ? 0u : SWP_NOACTIVATE);
            if (!SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    outerBounds.X,
                    outerBounds.Y,
                    outerBounds.Width,
                    outerBounds.Height,
                    flags))
            {
                throw new InvalidOperationException($"SetWindowPos failed: {Marshal.GetLastWin32Error()}");
            }
        });

    private static async Task<ViewportConditions> WaitForStableViewportAsync(
        IntPtr hwnd,
        CancellationToken cancellationToken)
    {
        ViewportConditions? previous = null;
        var delayMs = Math.Max(UiDelayWindowSettleMs, 25);
        for (var sample = 0; sample < 12; sample++)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            var current = CaptureViewportConditions(hwnd);
            if (previous is not null && HasStableViewportGeometry(previous, current))
            {
                return current;
            }

            previous = current;
        }

        return previous ?? CaptureViewportConditions(hwnd);
    }

    private static bool HasStableViewportGeometry(ViewportConditions left, ViewportConditions right) =>
        left.ClientBoundsPhysicalPixels == right.ClientBoundsPhysicalPixels &&
        left.OuterBoundsPhysicalPixels == right.OuterBoundsPhysicalPixels &&
        left.Dpi == right.Dpi &&
        left.Monitor == right.Monitor &&
        left.WindowState == right.WindowState;

    private static ViewportConditions CaptureViewportConditions(IntPtr hwnd) =>
        RunInPerMonitorV2DpiContext(() =>
        {
            if (!GetWindowRect(hwnd, out var outerRect) || outerRect.Width <= 0 || outerRect.Height <= 0)
            {
                throw new InvalidOperationException($"GetWindowRect failed: {Marshal.GetLastWin32Error()}");
            }

            if (!GetClientRect(hwnd, out var clientRect) || clientRect.Width < 0 || clientRect.Height < 0)
            {
                throw new InvalidOperationException($"GetClientRect failed: {Marshal.GetLastWin32Error()}");
            }

            var clientTopLeft = new POINT(0, 0);
            var clientBottomRight = new POINT(clientRect.Width, clientRect.Height);
            if (!ClientToScreen(hwnd, ref clientTopLeft) || !ClientToScreen(hwnd, ref clientBottomRight))
            {
                throw new InvalidOperationException($"ClientToScreen failed: {Marshal.GetLastWin32Error()}");
            }

            var clientBounds = new Rect(
                clientTopLeft.X,
                clientTopLeft.Y,
                Math.Max(0, clientBottomRight.X - clientTopLeft.X),
                Math.Max(0, clientBottomRight.Y - clientTopLeft.Y));
            var outerBounds = new Rect(outerRect.Left, outerRect.Top, outerRect.Width, outerRect.Height);
            var windowDpi = GetDpiForWindow(hwnd);
            if (windowDpi == 0)
            {
                throw new InvalidOperationException($"GetDpiForWindow failed: {Marshal.GetLastWin32Error()}");
            }

            var monitorHandle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitorHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"MonitorFromWindow failed: {Marshal.GetLastWin32Error()}");
            }

            var monitorInfo = new VIEWPORT_MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<VIEWPORT_MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (!GetMonitorInfoForViewport(monitorHandle, ref monitorInfo))
            {
                throw new InvalidOperationException($"GetMonitorInfo failed: {Marshal.GetLastWin32Error()}");
            }

            var monitorDpi = GetMonitorDpiForViewport(monitorInfo.rcMonitor);

            var frame = new ViewportFrameInsets(
                Left: clientBounds.X - outerBounds.X,
                Top: clientBounds.Y - outerBounds.Y,
                Right: outerBounds.X + outerBounds.Width - clientBounds.X - clientBounds.Width,
                Bottom: outerBounds.Y + outerBounds.Height - clientBounds.Y - clientBounds.Height);
            var awareness = GetViewportDpiAwareness(hwnd);
            var clientSizeWpfDips = ConvertPhysicalClientExtentToWpfDips(
                hwnd,
                clientBounds,
                windowDpi,
                windowDpi,
                monitorDpi,
                monitorDpi);

            return new ViewportConditions(
                ClientBoundsPhysicalPixels: clientBounds,
                OuterBoundsPhysicalPixels: outerBounds,
                ClientSizePhysicalPixels: new ViewportSize(clientBounds.Width, clientBounds.Height),
                ClientSizeWpfDips: clientSizeWpfDips,
                FramePhysicalPixels: frame,
                Dpi: new ViewportDpi(
                    WindowDpiX: windowDpi,
                    WindowDpiY: windowDpi,
                    WindowScaleX: windowDpi / 96d,
                    WindowScaleY: windowDpi / 96d,
                    MonitorDpiX: monitorDpi,
                    MonitorDpiY: monitorDpi,
                    MonitorScaleX: monitorDpi / 96d,
                    MonitorScaleY: monitorDpi / 96d,
                    Awareness: awareness),
                Monitor: new ViewportMonitor(
                    DeviceName: monitorInfo.szDevice?.TrimEnd('\0') ?? string.Empty,
                    BoundsPhysicalPixels: ToContractRect(monitorInfo.rcMonitor),
                    WorkAreaPhysicalPixels: ToContractRect(monitorInfo.rcWork),
                    IsPrimary: (monitorInfo.dwFlags & MONITORINFOF_PRIMARY) != 0),
                WindowState: GetNativeWindowState(hwnd));
        });

    private static ViewportFrameInsets GetAdjustedFrameInsets(
        IntPtr hwnd,
        PixelDimensions clientSize,
        uint dpi)
    {
        var style = unchecked((uint)GetWindowLongPtrCompat(hwnd, GWL_STYLE).ToInt64());
        var extendedStyle = unchecked((uint)GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE).ToInt64());
        var adjusted = new VIEWPORT_RECT
        {
            Left = 0,
            Top = 0,
            Right = clientSize.Width,
            Bottom = clientSize.Height
        };
        if (!AdjustWindowRectExForDpi(
                ref adjusted,
                style,
                GetMenu(hwnd) != IntPtr.Zero,
                extendedStyle,
                dpi))
        {
            throw new InvalidOperationException($"AdjustWindowRectExForDpi failed: {Marshal.GetLastWin32Error()}");
        }

        return new ViewportFrameInsets(
            Left: -adjusted.Left,
            Top: -adjusted.Top,
            Right: adjusted.Right - clientSize.Width,
            Bottom: adjusted.Bottom - clientSize.Height);
    }

    private static PixelDimensions GetMinimumOuterSize(
        IntPtr hwnd,
        Rect physicalAnchor,
        uint windowDpi,
        uint monitorDpi)
    {
        var systemMinimum = new PixelDimensions(
            GetSystemMetricForDpi(SM_CXMINTRACK, windowDpi),
            GetSystemMetricForDpi(SM_CYMINTRACK, windowDpi));
        var systemMaximum = new PixelDimensions(
            GetSystemMetricForDpi(SM_CXMAXTRACK, windowDpi),
            GetSystemMetricForDpi(SM_CYMAXTRACK, windowDpi));
        var minimumLogical = systemMinimum;
        var minMaxInfo = new VIEWPORT_MINMAXINFO
        {
            MaxSize = new POINT(systemMaximum.Width, systemMaximum.Height),
            MinTrackSize = new POINT(systemMinimum.Width, systemMinimum.Height),
            MaxTrackSize = new POINT(systemMaximum.Width, systemMaximum.Height)
        };

        Marshal.SetLastPInvokeError(0);
        var sent = SendMessageTimeoutForViewport(
            hwnd,
            WM_GETMINMAXINFO,
            UIntPtr.Zero,
            ref minMaxInfo,
            SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT,
            250,
            out _);
        if (sent != IntPtr.Zero &&
            minMaxInfo.MinTrackSize.X > 0 &&
            minMaxInfo.MinTrackSize.Y > 0)
        {
            minimumLogical = new PixelDimensions(
                Math.Max(systemMinimum.Width, minMaxInfo.MinTrackSize.X),
                Math.Max(systemMinimum.Height, minMaxInfo.MinTrackSize.Y));
        }

        return ConvertTargetLogicalExtentToPhysical(
            hwnd,
            minimumLogical,
            physicalAnchor,
            windowDpi,
            windowDpi,
            monitorDpi,
            monitorDpi);
    }

    private static PixelDimensions ConvertTargetLogicalExtentToPhysical(
        IntPtr hwnd,
        PixelDimensions logicalExtent,
        Rect physicalAnchor,
        uint windowDpiX,
        uint windowDpiY,
        uint monitorDpiX,
        uint monitorDpiY)
    {
        var logicalStart = new POINT(physicalAnchor.X, physicalAnchor.Y);
        if (!PhysicalToLogicalPointForPerMonitorDpi(hwnd, ref logicalStart))
        {
            return new PixelDimensions(
                ViewportGeometryCalculator.ScalePixelsBetweenDpi(logicalExtent.Width, windowDpiX, monitorDpiX),
                ViewportGeometryCalculator.ScalePixelsBetweenDpi(logicalExtent.Height, windowDpiY, monitorDpiY));
        }

        var logicalEnd = new POINT(
            checked(logicalStart.X + logicalExtent.Width),
            checked(logicalStart.Y + logicalExtent.Height));
        var physicalStart = logicalStart;
        var physicalEnd = logicalEnd;
        if (!LogicalToPhysicalPointForPerMonitorDpi(hwnd, ref physicalStart) ||
            !LogicalToPhysicalPointForPerMonitorDpi(hwnd, ref physicalEnd))
        {
            return new PixelDimensions(
                ViewportGeometryCalculator.ScalePixelsBetweenDpi(logicalExtent.Width, windowDpiX, monitorDpiX),
                ViewportGeometryCalculator.ScalePixelsBetweenDpi(logicalExtent.Height, windowDpiY, monitorDpiY));
        }

        return new PixelDimensions(
            Math.Max(1, Math.Abs(physicalEnd.X - physicalStart.X)),
            Math.Max(1, Math.Abs(physicalEnd.Y - physicalStart.Y)));
    }

    private static ViewportSize ConvertPhysicalClientExtentToWpfDips(
        IntPtr hwnd,
        Rect physicalBounds,
        uint windowDpiX,
        uint windowDpiY,
        uint monitorDpiX,
        uint monitorDpiY)
    {
        var logicalStart = new POINT(physicalBounds.X, physicalBounds.Y);
        var logicalEnd = new POINT(
            checked(physicalBounds.X + physicalBounds.Width),
            checked(physicalBounds.Y + physicalBounds.Height));
        if (!PhysicalToLogicalPointForPerMonitorDpi(hwnd, ref logicalStart) ||
            !PhysicalToLogicalPointForPerMonitorDpi(hwnd, ref logicalEnd))
        {
            var fallbackLogicalWidth = ViewportGeometryCalculator.ScalePixelsBetweenDpi(
                Math.Max(1, physicalBounds.Width),
                monitorDpiX,
                windowDpiX);
            var fallbackLogicalHeight = ViewportGeometryCalculator.ScalePixelsBetweenDpi(
                Math.Max(1, physicalBounds.Height),
                monitorDpiY,
                windowDpiY);
            return new ViewportSize(
                ViewportGeometryCalculator.PhysicalPixelsToDips(fallbackLogicalWidth, windowDpiX),
                ViewportGeometryCalculator.PhysicalPixelsToDips(fallbackLogicalHeight, windowDpiY));
        }

        var logicalWidth = Math.Abs(logicalEnd.X - logicalStart.X);
        var logicalHeight = Math.Abs(logicalEnd.Y - logicalStart.Y);
        return new ViewportSize(
            ViewportGeometryCalculator.PhysicalPixelsToDips(logicalWidth, windowDpiX),
            ViewportGeometryCalculator.PhysicalPixelsToDips(logicalHeight, windowDpiY));
    }

    private static uint GetMonitorDpiForViewport(VIEWPORT_RECT monitorBounds)
    {
        var x = monitorBounds.Left + ((monitorBounds.Right - monitorBounds.Left) / 2);
        var y = monitorBounds.Top + ((monitorBounds.Bottom - monitorBounds.Top) / 2);
        var probe = CreateWindowExForViewport(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "STATIC",
            string.Empty,
            WS_POPUP,
            x,
            y,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (probe == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"monitor_dpi_unavailable: CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var dpi = GetDpiForWindow(probe);
            if (dpi == 0)
            {
                throw new InvalidOperationException(
                    $"monitor_dpi_unavailable: GetDpiForWindow failed: {Marshal.GetLastWin32Error()}");
            }

            return dpi;
        }
        finally
        {
            _ = DestroyWindowForViewport(probe);
        }
    }

    private static int GetSystemMetricForDpi(int index, uint dpi)
    {
        try
        {
            return Math.Max(1, GetSystemMetricsForDpi(index, dpi));
        }
        catch (EntryPointNotFoundException)
        {
            return Math.Max(1, GetSystemMetricsForViewport(index));
        }
    }

    private static IntPtr GetWindowLongPtrCompat(IntPtr hwnd, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));

    private static DpiAwareness GetViewportDpiAwareness(IntPtr hwnd)
    {
        try
        {
            return GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)) switch
            {
                0 => DpiAwareness.Unaware,
                1 => DpiAwareness.SystemAware,
                2 => DpiAwareness.PerMonitorAware,
                _ => DpiAwareness.Unknown
            };
        }
        catch (EntryPointNotFoundException)
        {
            return DpiAwareness.Unknown;
        }
    }

    private static bool RequiresViewportReplan(ViewportResizePlan plan, ViewportConditions actual) =>
        !string.Equals(plan.MonitorDeviceName, actual.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase) ||
        plan.MonitorBounds != actual.Monitor.BoundsPhysicalPixels ||
        plan.WorkArea != actual.Monitor.WorkAreaPhysicalPixels ||
        plan.WindowDpiX != actual.Dpi.WindowDpiX ||
        plan.WindowDpiY != actual.Dpi.WindowDpiY ||
        plan.MonitorDpiX != actual.Dpi.MonitorDpiX ||
        plan.MonitorDpiY != actual.Dpi.MonitorDpiY;

    private static void RunInPerMonitorV2DpiContext(Action action) =>
        RunInPerMonitorV2DpiContext(() =>
        {
            action();
            return true;
        });

    private static T RunInPerMonitorV2DpiContext<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var previousContext = EnterPerMonitorV2DpiContext();
        try
        {
            return action();
        }
        finally
        {
            RestoreDpiContext(previousContext);
        }
    }

    private static IntPtr EnterPerMonitorV2DpiContext()
    {
        try
        {
            var previousContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previousContext == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"dpi_context_unavailable: SetThreadDpiAwarenessContext failed: {Marshal.GetLastWin32Error()}");
            }

            return previousContext;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "dpi_context_unavailable: per-monitor-v2 thread DPI awareness is unavailable on this Windows version.",
                ex);
        }
    }

    private static void RestoreDpiContext(IntPtr previousContext)
    {
        try
        {
            if (SetThreadDpiAwarenessContext(previousContext) == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"dpi_context_unavailable: failed to restore the previous thread DPI context: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "dpi_context_unavailable: the previous thread DPI context could not be restored.",
                ex);
        }
    }

    private static Rect ToContractRect(VIEWPORT_RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static ViewportSize ToViewportSize(PixelDimensions size) =>
        new(size.Width, size.Height);

    private static void AddConstraint(List<ViewportConstraint> constraints, ViewportConstraint constraint)
    {
        if (!constraints.Contains(constraint))
        {
            constraints.Add(constraint);
        }
    }

    private sealed record ViewportResizePlan(
        ViewportRequest Requested,
        PixelDimensions RequestedPhysicalPixels,
        ViewportSize RequestedDips,
        PixelDimensions AppliedPhysicalPixels,
        Rect OuterBounds,
        Rect MonitorBounds,
        Rect WorkArea,
        string MonitorDeviceName,
        uint WindowDpiX,
        uint WindowDpiY,
        uint MonitorDpiX,
        uint MonitorDpiY,
        bool ClampToWorkArea,
        bool WasClamped,
        bool MinimumSizeConstrained,
        bool MinimumExceedsWorkArea,
        IReadOnlyList<ViewportConstraint> Constraints);

    private sealed record ViewportResizeApplicationResult(
        ViewportConditions Actual,
        int ResizeAttempts,
        bool CorrectionWasClamped);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int SM_CXMINTRACK = 34;
    private const int SM_CYMINTRACK = 35;
    private const int SM_CXMAXTRACK = 59;
    private const int SM_CYMAXTRACK = 60;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_ERRORONEXIT = 0x0020;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "LogicalToPhysicalPointForPerMonitorDPI", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDpi(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", EntryPoint = "PhysicalToLogicalPointForPerMonitorDPI", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PhysicalToLogicalPointForPerMonitorDpi(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutForViewport(
        IntPtr hwnd,
        uint message,
        UIntPtr wParam,
        ref VIEWPORT_MINMAXINFO lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExForViewport(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindowForViewport(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectExForDpi(
        ref VIEWPORT_RECT rect,
        uint style,
        [MarshalAs(UnmanagedType.Bool)] bool hasMenu,
        uint extendedStyle,
        uint dpi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoForViewport(IntPtr monitor, ref VIEWPORT_MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetricsForViewport(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct VIEWPORT_RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VIEWPORT_MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct VIEWPORT_MONITORINFOEX
    {
        public int cbSize;
        public VIEWPORT_RECT rcMonitor;
        public VIEWPORT_RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

}
