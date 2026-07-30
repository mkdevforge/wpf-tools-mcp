namespace WpfToolsMcp.Contracts;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed record LaunchAppRequest(
    string ExePath,
    IReadOnlyList<string>? Args = null,
    string? WorkingDirectory = null,
    int WaitForMainWindowMs = 15000,
    bool ReuseExistingInstance = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record LaunchAppResponse(
    string SessionId,
    int Pid,
    string ProcessName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record AttachToAppRequest(
    int? Pid = null,
    string? ProcessName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProcessInstanceId = null);

public sealed record AttachToAppResponse(
    string SessionId,
    int Pid,
    string ProcessName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessInstanceId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttachRecoveryInfo? Recovery { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GetActiveWindowResponse? ActiveWindow { get; init; }
}

public sealed record AttachRecoveryInfo(
    string PreviousSessionId,
    string SuccessorSessionId,
    int PreviousPid,
    bool WindowHandlesInvalidated,
    bool ElementIdsInvalidated,
    bool SubscriptionsCleared);

public sealed record ProcessCandidateInfo(
    int Index,
    string ProcessInstanceId,
    int Pid,
    string ProcessName,
    string StartTimeUtc,
    long MainWindowHandle,
    string MainWindowTitle);

public sealed record ProcessSelectionAmbiguity(
    string Code,
    string RequestedProcessName,
    int DiscoveredCandidates,
    int ReturnedCandidates,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason,
    IReadOnlyList<ProcessCandidateInfo> Candidates,
    string Recovery);

public sealed record InteractionPolicy(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowForegroundActivation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowPhysicalInput = null);

public sealed record InteractionEffects(
    bool Semantic = false,
    bool ForegroundActivated = false,
    bool WindowRestored = false,
    bool MouseInput = false,
    bool KeyboardInput = false,
    bool CursorMoved = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? KeyboardFocusChanged = null);

public sealed record CloseAppRequest(bool Force = false, int TimeoutMs = 5000);

public sealed record CloseAppResponse(
    bool Closed,
    bool SessionRemoved = false,
    bool ProcessExited = false,
    bool ProcessAlreadyExited = false)
{
    public bool CloseRequested { get; init; }

    public bool CloseRequestDispatched { get; init; }

    public bool ForceTerminationRequested { get; init; }

    public bool ForceTerminationAttempted { get; init; }
}

public sealed record DetachSessionResponse(
    int Pid,
    bool SessionRemoved,
    bool ProcessWasRunning,
    bool ProcessStillRunning)
{
    public bool ProcessWasRunningObserved { get; init; }

    public bool ProcessStillRunningObserved { get; init; }
}

public sealed record FailureInfo(
    string Code,
    string Stage,
    string Detail)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Retryable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RecoveryActions { get; init; }
}

public sealed record BackendFallbackInfo(
    string FromBackend,
    string ToBackend,
    bool Attempted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Available,
    bool Used)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureInfo? Failure { get; init; }
}

public sealed record BackendCapabilityState(string Backend, string State)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureInfo? Failure { get; init; }
}

public sealed record SessionInfo(
    string SessionId,
    int Pid,
    string ProcessName,
    long ActiveWindowHandle,
    string ActiveWindowTitle,
    string CreatedAtUtc,
    IReadOnlyList<string> BackendCapabilities,
    IReadOnlyList<BackendCapabilityState> BackendCapabilityStates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record ListSessionsResponse(IReadOnlyList<SessionInfo> Sessions);

public sealed record GetActiveWindowResponse(long Handle, string Title);

public sealed record ListWindowsResponse(int ProcessId, string ProcessName, IReadOnlyList<WindowInfo> Windows);

public sealed record WindowInfo(
    string Title,
    long Handle,
    Rect Bounds,
    bool IsVisible,
    bool IsEnabled)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OwnerHandle { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsModal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrameworkId { get; init; }
}

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BackendFallbackInfo? Fallback { get; init; }
}

public sealed record UiaTreeNode(
    string ControlType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassName,
    string UiaXPath,
    int ChildrenCount,
    IReadOnlyList<UiaTreeNode> Children);

public sealed record GetUiaTreeResponse(
    UiaTreeNode Root,
    int ReturnedNodes,
    int ScannedNodes,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null);

