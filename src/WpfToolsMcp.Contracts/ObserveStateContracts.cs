namespace WpfToolsMcp.Contracts;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObserveStateSource
{
    DependencyProperty,
    DataContextPath
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObserveStateEventKind
{
    Initial,
    Change
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObserveStateValueState
{
    Value,
    Null,
    Unset,
    Unavailable,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObserveStateStopReason
{
    DurationElapsed,
    ElementUnloaded,
    ClientRequested
}

public sealed record ObserveStateStartRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    IReadOnlyList<string>? DependencyProperties = null,
    IReadOnlyList<string>? DataContextPaths = null,
    int MaxNodes = 5_000,
    int DurationMs = 30_000,
    int MaxEvents = 256,
    int MaxValueLength = 512,
    bool IncludeVisualMetadata = false);

public sealed record ObserveStateWatch(
    ObserveStateSource Source,
    string Path);

public sealed record ObserveStateValue(
    ObserveStateValueState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueType = null,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed record ObserveStateVisualMetadata(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsVisible = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Visibility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled = null);

public sealed record ObserveStateEvent(
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    long ElapsedMs,
    ObserveStateSource Source,
    string Path,
    ObserveStateEventKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObserveStateValue? OldValue,
    ObserveStateValue NewValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PreviousValueDurationMs = null,
    int CoalescedChangeCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObserveStateVisualMetadata? Visual = null);

public sealed record ObserveStateStartResponse(
    string ObservationId,
    ElementRef Element,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int DurationMs,
    int MaxEvents,
    int MaxValueLength,
    int MaxNodes,
    IReadOnlyList<ObserveStateWatch> Watches,
    IReadOnlyList<ObserveStateEvent> InitialEvents);

public sealed record ObserveStatePollRequest(
    string ObservationId,
    int MaxBatch = 100,
    int MaxPayloadChars = 262_144);

public sealed record ObserveStatePollResponse(
    IReadOnlyList<ObserveStateEvent> Events,
    int DroppedSinceLastPoll,
    long DroppedTotal,
    int CoalescedSinceLastPoll,
    long CoalescedTotal,
    int TruncatedSinceLastPoll,
    long TruncatedTotal,
    bool HasMore,
    bool Completed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObserveStateStopReason? StopReason,
    DateTimeOffset StartedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StoppedAtUtc,
    long DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OldestAvailableSequence);

public sealed record ObserveStateStopRequest(string ObservationId);

public sealed record ObserveStateStopResponse(
    bool Stopped,
    ObserveStateStopReason StopReason,
    DateTimeOffset StoppedAtUtc,
    int PendingEventCount);
