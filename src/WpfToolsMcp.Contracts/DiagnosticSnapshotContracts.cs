using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WpfToolsMcp.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSection
{
    VisualTree,
    UiaProperties,
    WpfProperties,
    Layout,
    Bindings,
    DataContext,
    BindingErrors,
    Screenshot
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSectionStatus
{
    Success,
    Unavailable,
    Truncated,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticCaptureSource
{
    WpfDispatcher,
    Uia,
    Screenshot
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticTargetScope
{
    Window,
    Element
}

public static class DiagnosticSnapshotLimits
{
    public const int MaxSections = 8;
    public const int MinDepth = 1;
    public const int MaxDepth = 6;
    public const int MinItems = 1;
    public const int MaxItems = 100;
    public const int MinNodes = 1;
    public const int MaxNodes = 1_000;
    public const int MinValueLength = 64;
    public const int MaxValueLength = 2_000;
    public const int MinPayloadChars = 1_000;
    public const int MaxPayloadChars = 100_000;
    public const int MaxPropertyNames = 50;
    public const int MaxPropertyNameLength = 256;
    public const int MaxFailureMessageLength = 1_000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 30_000;
}

public sealed record DiagnosticSnapshotBudget(
    int MaxDepth = 3,
    int MaxItems = 25,
    int MaxNodes = 200,
    int MaxValueLength = 1_000,
    int MaxPayloadChars = 40_000);

public sealed record CaptureDiagnosticSnapshotRequest(
    string SessionId,
    IReadOnlyList<DiagnosticSection> Sections,
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    DiagnosticSnapshotBudget? Budget = null,
    IReadOnlyList<string>? PropertyNames = null,
    IReadOnlyList<string>? DataContextProperties = null,
    int TimeoutMs = 10_000);

public sealed record DiagnosticSnapshotTarget(
    string SessionId,
    int ProcessId,
    string ProcessName,
    long WindowHandle,
    string WindowTitle,
    DiagnosticTargetScope Scope,
    InspectionBackend AnchorBackend,
    ElementRef Element);

public sealed record DiagnosticSectionResult(
    DiagnosticSection Section,
    DiagnosticSectionStatus Status,
    DiagnosticCaptureSource Source,
    string EvidenceSchema,
    string CaptureGroup,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long StartedOffsetMs,
    long CompletedOffsetMs,
    long DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Data = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    int PayloadChars = 0);

public sealed record DiagnosticSnapshotConsistency(
    bool SessionSerialized,
    bool WpfSectionsSingleDispatcherTurn,
    bool CrossBackendAtomic,
    long TimingSkewMs);

public sealed record CaptureDiagnosticSnapshotResponse(
    string CaptureId,
    DiagnosticSnapshotTarget Target,
    DiagnosticSnapshotBudget Budget,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMs,
    DiagnosticSnapshotConsistency Consistency,
    IReadOnlyList<DiagnosticSectionResult> Sections);

public sealed record CaptureWpfDiagnosticSnapshotRequest(
    long? WindowHandle,
    ElementLocator? Locator,
    [property: JsonPropertyName("elementId")] string? ElementId,
    string RootXPath,
    IReadOnlyList<DiagnosticSection> Sections,
    DiagnosticSnapshotBudget Budget,
    IReadOnlyList<string>? PropertyNames = null,
    IReadOnlyList<string>? DataContextProperties = null);

public sealed record CaptureWpfDiagnosticSnapshotResponse(
    ElementRef Target,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<DiagnosticSectionResult> Sections);
