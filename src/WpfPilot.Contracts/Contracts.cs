namespace WpfPilot.Contracts;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed record LaunchAppRequest(
    string ExePath,
    IReadOnlyList<string>? Args = null,
    string? WorkingDirectory = null,
    int WaitForMainWindowMs = 15000,
    bool ReuseExistingInstance = true);

public sealed record LaunchAppResponse(string SessionId, int Pid, string ProcessName);

public sealed record AttachToAppRequest(int? Pid = null, string? ProcessName = null);

public sealed record AttachToAppResponse(string SessionId, int Pid, string ProcessName);

public sealed record CloseAppRequest(bool Force = false, int TimeoutMs = 5000);

public sealed record CloseAppResponse(bool Closed);

public sealed record SessionInfo(
    string SessionId,
    int Pid,
    string ProcessName,
    long ActiveWindowHandle,
    string ActiveWindowTitle,
    string CreatedAtUtc,
    IReadOnlyList<string> BackendCapabilities);

public sealed record ListSessionsResponse(IReadOnlyList<SessionInfo> Sessions);

public sealed record GetActiveWindowResponse(long Handle, string Title);

public sealed record ListWindowsResponse(int ProcessId, string ProcessName, IReadOnlyList<WindowInfo> Windows);

public sealed record WindowInfo(
    string Title,
    long Handle,
    Rect Bounds,
    bool IsVisible,
    bool IsEnabled);

public sealed record Rect(int X, int Y, int Width, int Height);

