using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

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
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetElementPropertiesAsync(locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, cancellationToken),
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
        [Description("Filter to interactive elements only")] bool interactiveOnly = false,
        [Description("Interactive filtering mode")] InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        [Description("Maximum number of matches returned")] int maxResults = 25,
        [Description("Maximum number of nodes scanned")] int maxNodes = 1000,
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
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetPathToElementAsync(backend, locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "resolve_element"), Description("Resolve an element and return an elementId handle for re-use.")]
    public static Task<ResolveElementResponse> ResolveElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator locator,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Uia,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.ResolveElementAsync(backend, locator, effectiveWindowHandle, cancellationToken),
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

    [McpServerTool(Name = "pick_element_at_point"), Description("Pick an element at a screen coordinate (UIA or WPF).")]
    public static Task<PickElementAtPointResponse> PickElementAtPoint(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Screen X coordinate (pixels)")] int x,
        [Description("Screen Y coordinate (pixels)")] int y,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Include ancestor chain in response")] bool includeAncestors = false,
        [Description("Maximum number of ancestors returned")] int maxAncestors = 8,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.PickElementAtPointAsync(
                    new PickElementAtPointRequest(
                        X: x,
                        Y: y,
                        WindowHandle: effectiveWindowHandle,
                        Backend: backend,
                        IncludeAncestors: includeAncestors,
                        MaxAncestors: maxAncestors),
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
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Highlight duration (ms)")] int durationMs = 1500,
        [Description("Stroke color (e.g. #3B82F6)")] string color = "#3B82F6",
        [Description("Stroke thickness (px)")] int thickness = 3,
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
                        DurationMs: durationMs,
                        Color: color,
                        Thickness: thickness),
                    cancellationToken),
                cancellationToken);
        });
}
