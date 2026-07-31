using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public async Task<TakeScreenshotSequenceResponse> TakeScreenshotSequenceAsync(
        TakeScreenshotSequenceRequest request,
        CancellationToken cancellationToken = default,
        bool autoInject = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Locator is not null && !string.IsNullOrWhiteSpace(request.ElementId))
        {
            throw new ArgumentException("Provide either locator or elementId, not both.");
        }

        if (!Enum.IsDefined(request.CaptureMode))
        {
            throw new ArgumentOutOfRangeException(nameof(request.CaptureMode), request.CaptureMode, "Unsupported capture mode.");
        }

        if (!Enum.IsDefined(request.Area))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Area), request.Area, "Unsupported capture area.");
        }

        if (!Enum.IsDefined(request.Clip))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Clip), request.Clip, "Unsupported clip mode.");
        }

        ScreenshotSequenceCoordinator.ValidateSchedule(request.FrameCount, request.IntervalMs);

        var trace = BeginTraceSpan("take_screenshot_sequence");
        try
        {
            var sequenceId = Guid.NewGuid().ToString("N");
            var directoryPath = CreateScreenshotSequenceDirectory(request.OutputDirectory, sequenceId);
            Rect? pinnedBounds = null;
            ScreenshotCaptureMode? pinnedMode = null;
            long? pinnedWindowHandle = null;

            async Task<ScreenshotSequenceCaptureSample> CaptureFrameAsync(
                int index,
                string outputPath,
                CancellationToken token)
            {
                if (index == 0)
                {
                    var first = await TakeScreenshotAsync(
                        new TakeScreenshotRequest(
                            request.WindowHandle,
                            request.Locator,
                            request.ElementId,
                            InspectionBackend.Auto,
                            request.CaptureMode,
                            request.Area,
                            request.Clip,
                            ScreenshotImageFormat.Png,
                            JpegQuality: 90,
                            OutputPath: outputPath,
                            IncludeOverlay: false,
                            AutoScroll: true,
                            FullyVisible: true,
                            Annotate: false,
                            ReturnBase64: false)
                        {
                            IncludeViewport = true
                        },
                        token,
                        autoInject).ConfigureAwait(false);

                    var initialApplication = EnsureAttached();
                    var initialAutomation = EnsureAutomation();
                    _ = FindWindowByHandle(initialApplication, initialAutomation, first.WindowHandleUsed);
                    pinnedBounds = first.CapturedBounds;
                    pinnedMode = first.CaptureModeUsed;
                    pinnedWindowHandle = first.WindowHandleUsed;

                    return new ScreenshotSequenceCaptureSample(
                        first.Width,
                        first.Height,
                        first.CapturedBounds,
                        first.WasClipped,
                        first.WindowHandleUsed,
                        first.CaptureModeUsed,
                        first.Viewport);
                }

                token.ThrowIfCancellationRequested();
                var bounds = pinnedBounds
                    ?? throw new InvalidOperationException("Screenshot sequence bounds were not initialized.");
                var mode = pinnedMode
                    ?? throw new InvalidOperationException("Screenshot sequence capture mode was not initialized.");
                var windowHandle = pinnedWindowHandle
                    ?? throw new InvalidOperationException("Screenshot sequence window was not initialized.");
                var application = EnsureAttached();
                var automation = EnsureAutomation();
                var window = FindWindowByHandle(application, automation, windowHandle);

                var capture = CaptureScreenshotWithMetadata(
                    window,
                    bounds,
                    mode,
                    request.Area,
                    request.Clip,
                    includeOverlay: false);
                using var bitmap = capture.Bitmap;

                if (capture.CapturedBounds != bounds || capture.CaptureModeUsed != mode)
                {
                    throw new InvalidOperationException(
                        $"Pinned screenshot context changed at frame {index}: " +
                        $"expected bounds {bounds} and mode {mode}, " +
                        $"observed bounds {capture.CapturedBounds} and mode {capture.CaptureModeUsed}.");
                }

                SaveBitmapWithWic(bitmap, outputPath, ScreenshotImageFormat.Png, jpegQuality: 90);
                return new ScreenshotSequenceCaptureSample(
                    bitmap.Width,
                    bitmap.Height,
                    capture.CapturedBounds,
                    capture.WasClipped,
                    windowHandle,
                    capture.CaptureModeUsed);
            }

            var response = await ScreenshotSequenceCoordinator.CaptureAsync(
                sequenceId,
                directoryPath,
                request.Area,
                request.Clip,
                request.FrameCount,
                request.IntervalMs,
                CaptureFrameAsync,
                cancellationToken).ConfigureAwait(false);

            trace?.SetSummary(
                $"frames={response.CapturedFrameCount}/{response.RequestedFrameCount} " +
                $"complete={response.Complete} {Path.GetFileName(response.DirectoryPath)}");
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

    private static string CreateScreenshotSequenceDirectory(string? outputDirectory, string sequenceId)
    {
        var parent = outputDirectory;
        if (string.IsNullOrWhiteSpace(parent))
        {
            parent = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_SCREENSHOT_DIR");
        }

        if (string.IsNullOrWhiteSpace(parent))
        {
            parent = Path.Combine(Path.GetTempPath(), "wpf-tools-mcp", "screenshots");
        }

        var directoryPath = Path.Combine(Path.GetFullPath(parent), $"sequence-{sequenceId}");
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }
}
