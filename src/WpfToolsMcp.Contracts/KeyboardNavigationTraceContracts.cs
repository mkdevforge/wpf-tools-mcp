using System.Text.Json.Serialization;

namespace WpfToolsMcp.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardNavigationDirection
{
    Next,
    Previous
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardNavigationTraceMode
{
    Physical,
    WpfSemantic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardNavigationStopReason
{
    MaximumSteps,
    NoFocusChange,
    CycleDetected,
    FocusLeftWindow,
    WindowClosed,
    FocusUnavailable,
    SemanticInteropBoundary
}

public sealed record TraceKeyboardNavigationRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    KeyboardNavigationDirection Direction = KeyboardNavigationDirection.Next,
    KeyboardNavigationTraceMode Mode = KeyboardNavigationTraceMode.Physical,
    int MaxSteps = 20,
    bool RestoreFocus = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record WpfKeyboardNavigationMetadata(
    int TabIndex,
    bool IsTabStop,
    bool Focusable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsVisible,
    bool IsFocusScope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FocusScopeXPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NavigationGroupXPath,
    string TabNavigation,
    string ControlTabNavigation,
    string DirectionalNavigation);

public sealed record KeyboardNavigationFocusObservation(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementRef? Uia = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementRef? Wpf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WpfKeyboardNavigationMetadata? WpfMetadata = null);

public sealed record KeyboardNavigationTraceStep(
    int Index,
    string MethodUsed,
    int DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KeyboardNavigationFocusObservation? Focus);

public sealed record KeyboardNavigationRestoration(
    bool Requested,
    bool Attempted,
    bool Restored,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MethodUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Failure = null);

public sealed record TraceKeyboardNavigationResponse(
    long WindowHandleUsed,
    KeyboardNavigationDirection Direction,
    KeyboardNavigationTraceMode ModeUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KeyboardNavigationFocusObservation? Start,
    IReadOnlyList<KeyboardNavigationTraceStep> Steps,
    KeyboardNavigationStopReason StopReason,
    KeyboardNavigationRestoration Restoration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

public sealed record WpfKeyboardNavigationStepRequest(
    long WindowHandle,
    KeyboardNavigationDirection Direction,
    bool Move);

public sealed record WpfKeyboardNavigationStepResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementRef? Focus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WpfKeyboardNavigationMetadata? Metadata,
    bool MoveAttempted,
    bool MoveAccepted,
    bool InteropBoundary);
