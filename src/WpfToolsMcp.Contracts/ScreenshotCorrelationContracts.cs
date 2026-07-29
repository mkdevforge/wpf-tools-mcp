namespace WpfToolsMcp.Contracts;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotCorrelationBackend
{
    Auto,
    Uia,
    Wpf,
    Both
}

public sealed record ScreenshotCorrelationOptions(
    int X,
    int Y,
    int Width = 1,
    int Height = 1,
    ScreenshotCorrelationBackend Backend = ScreenshotCorrelationBackend.Auto,
    int MaxCandidates = 8,
    int MaxNodes = 10_000,
    bool IncludeAncestors = false,
    int MaxAncestors = 4,
    bool Annotate = true);

public sealed record ScreenshotCorrelationPoint(int X, int Y);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotCorrelationMatchKind
{
    DirectHit,
    RenderedHit,
    BoundsIntersection
}

public sealed record ScreenshotCorrelationAnnotation(
    int Index,
    InspectionBackend Backend,
    Rect ImageBounds,
    string Label,
    string Color);

public sealed record ScreenshotCorrelationCandidate(
    int Index,
    InspectionBackend Backend,
    ElementRef Element,
    ScreenshotCorrelationMatchKind MatchKind,
    Rect IntersectionPhysicalPixels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ElementRef>? Ancestors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenshotCorrelationAnnotation? Annotation = null);

public sealed record ScreenshotCorrelationBackendResult(
    InspectionBackend Backend,
    IReadOnlyList<ScreenshotCorrelationCandidate> Candidates,
    int ReturnedCandidates,
    int DiscoveredCandidates,
    int ScannedNodes,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DirectHitIndex,
    bool HasOverlaps);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotObscurationState
{
    NotApplicable,
    ClearAtSamplePoints,
    PotentiallyObscured,
    Unknown
}

public sealed record ScreenshotObscurationInfo(
    ScreenshotObscurationState State,
    int SampledPoints,
    int ObscuredPoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<long>? ObscuringWindowHandles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record ScreenshotCaptureContext(
    ScreenshotCaptureMode CaptureModeRequested,
    ScreenshotCaptureMode CaptureModeUsed,
    ScreenshotCaptureArea Area,
    ScreenshotClipMode Clip,
    WindowInfo Window,
    Rect CapturedBounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? RequestedBounds,
    bool WasClipped,
    ViewportConditions Viewport,
    ScreenshotObscurationInfo Obscuration);

public sealed record ScreenshotCorrelationResult(
    Rect ImageRegion,
    Rect ScreenRegionPhysicalPixels,
    IReadOnlyList<ScreenshotCorrelationBackendResult> Backends,
    int ReturnedCandidates,
    int DiscoveredCandidates,
    int ScannedNodes,
    bool Ambiguous,
    IReadOnlyList<ScreenshotCorrelationAnnotation> Annotations,
    ScreenshotCaptureContext CaptureContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenshotCorrelationPoint? ScreenPointPhysicalPixels = null);

public sealed record CorrelateWpfScreenshotRegionRequest(
    Rect ScreenRegionPhysicalPixels,
    long? WindowHandle = null,
    int MaxCandidates = 8,
    int MaxNodes = 10_000,
    bool IncludeAncestors = false,
    int MaxAncestors = 4,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenshotCorrelationPoint? ScreenPointPhysicalPixels = null);

public sealed record CorrelateWpfScreenshotRegionResponse(
    ScreenshotCorrelationBackendResult Result);
