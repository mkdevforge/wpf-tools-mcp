using System.Text.Json.Serialization;

namespace WpfToolsMcp.Contracts;

public sealed record TakeScreenshotSequenceRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    ScreenshotCaptureMode CaptureMode = ScreenshotCaptureMode.Auto,
    ScreenshotCaptureArea Area = ScreenshotCaptureArea.Client,
    ScreenshotClipMode Clip = ScreenshotClipMode.Intersect,
    int FrameCount = 12,
    int IntervalMs = 100,
    string? OutputDirectory = null);

public sealed record ScreenshotSequenceFailure(
    string Code,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExceptionType = null);

public sealed record ScreenshotSequenceCaptureContext(
    long WindowHandleUsed,
    Rect CapturedBounds,
    ScreenshotCaptureMode CaptureModeUsed,
    ScreenshotCaptureArea Area,
    ScreenshotClipMode Clip,
    bool WasClipped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ViewportConditions? Viewport);

public sealed record ScreenshotSequenceFrame(
    int Index,
    string Path,
    DateTimeOffset ObservedAtUtc,
    long ElapsedMs,
    long CaptureDurationMs,
    int Width,
    int Height,
    Rect CapturedBounds,
    bool WasClipped);

public sealed record ScreenshotSequenceManifest(
    int Version,
    string SequenceId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int RequestedFrameCount,
    int CapturedFrameCount,
    int IntervalMs,
    bool Complete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenshotSequenceFailure? Failure,
    ScreenshotSequenceCaptureContext CaptureContext,
    IReadOnlyList<ScreenshotSequenceFrame> Frames);

public sealed record TakeScreenshotSequenceResponse(
    string SequenceId,
    string DirectoryPath,
    string ManifestPath,
    string FirstFramePath,
    string LastFramePath,
    int RequestedFrameCount,
    int CapturedFrameCount,
    int IntervalMs,
    bool Complete,
    long WindowHandleUsed,
    Rect CapturedBounds,
    ScreenshotCaptureMode CaptureModeUsed,
    bool WasClipped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenshotSequenceFailure? Failure = null);
