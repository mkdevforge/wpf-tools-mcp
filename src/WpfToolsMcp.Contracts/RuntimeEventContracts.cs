using System.Text.Json.Serialization;

namespace WpfToolsMcp.Contracts;

public static class RuntimeEventVersions
{
    public const int V1 = 1;
}

public static class RuntimeEventSourceKinds
{
    public const string BindingErrors = "binding_errors";
    public const string PropertyChanges = "property_changes";
    public const string ToolTrace = "tool_trace";
}

public static class SubscriptionEventKinds
{
    public const string BindingErrorAdded = "binding_error_added";
    public const string PropertyInitial = "property_initial";
    public const string PropertyChanged = "property_changed";
    public const string Terminal = "subscription_terminal";
}

public static class SubscriptionTerminalCodes
{
    public const string DurationElapsed = "duration_elapsed";
    public const string ElementUnloaded = "element_unloaded";
    public const string ClientRequested = "client_requested";
    public const string AgentConnectionLost = "agent_connection_lost";
    public const string TargetExited = "target_exited";
    public const string SourceError = "source_error";
    public const string SourceReleaseFailed = "source_release_failed";
    public const string Completed = "completed";
}

public sealed record RuntimeEventEnvelope(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("streamId")] string StreamId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("windowHandle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    [property: JsonPropertyName("elementId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ElementId = null,
    [property: JsonPropertyName("xpath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath = null,
    [property: JsonPropertyName("xpathOmitted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? XPathOmitted = null);

public sealed record SubscriptionTerminalEvent(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("completedAtUtc")] DateTimeOffset CompletedAtUtc)
{
    [JsonPropertyName("cause"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticCauseInfo? Cause { get; init; }

    [JsonPropertyName("causeTruncated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CauseTruncated { get; init; }
}
