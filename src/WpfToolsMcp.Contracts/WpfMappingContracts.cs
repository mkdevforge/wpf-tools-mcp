using System.Text.Json.Serialization;

namespace WpfToolsMcp.Contracts;

public sealed record UiaMappingSource(
    string ControlType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    Rect Bounds);

public sealed record WpfMappingCandidate(
    ElementRef Element,
    int Score,
    IReadOnlyList<string> Evidence);

public sealed record WpfMappingDiagnostics(
    bool Available,
    string Method,
    IReadOnlyList<WpfMappingCandidate> Candidates,
    int ReturnedCandidates,
    int TotalCandidates,
    int ScannedNodes,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ElementMappingStatus? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedElementId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedXPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Score { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScoreLead { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Evidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureInfo? Failure { get; init; }
}

public sealed record MapUiaToWpfAgentRequest(
    long WindowHandle,
    UiaMappingSource Source,
    int MaxNodes);

public sealed record MapUiaToWpfAgentResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementRef? SelectedElement,
    WpfMappingDiagnostics Mapping);
