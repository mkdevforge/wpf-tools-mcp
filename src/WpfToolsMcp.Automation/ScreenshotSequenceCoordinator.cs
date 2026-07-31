using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal sealed record ScreenshotSequenceCaptureSample(
    int Width,
    int Height,
    Rect CapturedBounds,
    bool WasClipped,
    long WindowHandleUsed,
    ScreenshotCaptureMode CaptureModeUsed,
    ViewportConditions? Viewport = null);

internal static class ScreenshotSequenceCoordinator
{
    internal const int MinimumFrameCount = 2;
    internal const int MaximumFrameCount = 300;
    internal const int MaximumRequestedDelayMs = 30_000;

    private const int ManifestVersion = 1;
    private const int MaximumFailureDetailLength = 2_000;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void ValidateSchedule(int frameCount, int intervalMs)
    {
        if (frameCount is < MinimumFrameCount or > MaximumFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                frameCount,
                $"frameCount must be between {MinimumFrameCount} and {MaximumFrameCount}.");
        }

        if (intervalMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMs), intervalMs, "intervalMs must be at least 1.");
        }

        var requestedDelayMs = (frameCount - 1L) * intervalMs;
        if (requestedDelayMs > MaximumRequestedDelayMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalMs),
                intervalMs,
                $"The requested inter-frame delay totals {requestedDelayMs} ms; the maximum is {MaximumRequestedDelayMs} ms.");
        }
    }

    public static async Task<TakeScreenshotSequenceResponse> CaptureAsync(
        string sequenceId,
        string directoryPath,
        ScreenshotCaptureArea area,
        ScreenshotClipMode clip,
        int frameCount,
        int intervalMs,
        Func<int, string, CancellationToken, Task<ScreenshotSequenceCaptureSample>> captureFrame,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(captureFrame);
        ValidateSchedule(frameCount, intervalMs);

        timeProvider ??= TimeProvider.System;
        delay ??= static (duration, token) => Task.Delay(duration, token);

        var startedAtUtc = timeProvider.GetUtcNow();
        var startedTimestamp = timeProvider.GetTimestamp();
        var manifestPath = Path.Combine(directoryPath, "manifest.json");
        var frames = new List<ScreenshotSequenceFrame>(frameCount);
        ScreenshotSequenceCaptureContext? captureContext = null;
        ScreenshotSequenceFailure? failure = null;

        try
        {
            for (var index = 0; index < frameCount; index++)
            {
                if (index > 0)
                {
                    await delay(TimeSpan.FromMilliseconds(intervalMs), cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var framePath = Path.Combine(directoryPath, $"frame-{index:D4}.png");
                var temporaryFramePath = Path.Combine(
                    directoryPath,
                    $".frame-{index:D4}-{Guid.NewGuid():N}.tmp");
                var observedAtUtc = timeProvider.GetUtcNow();
                var elapsedMs = ToMilliseconds(timeProvider.GetElapsedTime(startedTimestamp));
                var captureStartedTimestamp = timeProvider.GetTimestamp();

                ScreenshotSequenceCaptureSample sample;
                try
                {
                    sample = await captureFrame(index, temporaryFramePath, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (captureContext is not null &&
                        (sample.WindowHandleUsed != captureContext.WindowHandleUsed ||
                         sample.CapturedBounds != captureContext.CapturedBounds ||
                         sample.CaptureModeUsed != captureContext.CaptureModeUsed))
                    {
                        failure = new ScreenshotSequenceFailure(
                            "capture_context_changed",
                            BoundFailureDetail(
                                $"Frame {index} did not preserve the pinned HWND, bounds, and capture mode."));
                        break;
                    }

                    File.Move(temporaryFramePath, framePath);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (frames.Count > 0)
                {
                    failure = CreateFailure("capture_failed", ex);
                    break;
                }
                finally
                {
                    TryDelete(temporaryFramePath);
                }

                var captureDurationMs = ToMilliseconds(timeProvider.GetElapsedTime(captureStartedTimestamp));
                if (captureContext is null)
                {
                    captureContext = new ScreenshotSequenceCaptureContext(
                        sample.WindowHandleUsed,
                        sample.CapturedBounds,
                        sample.CaptureModeUsed,
                        area,
                        clip,
                        sample.WasClipped,
                        sample.Viewport);
                }

                frames.Add(new ScreenshotSequenceFrame(
                    index,
                    framePath,
                    observedAtUtc,
                    elapsedMs,
                    captureDurationMs,
                    sample.Width,
                    sample.Height,
                    sample.CapturedBounds,
                    sample.WasClipped));
            }

            var complete = failure is null && frames.Count == frameCount;
            var manifest = CreateManifest(
                sequenceId,
                startedAtUtc,
                timeProvider.GetUtcNow(),
                frameCount,
                intervalMs,
                complete,
                failure,
                captureContext!,
                frames);
            await PublishManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            return CreateResponse(directoryPath, manifestPath, manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && captureContext is not null)
        {
            var cancelled = new ScreenshotSequenceFailure(
                "cancelled",
                "Screenshot sequence capture was cancelled.");
            var manifest = CreateManifest(
                sequenceId,
                startedAtUtc,
                timeProvider.GetUtcNow(),
                frameCount,
                intervalMs,
                complete: false,
                cancelled,
                captureContext,
                frames);
            await PublishManifestBestEffortAsync(manifestPath, manifest).ConfigureAwait(false);
            throw;
        }
    }

    private static ScreenshotSequenceManifest CreateManifest(
        string sequenceId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        int requestedFrameCount,
        int intervalMs,
        bool complete,
        ScreenshotSequenceFailure? failure,
        ScreenshotSequenceCaptureContext captureContext,
        IReadOnlyList<ScreenshotSequenceFrame> frames) =>
        new(
            ManifestVersion,
            sequenceId,
            startedAtUtc,
            completedAtUtc,
            requestedFrameCount,
            frames.Count,
            intervalMs,
            complete,
            failure,
            captureContext,
            frames);

    private static TakeScreenshotSequenceResponse CreateResponse(
        string directoryPath,
        string manifestPath,
        ScreenshotSequenceManifest manifest) =>
        new(
            manifest.SequenceId,
            directoryPath,
            manifestPath,
            manifest.Frames[0].Path,
            manifest.Frames[^1].Path,
            manifest.RequestedFrameCount,
            manifest.CapturedFrameCount,
            manifest.IntervalMs,
            manifest.Complete,
            manifest.CaptureContext.WindowHandleUsed,
            manifest.CaptureContext.CapturedBounds,
            manifest.CaptureContext.CaptureModeUsed,
            manifest.CaptureContext.WasClipped,
            manifest.Failure);

    private static ScreenshotSequenceFailure CreateFailure(string code, Exception exception)
    {
        var root = exception.GetBaseException();
        var detail = string.IsNullOrWhiteSpace(root.Message)
            ? root.GetType().FullName ?? root.GetType().Name
            : root.Message;
        return new ScreenshotSequenceFailure(
            code,
            BoundFailureDetail(detail),
            root.GetType().FullName ?? root.GetType().Name);
    }

    private static string BoundFailureDetail(string detail) =>
        detail.Length <= MaximumFailureDetailLength
            ? detail
            : detail[..(MaximumFailureDetailLength - 3)] + "...";

    private static long ToMilliseconds(TimeSpan duration) =>
        Math.Max(0, (long)duration.TotalMilliseconds);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static async Task PublishManifestAsync(
        string manifestPath,
        ScreenshotSequenceManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            $".manifest-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task PublishManifestBestEffortAsync(
        string manifestPath,
        ScreenshotSequenceManifest manifest)
    {
        try
        {
            await PublishManifestAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
