using FlaUI.Core.AutomationElements;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    public async Task<HighlightElementResponse> HighlightElementAsync(
        HighlightElementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("highlight_element");
        try
        {
            var hasLocator = request.Locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            if (hasLocator == hasElementId)
            {
                throw new ArgumentException("highlight_element requires exactly one of: locator OR elementId.");
            }

            var application = EnsureAttached();
            var automation = EnsureAutomation();

            string? resolvedElementId = null;
            var backend = request.Backend;
            ElementLocator effectiveLocator = request.Locator ?? new ElementLocator();

            long windowHandleUsed;
            if (hasElementId)
            {
                resolvedElementId = request.ElementId!.Trim();
                var handle = RequireHandle(resolvedElementId);
                backend = handle.Backend;
                windowHandleUsed = handle.WindowHandle;

                if (request.WindowHandle is long requestedHandle && requestedHandle != windowHandleUsed)
                {
                    throw new ArgumentException("windowHandle does not match the elementId window.");
                }

                effectiveLocator = new ElementLocator(XPath: handle.XPath);
            }
            else
            {
                if (backend == InspectionBackend.Auto)
                {
                    backend = IsAgentConnected ? InspectionBackend.Wpf : InspectionBackend.Uia;
                }

                windowHandleUsed = request.WindowHandle
                    ?? FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
            }

            Rect bounds;
            ElementRef? wpfResolved = null;
            try
            {
                bounds = backend switch
                {
                    InspectionBackend.Uia => ResolveHighlightBoundsUia(application, automation, windowHandleUsed, effectiveLocator, resolvedElementId),
                    InspectionBackend.Wpf => (wpfResolved = await ResolveHighlightElementWpfAsync(windowHandleUsed, effectiveLocator, cancellationToken).ConfigureAwait(false)).Bounds
                        ?? new Rect(0, 0, 0, 0),
                    _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unsupported backend.")
                };
            }
            catch (InvalidOperationException ex) when (hasElementId &&
                                                      resolvedElementId is not null &&
                                                      backend == InspectionBackend.Wpf &&
                                                      ex.Message.StartsWith("wpf_resolve:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"stale_element: not_found for '{resolvedElementId}'. Call resolve_element again.");
            }

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                trace?.SetSummary($"{backend} highlighted=false reason=no_bounds");
                return new HighlightElementResponse(Highlighted: false, Bounds: bounds, Reason: "no_bounds");
            }

            var shown = false;
            string? methodUsed = null;
            string? error = null;

            if (backend == InspectionBackend.Wpf)
            {
                var client = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
                if (client is not null)
                {
                    var locator = wpfResolved is not null
                        ? new ElementLocator(XPath: wpfResolved.XPath)
                        : effectiveLocator;

                    var agentResult = await client.CallAsync<HighlightWpfElementResponse>(
                        "wpf/highlight_element",
                        new HighlightWpfElementRequest(
                            WindowHandle: windowHandleUsed,
                            Locator: locator,
                            RootXPath: null,
                            DurationMs: request.DurationMs,
                            Color: request.Color,
                            Thickness: request.Thickness),
                        cancellationToken).ConfigureAwait(false);

                    shown = agentResult.Highlighted;
                    if (shown)
                    {
                        methodUsed = "wpf_agent";
                    }
                }
            }

            if (!shown)
            {
                var overlayResult = await HighlightOverlay.ShowAsync(
                    bounds,
                    request.Color,
                    request.Thickness,
                    request.DurationMs,
                    cancellationToken).ConfigureAwait(false);

                shown = overlayResult.Shown;
                methodUsed = "win32_overlay";
                error = overlayResult.Error;
            }

            TakeScreenshotResponse? screenshot = null;
            if (request.ReturnScreenshot)
            {
                try
                {
                    Window window;
                    try
                    {
                        window = FindWindowByHandle(application, automation, windowHandleUsed);
                    }
                    catch
                    {
                        throw new InvalidOperationException("Window not found for highlight screenshot.");
                    }

                    await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

                    var capture = CaptureScreenshotWithMetadata(
                        window,
                        requestedBounds: null,
                        requestedMode: request.ScreenshotCaptureMode,
                        area: request.ScreenshotArea,
                        clip: ScreenshotClipMode.Intersect,
                        includeOverlay: true);

                    using var bitmap = capture.Bitmap;

                    AnnotateBitmap(
                        bitmap,
                        capture.CapturedBounds,
                        bounds,
                        request.Color,
                        request.Thickness,
                        label: null);

                    var screenshotPath = ResolveScreenshotOutputPath(request.ScreenshotOutputPath, request.ScreenshotFormat);
                    SaveBitmapWithWic(bitmap, screenshotPath, request.ScreenshotFormat, request.ScreenshotJpegQuality);

                    string? base64 = null;
                    if (request.ScreenshotReturnBase64)
                    {
                        var bytes = await File.ReadAllBytesAsync(screenshotPath, cancellationToken);
                        base64 = Convert.ToBase64String(bytes);
                    }

                    screenshot = new TakeScreenshotResponse(
                        Path: screenshotPath,
                        Width: bitmap.Width,
                        Height: bitmap.Height,
                        Format: GetImageFormatName(request.ScreenshotFormat),
                        CapturedBounds: capture.CapturedBounds,
                        RequestedBounds: bounds,
                        WasClipped: capture.WasClipped,
                        WindowHandleUsed: windowHandleUsed,
                        CaptureModeUsed: capture.CaptureModeUsed,
                        Base64: base64);
                }
                catch (Exception screenshotEx)
                {
                    error = (error is null ? "" : (error + "\n")) + "highlight_screenshot_failed: " + screenshotEx.GetBaseException().Message;
                }
            }

            var response = shown
                ? new HighlightElementResponse(Highlighted: true, Bounds: bounds, MethodUsed: methodUsed, Error: error, Screenshot: screenshot)
                : new HighlightElementResponse(Highlighted: false, Bounds: bounds, Reason: "overlay_failed", MethodUsed: methodUsed, Error: error, Screenshot: screenshot);

            trace?.SetSummary($"{backend} highlighted={response.Highlighted} bounds={bounds.Width}x{bounds.Height}");
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

    private Rect ResolveHighlightBoundsUia(
        FlaUI.Core.Application application,
        FlaUI.UIA3.UIA3Automation automation,
        long windowHandle,
        ElementLocator locator,
        string? elementId)
    {
        Window window;
        try
        {
            window = FindWindowByHandle(application, automation, windowHandle);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(elementId))
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
            }

            throw;
        }

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        AutomationElement element;

        if (!string.IsNullOrWhiteSpace(elementId))
        {
            element = ResolveUiaElementById(window, rawWalker, elementId.Trim(), out _);
        }
        else
        {
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            element = ResolveElement(window, locator, controlWalker, rawWalker);
        }

        return ToRect(element.BoundingRectangle);
    }

    private async Task<ElementRef> ResolveHighlightElementWpfAsync(
        long windowHandle,
        ElementLocator locator,
        CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            throw new InvalidOperationException("WPF agent is not connected. Call inject_agent first.");
        }

        return await client.CallAsync<ElementRef>(
            "wpf/resolve_element",
            new ResolveWpfElementRequest(
                WindowHandle: windowHandle,
                Locator: locator,
                RootXPath: null,
                VisibleOnly: true,
                MaxNodes: 2000,
                ReturnFields: FindReturnFields.Standard),
            cancellationToken).ConfigureAwait(false);
    }
}