public sealed record ElementLocator(
    [property: JsonPropertyName("automationId")] string? AutomationId = null,
    [property: JsonPropertyName("automationIdContains")] string? AutomationIdContains = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("nameContains")] string? NameContains = null,
    [property: JsonPropertyName("className")] string? ClassName = null,
    [property: JsonPropertyName("classNameContains")] string? ClassNameContains = null,
    [property: JsonPropertyName("typeEquals")] string? TypeEquals = null,
    [property: JsonPropertyName("controlTypeEquals")] string? ControlTypeEquals = null,
    [property: JsonPropertyName("xpath")] string? XPath = null,
    [property: JsonPropertyName("index")] int? Index = null,
    [property: JsonPropertyName("preferVisible")] bool PreferVisible = true,
    [property: JsonPropertyName("strict")] bool Strict = true);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InspectionBackend
{
    Auto,
    Uia,
    Wpf
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TreePreset
{
    Minimal,
    Standard,
    Debug
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractiveMode
{
    Heuristic,
    Patterns
}

public sealed record TreeNode(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    string XPath,
    int ChildrenCount,
    IReadOnlyList<TreeNode> Children,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsOffscreen = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Visibility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsVisible = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataContextType = null);

public sealed record GetVisualTreeResponse(
    InspectionBackend BackendUsed,
    TreeNode Root,
    int ReturnedNodes,
    int ScannedNodes,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

public sealed record ElementSummary(
    string ElementType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    Rect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    string XPath);

public sealed record GetElementPropertiesResponse(
    ElementSummary Element,
    IReadOnlyDictionary<string, JsonNode?> Properties,
    IReadOnlyDictionary<string, JsonNode?> Patterns);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotCaptureMode
{
    Screen,
    PrintWindow,
    Auto
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotImageFormat
{
    Png,
    Jpeg
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotCaptureArea
{
    Client,
    Window
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotClipMode
{
    None,
    Intersect
}

public sealed record TakeScreenshotRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    InspectionBackend Backend = InspectionBackend.Auto,
    ScreenshotCaptureMode CaptureMode = ScreenshotCaptureMode.Auto,
    ScreenshotCaptureArea Area = ScreenshotCaptureArea.Client,
    ScreenshotClipMode Clip = ScreenshotClipMode.Intersect,
    ScreenshotImageFormat Format = ScreenshotImageFormat.Png,
    int JpegQuality = 90,
    string? OutputPath = null,
    bool IncludeOverlay = false,
    bool AutoScroll = true,
    bool ReturnBase64 = false);

public sealed record TakeScreenshotResponse(
    string Path,
    int Width,
    int Height,
    string Format,
    Rect CapturedBounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? RequestedBounds,
    bool WasClipped,
    long WindowHandleUsed,
    ScreenshotCaptureMode CaptureModeUsed,
    string? Base64 = null);

public sealed record PickElementAtPointRequest(
    int X,
    int Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    InspectionBackend Backend = InspectionBackend.Auto,
    bool IncludeAncestors = false,
    int MaxAncestors = 8);

public sealed record PickElementAtPointResponse(
    InspectionBackend BackendUsed,
    ElementRef Element,
    long WindowHandleUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ElementRef>? Ancestors = null);

public sealed record PickWpfElementAtPointRequest(
    long? WindowHandle = null,
    int X = 0,
    int Y = 0,
    bool IncludeAncestors = false,
    int MaxAncestors = 8,
    FindReturnFields ReturnFields = FindReturnFields.Standard);

public sealed record PickWpfElementAtPointResponse(
    ElementRef Element,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ElementRef>? Ancestors = null);

public sealed record HighlightElementRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    InspectionBackend Backend = InspectionBackend.Auto,
    int DurationMs = 1500,
    string Color = "#3B82F6",
    int Thickness = 3);

public sealed record HighlightElementResponse(
    bool Highlighted,
    Rect Bounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MethodUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed record FocusWindowRequest(long? WindowHandle = null, string? Title = null);

public sealed record FocusWindowResponse(bool Focused, long Handle, string Title);

public sealed record DisplayInfo(
    string DeviceName,
    Rect Bounds,
    bool IsPrimary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? WorkArea = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpiScaleX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpiScaleY = null);

public sealed record ListDisplaysResponse(
    Rect VirtualScreen,
    IReadOnlyList<DisplayInfo> Displays);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickType
{
    Single,
    Double,
    Right
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClickMode
{
    Auto,
    MouseAlways,
    InvokePreferred
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseCoordinateSpace
{
    Screen,
    Client
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseButtonKind
{
    Left,
    Right,
    Middle
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseClickType
{
    Single,
    Double
}

public sealed record ClickElementRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    ClickType ClickType = ClickType.Single,
    ClickMode ClickMode = ClickMode.Auto,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record ClickElementResponse(bool Clicked, string MethodUsed);

public sealed record MouseClickRequest(
    int X,
    int Y,
    MouseCoordinateSpace CoordSpace = MouseCoordinateSpace.Screen,
    MouseButtonKind Button = MouseButtonKind.Left,
    MouseClickType ClickType = MouseClickType.Single,
    long? WindowHandle = null,
    bool EnsureForeground = true);

public sealed record MouseClickResponse(
    bool Clicked,
    int XScreen,
    int YScreen,
    MouseCoordinateSpace CoordSpaceUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed record InvokeRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record InvokeResponse(bool Invoked);

public sealed record TypeTextRequest(
    ElementLocator? Locator = null,
    string Text = "",
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record TypeTextResponse(bool Typed, string MethodUsed);

public sealed record SetValueRequest(
    ElementLocator? Locator = null,
    double Value = 0,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record SetValueResponse(bool Set, string MethodUsed);

public sealed record SelectItemRequest(
    ElementLocator? Locator = null,
    string? Text = null,
    int? Index = null,
    long? WindowHandle = null,
    ElementLocator? ItemLocator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonPropertyName("itemElementId")] string? ItemElementId = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record SelectItemResponse(bool Selected);

public sealed record ScrollToElementRequest(
    ElementLocator? Locator = null,
    long? WindowHandle = null,
    ElementLocator? ContainerLocator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonPropertyName("containerElementId")] string? ContainerElementId = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record ScrollToElementResponse(bool Scrolled, string MethodUsed);

public sealed record DragRequest(
    ElementLocator? Locator = null,
    long? WindowHandle = null,
    ElementLocator? TargetLocator = null,
    int? ToX = null,
    int? ToY = null,
    int Steps = 20,
    string? Button = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonPropertyName("targetElementId")] string? TargetElementId = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150);

public sealed record WaitForRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    InspectionBackend Backend = InspectionBackend.Auto,
    string State = "visible",
    int TimeoutMs = 5000,
    int PollIntervalMs = 100,
    int StableMs = 250,
    double? ExpectedValue = null,
    string? ExpectedText = null,
    bool ThrowOnTimeout = true);

public sealed record WaitForObservation(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsOffscreen = null);

public sealed record WaitForResponse(
    bool Succeeded,
    string State,
    int ElapsedMs,
    int Attempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitForObservation? LastObservation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureReason = null);

public sealed record DragResponse(bool Dragged, string MethodUsed);

public sealed record FindElementsQuery(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationIdEquals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationIdContains = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NameEquals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NameContains = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TypeEquals = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindReturnFields
{
    Minimal,
    Standard
}

public sealed record ElementRef(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    string XPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("elementIdUia")] string? ElementIdUia = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("elementIdWpf")] string? ElementIdWpf = null);

public sealed record ResolveElementResponse(
    InspectionBackend BackendUsed,
    ElementRef Element,
    long WindowHandleUsed);

public sealed record ReleaseElementResponse(bool Released);

public sealed record FindElementsResponse(
    InspectionBackend BackendUsed,
    IReadOnlyList<ElementRef> Matches,
    int ReturnedMatches,
    int ScannedNodes,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

public sealed record GetPathToElementResponse(
    InspectionBackend BackendUsed,
    string XPath);

// Phase 2 (Snoop agent)

public sealed record InjectAgentResponse(bool Injected, string PipeName);

public sealed record AgentPingResponse(string Message);

public sealed record GetWpfVisualTreeRequestV2(
    long? WindowHandle = null,
    string? RootXPath = null,
    int Depth = 4,
    int MaxNodes = 500,
    bool VisibleOnly = true,
    bool IncludeOffViewport = false,
    bool InteractiveOnly = false,
    InteractiveMode InteractiveMode = InteractiveMode.Heuristic,
    TreePreset Preset = TreePreset.Minimal,
    IReadOnlyList<string>? Fields = null);

public sealed record FindElementsWpfRequest(
    long? WindowHandle = null,
    string? RootXPath = null,
    FindElementsQuery? Query = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = false,
    bool InteractiveOnly = false,
    InteractiveMode InteractiveMode = InteractiveMode.Heuristic,
    int MaxResults = 25,
    int MaxNodes = 1000,
    FindReturnFields ReturnFields = FindReturnFields.Minimal);

public sealed record GetWpfPathRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    string? RootXPath = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = false,
    int MaxNodes = 2000);

public sealed record ResolveWpfElementRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    string? RootXPath = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = false,
    bool InteractiveOnly = false,
    InteractiveMode InteractiveMode = InteractiveMode.Heuristic,
    int MaxNodes = 2000,
    FindReturnFields ReturnFields = FindReturnFields.Minimal);

public sealed record BringIntoViewWpfRequest(
    long WindowHandle,
    string XPath);

public sealed record BringIntoViewWpfResponse(
    bool BroughtIntoView,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record HighlightWpfElementRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    string? RootXPath = null,
    int DurationMs = 1500,
    string Color = "#3B82F6",
    int Thickness = 3);

public sealed record HighlightWpfElementResponse(
    bool Highlighted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record GetBindingInfoRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    bool IncludeUnbound = false,
    int MaxProperties = 2000,
    string ValueFormat = "string");

public sealed record BindingInfo(
    string TargetProperty,
    string BindingKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UpdateSourceTrigger = null,
    string Status = "Unknown",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrentValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Converter = null);

public sealed record GetBindingInfoResponse(
    ElementRef Element,
    IReadOnlyList<BindingInfo> Bindings,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null);

public sealed record GetBindingErrorsRequest(
    long? WindowHandle = null,
    string? RootXPath = null,
    int Depth = 6,
    int MaxErrors = 200,
    int MaxNodes = 2000);

public sealed record BindingErrorInfo(
    string ElementXPath,
    string ElementType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ElementName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId = null,
    string TargetProperty = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage = null,
    string Status = "Unknown");

public sealed record GetBindingErrorsResponse(
    IReadOnlyList<BindingErrorInfo> Errors,
    int ScannedNodes,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubscriptionKind
{
    BindingErrors
}

public sealed record SubscribeBindingErrorsResponse(string SubscriptionId);

public sealed record SubscriptionEvent(
    int Sequence,
    string Kind,
    JsonNode Payload);

public sealed record PollSubscriptionResponse(
    IReadOnlyList<SubscriptionEvent> Events,
    int Dropped,
    bool HasMore);

public sealed record UnsubscribeResponse(bool Unsubscribed);

public sealed record GetUiaCoverageReportRequest(
    long? WindowHandle = null,
    string? RootXPath = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = false,
    bool InteractiveOnly = true,
    InteractiveMode InteractiveMode = InteractiveMode.Heuristic,
    int MaxNodes = 5000,
    int MaxFindings = 200);

public sealed record UiaCoverageIssueCount(string IssueCode, int Count);

public sealed record UiaCoverageSummary(
    int ScannedNodes,
    int ConsideredNodes,
    int FindingsCount,
    IReadOnlyList<UiaCoverageIssueCount> IssueCounts,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null);

public sealed record UiaCoverageFinding(
    string IssueCode,
    string Severity,
    ElementRef Element,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Suggestions);

public sealed record GetUiaCoverageReportResponse(
    UiaCoverageSummary Summary,
    IReadOnlyList<UiaCoverageFinding> Findings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

public sealed record GetDataContextRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    DataContextMode Mode = DataContextMode.Summary,
    int MaxDepth = 2,
    int MaxPropertiesPerObject = 50,
    int MaxStringLength = 2000,
    bool IncludeNulls = false,
    bool IncludeFrameworkProperties = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? PropertyAllowList = null);

public sealed record GetDataContextResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataContextType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary = null,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataContextMode
{
    Summary,
    Full
}

// Milestone 4 (computed properties / style / template)

public sealed record GetComputedPropertiesRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    IReadOnlyList<string>? PropertyNames = null,
    bool IncludeSources = true,
    bool IncludeDefault = false,
    bool IncludeUnset = false,
    int MaxProperties = 500,
    string ValueFormat = "string");

public sealed record ComputedPropertyInfo(
    string Name,
    string OwnerType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsBinding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BindingKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UpdateSourceTrigger = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Converter = null);

public sealed record GetComputedPropertiesResponse(
    ElementRef Element,
    IReadOnlyList<ComputedPropertyInfo> Properties,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MissingPropertyNames = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StyleChainKind
{
    LocalStyle,
    ImplicitStyle,
    ThemeStyle
}

public sealed record GetStyleChainRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    bool IncludeThemeStyle = true,
    bool IncludeResourceKeys = false,
    int MaxBasedOnDepth = 10);

public sealed record StyleChainEntry
{
    public required StyleChainKind Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceKey { get; init; }

    public IReadOnlyList<string> BasedOnChainTargetTypes { get; init; } = Array.Empty<string>();

    public int SettersCount { get; init; }

    public int TriggersCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StylePropertyValueSource { get; init; }
}

public sealed record GetStyleChainResponse(
    ElementRef Element,
    IReadOnlyList<StyleChainEntry> Styles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TemplateKind
{
    None,
    ControlTemplate,
    DataTemplate,
    ItemsPanelTemplate,
    FrameworkTemplate
}

public sealed record GetTemplateInfoRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    bool IncludeNamedElements = false,
    int MaxNamedElements = 50,
    bool IncludeResourceKeys = false,
    bool IncludePartElementRefs = false);

public sealed record TemplatePartInfo(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedType = null,
    bool Found = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActualType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null);

public sealed record NamedTemplateElementInfo(
    string Name,
    string Type);

public sealed record TemplateInfo(
    TemplateKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TemplateType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceKey = null,
    int TriggersCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TemplatePartInfo>? TemplateParts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NamedTemplateElementInfo>? NamedElements = null);

public sealed record GetTemplateInfoResponse(
    ElementRef Element,
    TemplateInfo Template,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

public sealed record PerformanceStartRequest(
    int ProbeIntervalMs = 50,
    int AutoStopAfterMs = 30000,
    bool ResetIfRunning = false);

public sealed record PerformanceStartResponse(
    string RunId,
    DateTime StartedAtUtc,
    int ProbeIntervalMs,
    int AutoStopAfterMs);

public sealed record PerformanceStopRequest(string RunId);

public sealed record PerformanceSummary(
    string RunId,
    DateTime StartedAtUtc,
    DateTime StoppedAtUtc,
    int ProbeIntervalMs,
    int SampleCount,
    int DroppedProbeCount,
    int MinLatencyMs,
    int P50LatencyMs,
    int P95LatencyMs,
    int P99LatencyMs,
    int MaxLatencyMs);

public sealed record PerformanceStopResponse(PerformanceSummary Summary);

public sealed record TraceEvent(
    string Tool,
    DateTime StartedAtUtc,
    int DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed record TraceStartResponse(
    string TraceId,
    DateTime StartedAtUtc,
    bool Started,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

public sealed record TraceStopResponse(
    string TraceId,
    DateTime StoppedAtUtc,
    string OutputPath,
    int EventCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TraceEvent>? Events = null);
