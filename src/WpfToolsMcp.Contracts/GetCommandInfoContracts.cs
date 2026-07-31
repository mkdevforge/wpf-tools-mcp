namespace WpfToolsMcp.Contracts;

using System.Text.Json.Serialization;

public sealed record GetCommandInfoRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    int MaxAncestors = 8,
    int MaxBindings = 128,
    int MaxValueLength = 500);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandInspectionStatus
{
    Available,
    Missing,
    Null,
    Empty,
    Unsupported,
    Unavailable,
    Threw,
    NotEvaluated
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandCanExecuteMode
{
    Command,
    RoutedCommand
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandGestureKind
{
    Key,
    Mouse,
    Custom
}

public sealed record CommandFormattedValue(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value,
    ProvenanceEvidence Evidence,
    bool Truncated = false);

public sealed record CommandMemberValue(
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandFormattedValue? Formatted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandIdentityInfo(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandMemberValue? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandMemberValue? Display = null);

public sealed record CommandElementSummary(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath = null);

public sealed record CommandTargetInfo(
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandElementSummary? Element = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandSourceInfo(
    CommandInspectionStatus Status,
    string SourceType,
    string CommandProperty,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandIdentityInfo? Command,
    CommandMemberValue Parameter,
    CommandTargetInfo Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandEnabledInfo(
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandCanExecuteInfo(
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? CanExecute = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandCanExecuteMode? Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandTargetInfo? EffectiveTarget = null,
    bool UsedCommandSourceFallback = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnavailableReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandBindingInspection(
    int Index,
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandIdentityInfo? Command = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MatchesSourceCommand = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandGestureInfo(
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandGestureKind? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Modifiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MouseAction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandMemberValue? Display = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record InputBindingInspection(
    int Index,
    string Type,
    CommandInspectionStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommandIdentityInfo? Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MatchesSourceCommand,
    CommandMemberValue Parameter,
    CommandGestureInfo Gesture,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandBindingCollectionInfo(
    CommandInspectionStatus Status,
    int DiscoveredCount,
    IReadOnlyList<CommandBindingInspection> Bindings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record InputBindingCollectionInfo(
    CommandInspectionStatus Status,
    int DiscoveredCount,
    IReadOnlyList<InputBindingInspection> Bindings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? Failure = null);

public sealed record CommandContextInfo(
    int Depth,
    CommandElementSummary Element,
    CommandBindingCollectionInfo CommandBindings,
    InputBindingCollectionInfo InputBindings);

public sealed record CommandInspectionCounts(
    int ReturnedContexts,
    int DiscoveredCommandBindings,
    int ReturnedCommandBindings,
    int DiscoveredInputBindings,
    int ReturnedInputBindings);

public sealed record GetCommandInfoResponse(
    ElementRef Element,
    CommandSourceInfo Source,
    CommandEnabledInfo ControlIsEnabled,
    CommandCanExecuteInfo CanExecute,
    IReadOnlyList<CommandContextInfo> ContextChain,
    CommandInspectionCounts Counts,
    CommandInspectionStatus ParentChainStatus,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TruncatedReasons = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticCauseInfo? ParentChainFailure = null)
{
    public long WindowHandleUsed { get; init; }
}
