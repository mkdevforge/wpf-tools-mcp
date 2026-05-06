using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "get_visual_tree"), Description("Return an inspection tree (UIA or WPF) for the main window or a subtree.")]
    public static Task<GetVisualTreeResponse> GetVisualTree(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional root locator for subtree")] ElementLocator? root = null,
        [Description("Maximum depth (1 = root only)")] int depth = 4,
        [Description("Maximum number of nodes returned")] int maxNodes = 500,
        [Description("Filter to visible elements only")] bool visibleOnly = true,
        [Description("Include off-viewport elements even when visibleOnly=true")] bool includeOffViewport = false,
        [Description("Filter to interactive elements only")] bool interactiveOnly = false,
        [Description("Interactive filtering mode")] InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        [Description("Response verbosity preset")] TreePreset preset = TreePreset.Minimal,
        [Description("Optional field allowlist overriding preset")] IReadOnlyList<string>? fields = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetVisualTreeAsync(
                    backend,
                    effectiveWindowHandle,
                    root,
                    depth,
                    maxNodes,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    preset,
                    fields,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_element_properties"), Description("Return all UIA properties and supported patterns for a single element.")]
    public static Task<GetElementPropertiesResponse> GetElementProperties(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var target = ElementTarget.Parse(
                locator,
                elementId,
                windowHandle,
                operationName: "get_element_properties");
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var requestTarget = target.WithInheritedWindowHandle(effectiveWindowHandle);

            return automation.RunExclusiveAsync(
                () => automation.GetElementPropertiesAsync(
                    requestTarget.Locator,
                    requestTarget.ElementId,
                    requestTarget.WindowHandle,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_uia_locators"), Description("Return UIA locator suggestions and FlaUI snippets for a WPF or UIA element.")]
    public static Task<GetUiaLocatorsResponse> GetUiaLocators(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var target = ElementTarget.Parse(
                locator,
                elementId,
                windowHandle,
                operationName: "get_uia_locators");
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var requestTarget = target.WithInheritedWindowHandle(effectiveWindowHandle);

            return automation.RunExclusiveAsync(
                () => automation.GetUiaLocatorsAsync(
                    requestTarget.Locator,
                    requestTarget.ElementId,
                    requestTarget.WindowHandle,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_uia_tree"), Description("Return a bounded UIA automation tree for a window or subtree.")]
    public static Task<GetUiaTreeResponse> GetUiaTree(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional root locator for subtree")] ElementLocator? root = null,
        [Description("Maximum depth (1 = root only)")] int depth = 4,
        [Description("Maximum number of nodes returned")] int maxNodes = 200,
        [Description("Filter to visible elements only")] bool visibleOnly = true,
        [Description("Include off-viewport elements even when visibleOnly=true")] bool includeOffViewport = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetUiaTreeAsync(effectiveWindowHandle, root, depth, maxNodes, visibleOnly, includeOffViewport, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "find_elements"), Description("Find elements without dumping the full tree.")]
    public static Task<FindElementsResponse> FindElements(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional root locator for subtree")] ElementLocator? root = null,
        [Description("Search query")] FindElementsQuery? query = null,
        [Description("Filter to visible elements only")] bool visibleOnly = true,
        [Description("Include off-viewport elements even when visibleOnly=true")] bool includeOffViewport = true,
        [Description("Filter to interactive elements only")] bool interactiveOnly = false,
        [Description("Interactive filtering mode")] InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        [Description("Maximum number of matches returned")] int maxResults = 25,
        [Description("Maximum number of nodes scanned")] int maxNodes = 5000,
        [Description("Match verbosity preset")] FindReturnFields returnFields = FindReturnFields.Minimal,
        [Description("Include element IDs in results (recommended)")] bool includeElementIds = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.FindElementsAsync(
                    backend,
                    effectiveWindowHandle,
                    root,
                    query,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    maxResults,
                    maxNodes,
                    returnFields,
                    includeElementIds,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_path_to_element"), Description("Get the XPath for a resolved element (UIA or WPF).")]
    public static Task<GetPathToElementResponse> GetPathToElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Backend used to resolve the locator (ignored when elementId is set)")] InspectionBackend backend = InspectionBackend.Uia,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var target = ElementTarget.Parse(
                locator,
                elementId,
                windowHandle,
                operationName: "get_path_to_element");
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var requestTarget = target.WithInheritedWindowHandle(effectiveWindowHandle);

            return automation.RunExclusiveAsync(
                () => automation.GetPathToElementAsync(
                    backend,
                    requestTarget.Locator,
                    requestTarget.ElementId,
                    requestTarget.WindowHandle,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "resolve_element"), Description("Resolve an element and return an elementId handle for re-use.")]
    public static Task<ResolveElementResponse> ResolveElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator locator,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 0,
        [Description("Filter to visible elements only")] bool visibleOnly = true,
        [Description("Include off-viewport elements even when visibleOnly=true")] bool includeOffViewport = true,
        [Description("Filter to interactive elements only")] bool interactiveOnly = false,
        [Description("Interactive filtering mode")] InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.ResolveElementAsync(
                    backend,
                    locator,
                    effectiveWindowHandle,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "release_element"), Description("Release an elementId handle.")]
    public static Task<ReleaseElementResponse> ReleaseElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element ID")] string elementId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.ReleaseElementAsync(elementId), cancellationToken);
        });

    [McpServerTool(Name = "pick_element_at_point"), Description("Pick an element at a coordinate (UIA or WPF).")]
    public static Task<PickElementAtPointResponse> PickElementAtPoint(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("X coordinate (pixels)")] int x,
        [Description("Y coordinate (pixels)")] int y,
        [Description("Coordinate space: screen | client")] MouseCoordinateSpace coordSpace = MouseCoordinateSpace.Screen,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Include ancestor chain in response")] bool includeAncestors = false,
        [Description("Maximum number of ancestors returned")] int maxAncestors = 8,
        [Description("Return the containing window/root when no deeper element is hit (defaults to false)")] bool returnRootOnMiss = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.PickElementAtPointAsync(
                    new PickElementAtPointRequest(
                        X: x,
                        Y: y,
                        CoordSpace: coordSpace,
                        WindowHandle: coordSpace == MouseCoordinateSpace.Client ? effectiveWindowHandle : windowHandle,
                        Backend: backend,
                        IncludeAncestors: includeAncestors,
                        MaxAncestors: maxAncestors,
                        ReturnRootOnMiss: returnRootOnMiss),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "highlight_element"), Description("Highlight an element on-screen (locator or elementId).")]
    public static Task<HighlightElementResponse> HighlightElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Prefer in-proc WPF highlighting when the agent is available")] bool preferInProcHighlight = true,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Highlight duration (ms)")] int durationMs = 1500,
        [Description("Stroke color (e.g. #3B82F6)")] string color = "#3B82F6",
        [Description("Stroke thickness (px)")] int thickness = 3,
        [Description("Capture and return an annotated screenshot (defaults to false)")] bool returnScreenshot = false,
        [Description("Screenshot capture mode")] ScreenshotCaptureMode screenshotCaptureMode = ScreenshotCaptureMode.Auto,
        [Description("Screenshot capture area")] ScreenshotCaptureArea screenshotArea = ScreenshotCaptureArea.Client,
        [Description("Screenshot image format")] ScreenshotImageFormat screenshotFormat = ScreenshotImageFormat.Png,
        [Description("JPEG quality 1-100 (only used when screenshotFormat=jpeg)")] int screenshotJpegQuality = 90,
        [Description("Optional output file path (auto-generated when omitted)")] string? screenshotOutputPath = null,
        [Description("Include base64 payload in screenshot response (defaults to false)")] bool screenshotReturnBase64 = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.HighlightElementAsync(
                    new HighlightElementRequest(
                        Locator: locator,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        Backend: backend,
                        PreferInProcHighlight: preferInProcHighlight,
                        DurationMs: durationMs,
                        Color: color,
                        Thickness: thickness,
                        ReturnScreenshot: returnScreenshot,
                        ScreenshotCaptureMode: screenshotCaptureMode,
                        ScreenshotArea: screenshotArea,
                        ScreenshotFormat: screenshotFormat,
                        ScreenshotJpegQuality: screenshotJpegQuality,
                        ScreenshotOutputPath: screenshotOutputPath,
                        ScreenshotReturnBase64: screenshotReturnBase64),
                    cancellationToken),
                cancellationToken);
        });
}