public sealed record ElementSummary(
    string ElementType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    Rect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? XPathOmitted = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ElementPropertiesPreset
{
    Summary,
    Full
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ElementMappingStatus
{
    Exact,
    Heuristic,
    Ambiguous,
    Unmapped
}

public sealed record UiaMappingCandidate(
    string ElementType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    Rect Bounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath = null,
    [property: JsonRequired] int Score = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? XPathOmitted = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ElementId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Reusable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Evidence { get; init; }
}

public sealed record UiaMappingDiagnostics(
    bool Ambiguous,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedXPath = null,
    [property: JsonRequired] IReadOnlyList<UiaMappingCandidate> Candidates = null!,
    int ReturnedCandidates = 0,
    int TotalCandidates = 0,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SelectedXPathOmitted = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ElementMappingStatus? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedElementId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Score { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScoreLead { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Evidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScannedNodes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ScanComplete { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruncatedReason { get; init; }
}

public sealed record GetElementPropertiesResponse(
    ElementSummary Element,
    IReadOnlyDictionary<string, JsonNode?> Properties,
    IReadOnlyDictionary<string, JsonNode?> Patterns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UiaMappingDiagnostics? UiaMapping = null,
    ElementPropertiesPreset Preset = ElementPropertiesPreset.Summary,
    int ReturnedProperties = 0,
    int SelectedProperties = 0,
    int ScannedProperties = 0,
    bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TruncatedReasons = null);

public sealed record WpfLocatorIdentity(
    string? Type,
    string? AutomationId,
    string? Name,
    string? ClassName,
    string? WpfXPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ElementId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Rect? Bounds { get; init; }
}

public sealed record UiaLocatorIdentity(
    string ControlType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    string UiaXPath,
    Rect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HelpText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsControlElement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsContentElement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FlaUiXPath = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ElementId { get; init; }
}

public sealed record UiaLocatorSuggestions(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ByAutomationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ByName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ByClassName,
    string ByControlType,
    string ByXPath,
    string Recommended,
    string RecommendedReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ByFlaUiXPath = null);

public sealed record FlaUiLocatorSnippets(string FindFirst, string FindFirstByXPath);

public sealed record GetUiaLocatorsResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WpfLocatorIdentity? Wpf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UiaLocatorIdentity? Uia = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UiaLocatorSuggestions? LocatorSuggestions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlaUiLocatorSnippets? FlaUi = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UiaMappingDiagnostics? UiaMapping = null);

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
    bool FullyVisible = true,
    bool Annotate = false,
    string AnnotationColor = "#3B82F6",
    int AnnotationThickness = 3,
    string? AnnotationLabel = null,
    bool ReturnBase64 = false)
{
    public bool IncludeViewport { get; init; }

    [JsonIgnore]
    public bool RequireStableElementIdentity { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenshotCorrelationOptions? Correlation { get; init; }
}

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
    string? Base64 = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewportConditions? Viewport { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenshotCorrelationResult? Correlation { get; init; }
}

public sealed record PickElementAtPointRequest(
    int X,
    int Y,
    MouseCoordinateSpace CoordSpace = MouseCoordinateSpace.Screen,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    InspectionBackend Backend = InspectionBackend.Auto,
    bool IncludeAncestors = false,
    int MaxAncestors = 8,
    bool ReturnRootOnMiss = false);

public sealed record PickElementAtPointResponse(
    InspectionBackend BackendUsed,
    ElementRef Element,
    long WindowHandleUsed,
    int XScreen,
    int YScreen,
    MouseCoordinateSpace CoordSpaceUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ElementRef>? Ancestors = null);

public sealed record PickWpfElementAtPointRequest(
    long? WindowHandle = null,
    int X = 0,
    int Y = 0,
    bool IncludeAncestors = false,
    int MaxAncestors = 8,
    bool ReturnRootOnMiss = false,
    FindReturnFields ReturnFields = FindReturnFields.Standard);

public sealed record PickWpfElementAtPointResponse(
    ElementRef Element,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ElementRef>? Ancestors = null);

public sealed record HighlightElementRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    InspectionBackend Backend = InspectionBackend.Auto,
    [property: JsonPropertyName("preferInProcHighlight")] bool PreferInProcHighlight = true,
    int DurationMs = 1500,
    string Color = "#3B82F6",
    int Thickness = 3,
    bool ReturnScreenshot = false,
    ScreenshotCaptureMode ScreenshotCaptureMode = ScreenshotCaptureMode.Auto,
    ScreenshotCaptureArea ScreenshotArea = ScreenshotCaptureArea.Client,
    ScreenshotImageFormat ScreenshotFormat = ScreenshotImageFormat.Png,
    int ScreenshotJpegQuality = 90,
    string? ScreenshotOutputPath = null,
    bool ScreenshotReturnBase64 = false);

public sealed record HighlightElementResponse(
    bool Highlighted,
    Rect Bounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MethodUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TakeScreenshotResponse? Screenshot = null);

public sealed record FocusWindowRequest(
    long? WindowHandle = null,
    string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record FocusWindowResponse(
    bool Focused,
    long Handle,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WindowState
{
    Normal,
    Minimized,
    Maximized
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewportUnit
{
    PhysicalPixels,
    WpfDips
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewportConstraint
{
    DpiRounding,
    WorkAreaClamped,
    MinimumSize,
    MinimumExceedsWorkArea,
    ApplicationConstraint
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DpiAwareness
{
    Unknown,
    Unaware,
    SystemAware,
    PerMonitorAware
}

public sealed record ViewportSize(double Width, double Height);

public sealed record ViewportFrameInsets(int Left, int Top, int Right, int Bottom);

public sealed record ViewportDpi(
    uint WindowDpiX,
    uint WindowDpiY,
    double WindowScaleX,
    double WindowScaleY,
    uint MonitorDpiX,
    uint MonitorDpiY,
    double MonitorScaleX,
    double MonitorScaleY,
    DpiAwareness Awareness);

public sealed record ViewportMonitor(
    string DeviceName,
    Rect BoundsPhysicalPixels,
    Rect WorkAreaPhysicalPixels,
    bool IsPrimary);

public sealed record ViewportConditions(
    Rect ClientBoundsPhysicalPixels,
    Rect OuterBoundsPhysicalPixels,
    ViewportSize ClientSizePhysicalPixels,
    ViewportSize ClientSizeWpfDips,
    ViewportFrameInsets FramePhysicalPixels,
    ViewportDpi Dpi,
    ViewportMonitor Monitor,
    WindowState WindowState);

public sealed record ViewportRequest(
    ViewportUnit Unit,
    ViewportSize ClientSize,
    Rect ClientBoundsPhysicalPixels,
    ViewportSize ClientSizePhysicalPixels,
    ViewportSize ClientSizeWpfDips);

public sealed record ViewportAdjustment(
    ViewportSize AppliedClientSizePhysicalPixels,
    ViewportSize ClientSizeDeltaPhysicalPixels,
    ViewportSize ClientSizeDeltaWpfDips,
    bool ExactMatch,
    bool WasClamped,
    bool MinimumSizeConstrained,
    int ResizeAttempts,
    IReadOnlyList<ViewportConstraint> Constraints);

public sealed record SetWindowViewportRequest(
    double ClientWidth,
    double ClientHeight,
    ViewportUnit Unit = ViewportUnit.PhysicalPixels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    bool ClampToWorkArea = false,
    bool EnsureForeground = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record SetWindowViewportResponse(
    bool Updated,
    long WindowHandleUsed,
    ViewportRequest Requested,
    ViewportConditions Actual,
    ViewportAdjustment Adjustment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

public sealed record SetWindowBoundsRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? X = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Y = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Width = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Height = null,
    bool ClampToVirtualScreen = true,
    bool EnsureForeground = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record SetWindowBoundsResponse(
    bool Updated,
    long WindowHandleUsed,
    Rect PreviousBounds,
    Rect NewBounds,
    bool WasClamped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

public sealed record SetWindowStateRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WindowHandle = null,
    WindowState State = WindowState.Normal,
    bool EnsureForeground = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record SetWindowStateResponse(
    bool Updated,
    long WindowHandleUsed,
    WindowState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

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
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record ClickElementResponse(
    bool Clicked,
    string MethodUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

public sealed record MouseClickRequest(
    int X,
    int Y,
    MouseCoordinateSpace CoordSpace = MouseCoordinateSpace.Screen,
    MouseButtonKind Button = MouseButtonKind.Left,
    MouseClickType ClickType = MouseClickType.Single,
    long? WindowHandle = null,
    bool EnsureForeground = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record MouseClickResponse(
    bool Clicked,
    int XScreen,
    int YScreen,
    MouseCoordinateSpace CoordSpaceUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MethodUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

public sealed record InvokeRequest(
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record InvokeResponse(
    bool Invoked,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MethodUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextEntryMode
{
    Replace,
    Append,
    AtSelection
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardKey
{
    Backspace,
    Tab,
    Enter,
    Escape,
    Space,
    PageUp,
    PageDown,
    End,
    Home,
    ArrowLeft,
    ArrowUp,
    ArrowRight,
    ArrowDown,
    Insert,
    Delete,
    Digit0,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyboardModifier
{
    Shift,
    Control,
    Alt,
    Windows
}

public sealed record KeyStroke(
    [property: JsonRequired] KeyboardKey Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<KeyboardModifier>? Modifiers = null);

public sealed record TypeTextRequest(
    ElementLocator? Locator = null,
    string Text = "",
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TextEntryMode? Mode = null);

public sealed record TypeTextResponse(
    bool Typed,
    string MethodUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null,
    TextEntryMode ModeUsed = TextEntryMode.Replace,
    bool ForegroundFocusRequired = false,
    bool PhysicalInputRequired = false);

public sealed record SendKeysRequest(
    IReadOnlyList<KeyStroke> Sequence,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record SendKeysResponse(
    bool Sent,
    string MethodUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null,
    bool ForegroundFocusRequired = false,
    bool PhysicalInputRequired = false);

public sealed record SetValueRequest(
    ElementLocator? Locator = null,
    double? Value = null,
    string? Text = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    long? WindowHandle = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record SetValueResponse(
    bool Set,
    string MethodUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

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
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record SelectItemResponse(
    bool Selected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MethodUsed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

public sealed record ScrollToElementRequest(
    ElementLocator? Locator = null,
    long? WindowHandle = null,
    ElementLocator? ContainerLocator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    [property: JsonPropertyName("containerElementId")] string? ContainerElementId = null,
    int TimeoutMs = 5000,
    bool AutoWait = true,
    int PollIntervalMs = 100,
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

public sealed record ScrollToElementResponse(
    bool Scrolled,
    string MethodUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

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
    int StableMs = 150,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionPolicy? InteractionPolicy = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaitConditionKind
{
    Attached,
    Visible,
    Enabled,
    Actionable,
    BoundsStable,
    NumericValueEquals,
    NameContains,
    DependencyPropertyValue,
    DataContextValue,
    WindowOpen,
    WindowClosed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaitComparison
{
    Equals,
    NotEquals,
    Contains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaitScalarKind
{
    String,
    Number,
    Boolean,
    Null
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaitScalar(
    WaitScalarKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StringValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? NumberValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BooleanValue = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaitWindowSelector(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Handle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TitleContains = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OwnerHandle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FrameworkId = null);

public sealed record WaitCondition(
    WaitConditionKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PropertyName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataContextPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitComparison? Comparison = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitScalar? Expected = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitWindowSelector? Window = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HoldForMs = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaitBackend
{
    Uia,
    Wpf,
    Win32
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaitObservedValueState
{
    Value,
    Null,
    Unset,
    Unavailable,
    Error
}

public sealed record WaitObservedValue(
    WaitObservedValueState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

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
    bool ThrowOnTimeout = true)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WaitCondition? Condition { get; init; }
}

public sealed record WaitForObservation(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AutomationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? XPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEnabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsOffscreen = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WindowHandle { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OwnerHandle { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrameworkId { get; init; }
}

public sealed record WaitForResponse(
    bool Succeeded,
    string State,
    int ElapsedMs,
    int Attempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitForObservation? LastObservation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureReason = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WaitBackend? BackendUsed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WaitObservedValue? LastObservedValue { get; init; }
}

public sealed record DragResponse(
    bool Dragged,
    string MethodUsed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InteractionEffects? Effects = null);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("elementIdWpf")] string? ElementIdWpf = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsVisible { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsOffscreen { get; init; }
}

public sealed record ResolveElementResponse(
    InspectionBackend BackendUsed,
    ElementRef Element,
    long WindowHandleUsed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BackendFallbackInfo? Fallback { get; init; }
}

public sealed record ResolveElementCandidate(
    int Index,
    ElementRef Element);

public sealed record ResolveElementAmbiguity(
    string Code,
    InspectionBackend BackendUsed,
    long WindowHandleUsed,
    int ReturnedCandidates,
    int DiscoveredCandidates,
    bool Truncated,
    IReadOnlyList<ResolveElementCandidate> Candidates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null);

public sealed record ResolveWpfElementDetailedResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementRef? Element,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResolveElementAmbiguity? Ambiguity);

public sealed record ReleaseElementResponse(bool Released);

public sealed record FindElementsResponse(
    InspectionBackend BackendUsed,
    IReadOnlyList<ElementRef> Matches,
    int ReturnedMatches,
    int ScannedNodes,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null)
{
    public int DiscoveredMatches { get; init; } = ReturnedMatches;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BackendFallbackInfo? Fallback { get; init; }
}

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
    bool IncludeOffViewport = true,
    bool InteractiveOnly = false,
    InteractiveMode InteractiveMode = InteractiveMode.Heuristic,
    int MaxResults = 25,
    int MaxNodes = 1000,
    FindReturnFields ReturnFields = FindReturnFields.Minimal,
    bool IncludeElementIds = true);

public sealed record GetWpfPathRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    string? RootXPath = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = false,
    int MaxNodes = 2000);

public sealed record ResolveWpfElementRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    string? RootXPath = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = true,
    bool InteractiveOnly = false,
    InteractiveMode InteractiveMode = InteractiveMode.Heuristic,
    int MaxNodes = 2000,
    FindReturnFields ReturnFields = FindReturnFields.Minimal);

public sealed record BringIntoViewWpfRequest(
    long WindowHandle,
    string? XPath = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null);

public sealed record BringIntoViewWpfResponse(
    bool BroughtIntoView,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Rect? Bounds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record SetWpfValueRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    string? Text = null,
    double? Value = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = true,
    int MaxNodes = 2000,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TextEntryMode TextMode = TextEntryMode.Replace);

public sealed record FocusWpfElementRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = true,
    int MaxNodes = 2000);

public sealed record FocusWpfElementResponse(
    bool Focused,
    bool KeyboardFocusChanged,
    string MethodUsed);

public sealed record InvokeWpfRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    bool VisibleOnly = true,
    bool IncludeOffViewport = true,
    int MaxNodes = 2000);

public sealed record HighlightWpfElementRequest(
    long? WindowHandle = null,
    ElementLocator? Locator = null,
    [property: JsonPropertyName("elementId")] string? ElementId = null,
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
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    bool IncludeUnbound = false,
    int MaxProperties = 2000,
    string ValueFormat = "string");

public sealed record ReleaseWpfElementRequest(
    [property: JsonPropertyName("elementId")] string ElementId);

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

public sealed record GetValidationErrorsRequest(
    long? WindowHandle = null,
    string? RootXPath = null,
    int Depth = 6,
    bool VisibleOnly = false,
    int MaxErrors = 100,
    int MaxNodes = 2000,
    int MaxValueLength = 500);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationSourceKind
{
    ValidationRule,
    Conversion,
    Exception,
    DataError,
    NotifyDataError,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationBindingKind
{
    Binding,
    MultiBinding,
    PriorityBinding,
    BindingGroup,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationAdornerState
{
    Active,
    NotObserved,
    Unavailable
}

public sealed record ValidationSourceInfo(
    ValidationSourceKind Kind,
    ProvenanceEvidence Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleType = null);

public sealed record ValidationBindingInfo(
    ValidationBindingKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetProperty = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    bool Truncated = false);

public sealed record ValidationErrorContentInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnavailableReason = null);

public sealed record ValidationExceptionInfo(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    bool MessageTruncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MessageUnavailableReason = null);

public sealed record ValidationVisualInfo(
    bool HasError,
    bool ErrorTemplateConfigured,
    ValidationAdornerState AdornerState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AdornerReason = null);

public sealed record WpfValidationErrorInfo(
    ElementRef Element,
    int ErrorIndex,
    ValidationSourceInfo Source,
    ValidationBindingInfo Binding,
    ValidationErrorContentInfo Content,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValidationExceptionInfo? Exception,
    ValidationVisualInfo Visual);

public sealed record GetValidationErrorsResponse(
    InspectionBackend BackendUsed,
    long WindowHandleUsed,
    string RootXPath,
    bool RootXPathTruncated,
    int DepthUsed,
    IReadOnlyList<WpfValidationErrorInfo> Errors,
    int ReturnedErrors,
    int DiscoveredErrors,
    int ScannedNodes,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TruncatedReasons = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null,
    int ReturnedWarnings = 0,
    int DiscoveredWarnings = 0,
    bool WarningsTruncated = false);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubscriptionKind
{
    BindingErrors,
    PropertyChanges
}

public sealed record SubscribeBindingErrorsResponse(
    string SubscriptionId,
    int PollIntervalMs = 0,
    int MaxQueue = 0,
    int MaxPayloadChars = 0);

public sealed record SubscribePropertyChangesResponse(
    string SubscriptionId,
    ElementRef Element,
    IReadOnlyList<ObserveStateWatch> Watches,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int CadenceMs,
    int DurationMs,
    int MaxNodes,
    int MaxQueue,
    int MaxValueLength,
    int MaxPayloadChars);

public sealed record SubscriptionEvent(
    int Sequence,
    string Kind,
    JsonNode Payload)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeEventEnvelope? Envelope { get; init; }
}

public sealed record PollSubscriptionResponse(
    IReadOnlyList<SubscriptionEvent> Events,
    int Dropped,
    bool HasMore,
    int DroppedTotal = 0,
    int Coalesced = 0,
    int CoalescedTotal = 0,
    int Truncated = 0,
    int TruncatedTotal = 0,
    bool Completed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompletionReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompletedAtUtc = null)
{
    public int DroppedSinceLastPoll => Dropped;

    public int CoalescedSinceLastPoll => Coalesced;

    public int TruncatedSinceLastPoll => Truncated;
}

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
    [property: JsonPropertyName("elementId")] string? ElementId = null,
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
    [property: JsonPropertyName("elementId")] string? ElementId = null,
    IReadOnlyList<string>? PropertyNames = null,
    bool IncludeSources = true,
    bool IncludeDefault = false,
    bool IncludeUnset = false,
    int MaxProperties = 500,
    string ValueFormat = "string",
    bool IncludeProvenance = false,
    int MaxProvenanceCandidates = 20);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Converter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DependencyPropertyProvenance? Provenance = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProvenanceEvidenceKind
{
    Exact,
    BestEffort,
    Unavailable
}

public sealed record ProvenanceEvidence(
    ProvenanceEvidenceKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyPropertyBaseValueSource
{
    Unknown,
    Default,
    Inherited,
    DefaultStyle,
    DefaultStyleTrigger,
    Style,
    TemplateTrigger,
    StyleTrigger,
    ImplicitStyleReference,
    ParentTemplate,
    ParentTemplateTrigger,
    Local
}

public sealed record DependencyPropertyValueSourceProvenance(
    DependencyPropertyBaseValueSource BaseValueSource,
    bool IsExpression,
    bool IsAnimated,
    bool IsCoerced,
    bool IsCurrent,
    ProvenanceEvidence Evidence);

public sealed record BindingChildProvenance(
    int Index,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceSummary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataItemSummary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResolvedSourceSummary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResolvedSourcePropertyName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UpdateSourceTrigger = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveUpdateSourceTrigger = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Converter = null,
    string Status = "Unknown",
    bool HasError = false,
    bool HasValidationError = false);

public sealed record BindingProvenance(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceSummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataItemSummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResolvedSourceSummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResolvedSourcePropertyName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UpdateSourceTrigger,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveUpdateSourceTrigger,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Converter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasError,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasValidationError,
    IReadOnlyList<BindingChildProvenance> Children,
    int ReturnedChildren,
    int DiscoveredChildren,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ActiveChildIndex,
    bool ActiveChildOutsideReturnedRange,
    ProvenanceEvidence Evidence);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StyleProvenanceKind
{
    Unknown,
    Explicit,
    Implicit,
    Theme
}

public sealed record PropertyContributorCandidate(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeclaringType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Conditions,
    ProvenanceEvidence Evidence);

public sealed record StylePropertyProvenance(
    StyleProvenanceKind Kind,
    ProvenanceEvidence KindEvidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceKey,
    ProvenanceEvidence ResourceKeyEvidence,
    IReadOnlyList<string> BasedOnTargetTypes,
    IReadOnlyList<PropertyContributorCandidate> Candidates,
    int ReturnedCandidates,
    int DiscoveredCandidates,
    int ScannedDeclarations,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason,
    ProvenanceEvidence ParticipationEvidence,
    ProvenanceEvidence StyleDetailsEvidence,
    ProvenanceEvidence ContributorEvidence);

public sealed record ResourceCandidateProvenance(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key,
    string Scope,
    ProvenanceEvidence Evidence);

public sealed record ResourcePropertyProvenance(
    string ReferenceKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope,
    IReadOnlyList<ResourceCandidateProvenance> Candidates,
    int ReturnedCandidates,
    int DiscoveredCandidates,
    int ScanAttempts,
    int ScannedDictionaries,
    int ScannedEntries,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason,
    ProvenanceEvidence ScanEvidence,
    ProvenanceEvidence KeyEvidence,
    ProvenanceEvidence ScopeEvidence,
    ProvenanceEvidence OriginEvidence);

public sealed record TemplatePropertyProvenance(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TemplateType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TemplatedParentType,
    IReadOnlyList<PropertyContributorCandidate> Candidates,
    int ReturnedCandidates,
    int DiscoveredCandidates,
    int ScannedDeclarations,
    bool ScanComplete,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason,
    ProvenanceEvidence ParticipationEvidence,
    ProvenanceEvidence TemplateDetailsEvidence,
    ProvenanceEvidence ContributorEvidence);

public sealed record InheritancePropertyProvenance(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MetadataInherits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderSummary,
    ProvenanceEvidence ParticipationEvidence,
    ProvenanceEvidence ProviderEvidence);

public sealed record AnimationPropertyProvenance(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseValueType,
    ProvenanceEvidence BaseValueEvidence,
    ProvenanceEvidence OriginEvidence);

public sealed record CoercionPropertyProvenance(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Callback,
    ProvenanceEvidence CallbackEvidence,
    ProvenanceEvidence PreCoercionValueEvidence);

public sealed record DefaultMetadataPropertyProvenance(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultValueType,
    ProvenanceEvidence DefaultValueEvidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MetadataType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsEffectiveValueSource,
    ProvenanceEvidence EffectiveValueSourceEvidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Inherits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BindsTwoWayByDefault,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultUpdateSourceTrigger,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsAnimationProhibited,
    ProvenanceEvidence Evidence);

public sealed record DependencyPropertyProvenance(
    DependencyPropertyValueSourceProvenance ValueSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindingProvenance? Binding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StylePropertyProvenance? Style,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResourcePropertyProvenance? Resource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TemplatePropertyProvenance? Template,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InheritancePropertyProvenance? Inheritance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnimationPropertyProvenance? Animation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CoercionPropertyProvenance? Coercion,
    DefaultMetadataPropertyProvenance DefaultMetadata);

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
    [property: JsonPropertyName("elementId")] string? ElementId = null,
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
    [property: JsonPropertyName("elementId")] string? ElementId = null,
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
    double MinLatencyMs,
    double P50LatencyMs,
    double P95LatencyMs,
    double P99LatencyMs,
    double MaxLatencyMs);

public sealed record PerformanceStopResponse(PerformanceSummary Summary);

public sealed record TraceEvent(
    string Tool,
    DateTime StartedAtUtc,
    int DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeEventEnvelope? Envelope { get; init; }
}

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
    int ReturnedEventCount,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TruncatedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TraceEvent>? Events = null)
{
    public long ObservedEventCount { get; init; }

    public int RetainedEventCount { get; init; }

    public long DroppedEventCount { get; init; }

    public int RetentionLimit { get; init; }

    public bool RetentionTruncated { get; init; }
}
