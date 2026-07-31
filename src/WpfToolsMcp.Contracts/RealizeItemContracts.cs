using System.Text.Json.Serialization;

namespace WpfToolsMcp.Contracts;

public static class RealizeItemLimits
{
    public const int DefaultMaxProviderCalls = 100;
    public const int MinimumProviderCalls = 1;
    public const int MaximumProviderCalls = 1_000;
    public const int DefaultAdvisoryElapsedLimitMs = 5_000;
    public const int MinimumAdvisoryElapsedLimitMs = 1;
    public const int MaximumAdvisoryElapsedLimitMs = 60_000;
    public const int DefaultPollIntervalMs = 50;
    public const int MinimumPollIntervalMs = 10;
    public const int MaximumPollIntervalMs = 1_000;
}

public static class RealizeItemOutcomes
{
    public const string MethodNone = "none";
    public const string MethodAlreadyRealized = "alreadyRealized";
    public const string MethodVirtualizedItemRealize = "virtualizedItemRealize";

    public const string StopCompleted = "completed";
    public const string StopNotFound = "notFound";
    public const string StopAmbiguous = "ambiguous";
    public const string StopProviderCallLimit = "providerCallLimit";
    public const string StopAdvisoryElapsedLimit = "advisoryElapsedLimit";
    public const string StopTargetUnavailableAfterProbe = "targetUnavailableAfterProbe";
    public const string StopUnsupported = "unsupported";
    public const string StopProviderFailure = "providerFailure";
    public const string StopRealizeFailure = "realizeFailure";
    public const string StopPostconditionFailure = "postconditionFailure";
    public const string StopPostconditionUnverified = "postconditionUnverified";
    public const string StopPostconditionPollLimit = "postconditionPollLimit";
    public const string StopCancelledAfterRealize = "cancelledAfterRealize";
    public const string StopIdentityUnavailable = "identityUnavailable";
    public const string StopIdentityChanged = "identityChanged";
    public const string StopIdentityRecycled = "identityRecycled";
    public const string StopProcessChanged = "processChanged";
    public const string StopWindowChanged = "windowChanged";
    public const string StopRegistrationFailed = "registrationFailed";
}

public sealed record RealizeItemRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementLocator? ContainerLocator = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("containerElementId")] string? ContainerElementId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    int MaxProviderCalls = RealizeItemLimits.DefaultMaxProviderCalls,
    int AdvisoryElapsedLimitMs = RealizeItemLimits.DefaultAdvisoryElapsedLimitMs,
    int PollIntervalMs = RealizeItemLimits.DefaultPollIntervalMs);

public sealed record RealizeItemRequestedIdentity(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

public sealed record RealizeItemResponse(
    RealizeItemRequestedIdentity RequestedIdentity,
    string MethodUsed,
    bool RealizeInvoked,
    bool PostconditionVerified,
    int FindItemByPropertyCalls,
    int PostconditionPolls,
    long ElapsedMs,
    string StopReason,
    bool ViewportMayHaveChanged,
    bool DataOrContainerLoadingMayHaveOccurred,
    bool Reusable,
    long WindowHandleUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecoveryReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementRef? Element = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FailureInfo? Failure = null);
