namespace WpfToolsMcp.Contracts;

using System.Text.Json.Serialization;

public sealed record GetLayoutContextRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    int MaxAncestors = 6,
    int MaxSiblings = 8,
    int MaxGridDefinitions = 32);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutLengthKind
{
    Value,
    Auto,
    Unbounded
}

public sealed record LayoutLength(
    LayoutLengthKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Value = null);

public sealed record LayoutSize(double Width, double Height);

public sealed record LayoutPoint(double X, double Y);

public sealed record LayoutThickness(double Left, double Top, double Right, double Bottom);

public sealed record LayoutRect(double X, double Y, double Width, double Height);

public sealed record LayoutMatrix(
    double M11,
    double M12,
    double M21,
    double M22,
    double OffsetX,
    double OffsetY);

public sealed record LayoutTransformInfo(
    string Type,
    LayoutMatrix Matrix,
    bool IsIdentity);

public sealed record LayoutAlignmentInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Horizontal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Vertical = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HorizontalContent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerticalContent = null);

public sealed record LayoutVisibilityInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Visibility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsVisible = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsMeasureValid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsArrangeValid = null);

public sealed record LayoutGeometryInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? LayoutSlotInParentWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? RenderBoundsInParentWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? RenderBoundsInWindowWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? ScreenBoundsPhysicalPixels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpiScaleX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpiScaleY = null);

public sealed record LayoutClippingInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ClipToBounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasExplicitClip = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ExplicitClipIsEmpty = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? ExplicitClipBoundsLocalWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasLayoutClip = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LayoutClipIsEmpty = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? LayoutClipBoundsLocalWpfDips = null);

public sealed record LayoutElementSummary(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName = null,
    bool IdentityTruncated = false);

public sealed record LayoutElementIdentity(
    string Type,
    string XPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName = null,
    bool IdentityTruncated = false);

public sealed record LayoutElementMetrics(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? VisualIndexInParent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? DesiredSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? RenderSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? ActualSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? ConfiguredWidthWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? ConfiguredHeightWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? MinimumSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? MaximumWidthWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? MaximumHeightWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutThickness? MarginWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutThickness? PaddingWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutAlignmentInfo? Alignment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutVisibilityInfo? Visibility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutGeometryInfo? Geometry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutClippingInfo? Clipping = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutTransformInfo? LayoutTransform = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutTransformInfo? RenderTransform = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutPoint? RenderTransformOrigin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ZIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutElementSummary? TemplatedParent = null);

public sealed record LayoutAncestorContext(
    int Depth,
    LayoutElementIdentity Element,
    LayoutElementMetrics Layout);

public sealed record LayoutGridCellPlacement(int Row, int Column, int RowSpan, int ColumnSpan);

public sealed record LayoutGridPlacement(
    LayoutGridCellPlacement Raw,
    LayoutGridCellPlacement Effective,
    bool UsesImplicitRowDefinition,
    bool UsesImplicitColumnDefinition);

public sealed record LayoutSiblingMetrics(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? DesiredSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? RenderSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? ActualSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? ConfiguredWidthWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? ConfiguredHeightWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutSize? MinimumSizeWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? MaximumWidthWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? MaximumHeightWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutThickness? MarginWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutAlignmentInfo? Alignment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutVisibilityInfo? Visibility = null);

public sealed record LayoutGridSplitterInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResizeDirection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResizeBehavior = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ShowsPreview = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DragIncrementWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? KeyboardIncrementWpfDips = null);

public sealed record LayoutSiblingContext(
    int ContextDepth,
    LayoutElementIdentity Parent,
    int VisualIndex,
    int RelativeVisualIndex,
    LayoutElementIdentity Element,
    LayoutSiblingMetrics Layout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? RenderBoundsInParentWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? RenderBoundsInWindowWpfDips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? ScreenBoundsPhysicalPixels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpiScaleX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpiScaleY = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutGridPlacement? GridPlacement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ZIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutGridSplitterInfo? GridSplitter = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutGridUnitType
{
    Unknown,
    Auto,
    Pixel,
    Star
}

public sealed record LayoutGridDefinition(
    int Index,
    LayoutGridUnitType UnitType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ConfiguredValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ActualSizeWpfDips,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MinimumSizeWpfDips,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutLength? MaximumSizeWpfDips,
    bool IsImplicit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsAllocated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsNeighbor);

public sealed record LayoutGridContext(
    int ContextDepth,
    LayoutElementIdentity Grid,
    LayoutElementIdentity AllocatedChild,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutGridPlacement? Placement,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutRect? AllocationWpfDips,
    IReadOnlyList<LayoutGridDefinition> Rows,
    IReadOnlyList<LayoutGridDefinition> Columns,
    int TotalRows,
    int ReturnedRows,
    int TotalColumns,
    int ReturnedColumns,
    bool Truncated);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutEvidenceStatus
{
    NotApplicable,
    Unsupported,
    Unavailable
}

public sealed record LayoutUnavailableEvidence(
    string SubjectXPath,
    string Field,
    LayoutEvidenceStatus Status,
    string Reason);

public sealed record LayoutContextCounts(
    int DiscoveredAncestors,
    int ReturnedAncestors,
    int DiscoveredSiblings,
    int ReturnedSiblings,
    int DiscoveredGridContexts,
    int ReturnedGridContexts,
    int DiscoveredGridDefinitions,
    int ReturnedGridDefinitions,
    int DiscoveredUnavailableEvidence,
    int ReturnedUnavailableEvidence);

public sealed record GetLayoutContextResponse(
    ElementRef Element,
    LayoutElementMetrics Target,
    IReadOnlyList<LayoutAncestorContext> Ancestors,
    IReadOnlyList<LayoutSiblingContext> Siblings,
    IReadOnlyList<LayoutGridContext> GridContexts,
    LayoutContextCounts Counts,
    IReadOnlyList<LayoutUnavailableEvidence> UnavailableEvidence,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TruncatedReasons = null)
{
    public long WindowHandleUsed { get; init; }
}

internal static class LayoutContextText
{
    public static string TruncateAtValidUtf16Boundary(string? value, int maxLength, out bool truncated)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        value ??= string.Empty;
        truncated = value.Length > maxLength;
        if (!truncated)
        {
            return value;
        }

        var length = maxLength;
        if (length > 0 &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }
}

internal static class LayoutContextEvidenceFields
{
    public const string DpiScaleX = "geometry.dpiScaleX";
    public const string DpiScaleY = "geometry.dpiScaleY";
}
