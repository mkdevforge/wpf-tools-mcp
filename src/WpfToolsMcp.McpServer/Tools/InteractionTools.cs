using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class InteractionTools
{
    [McpServerTool(Name = "set_active_window"), Description("Bring a window to the foreground and set it as the active window for this session.")]
    public static Task<FocusWindowResponse> SetActiveWindow(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Window title (exact match first, then contains)")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle is not null && !string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Provide either windowHandle or title, not both.");
        }

        return McpToolErrors.RunAsync(() =>
            sessions.SetActiveWindowAsync(sessionId, new FocusWindowRequest(windowHandle, title), cancellationToken));
    }

    [McpServerTool(Name = "get_active_window"), Description("Get the active window for this session.")]
    public static Task<GetActiveWindowResponse> GetActiveWindow(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => sessions.GetActiveWindowAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "set_window_bounds"), Description("Move/resize a window by setting its bounds (outer window rectangle).")]
    public static Task<SetWindowBoundsResponse> SetWindowBounds(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("X screen coordinate (pixels)")] int? x = null,
        [Description("Y screen coordinate (pixels)")] int? y = null,
        [Description("Width (pixels)")] int? width = null,
        [Description("Height (pixels)")] int? height = null,
        [Description("Clamp the resulting bounds to the virtual screen")] bool clampToVirtualScreen = true,
        [Description("Bring window to foreground first")] bool ensureForeground = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.SetWindowBoundsAsync(
                    new SetWindowBoundsRequest(
                        WindowHandle: effectiveWindowHandle,
                        X: x,
                        Y: y,
                        Width: width,
                        Height: height,
                        ClampToVirtualScreen: clampToVirtualScreen,
                        EnsureForeground: ensureForeground),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_window_state"), Description("Set a window state (normal/minimized/maximized).")]
    public static Task<SetWindowStateResponse> SetWindowState(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Target window state")] WindowState state = WindowState.Normal,
        [Description("Bring window to foreground first")] bool ensureForeground = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.SetWindowStateAsync(
                    new SetWindowStateRequest(
                        WindowHandle: effectiveWindowHandle,
                        State: state,
                        EnsureForeground: ensureForeground),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "click_element"), Description("Click an element by locator or elementId.")]
    public static Task<ClickElementResponse> ClickElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Click type: single | double | right")] string? clickType = null,
        [Description("Click mode: auto | mouseAlways | invokePreferred")] string? clickMode = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var target = ElementTarget.Parse(
                locator,
                elementId,
                windowHandle,
                operationName: "click_element");
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var requestTarget = target.WithInheritedWindowHandle(effectiveWindowHandle);

            return automation.RunExclusiveAsync(
                () => automation.ClickElementAsync(
                    new ClickElementRequest(
                        Locator: requestTarget.Locator,
                        ElementId: requestTarget.ElementId,
                        WindowHandle: requestTarget.WindowHandle,
                        ClickType: ParseClickType(clickType),
                        ClickMode: ParseClickMode(clickMode),
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "mouse_click"), Description("Click at a coordinate (Playwright-style).")]
    public static Task<MouseClickResponse> MouseClick(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("X coordinate (pixels)")] int x,
        [Description("Y coordinate (pixels)")] int y,
        [Description("Coordinate space: screen | client")] MouseCoordinateSpace coordSpace = MouseCoordinateSpace.Screen,
        [Description("Mouse button: left | right | middle")] MouseButtonKind button = MouseButtonKind.Left,
        [Description("Click type: single | double")] MouseClickType clickType = MouseClickType.Single,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Bring window to foreground first")] bool ensureForeground = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.MouseClickAsync(
                    new MouseClickRequest(
                        X: x,
                        Y: y,
                        CoordSpace: coordSpace,
                        Button: button,
                        ClickType: clickType,
                        WindowHandle: effectiveWindowHandle,
                        EnsureForeground: ensureForeground),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "invoke"), Description("Invoke an element via InvokePattern (locator or elementId).")]
    public static Task<InvokeResponse> Invoke(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.InvokeAsync(
                    new InvokeRequest(
                        Locator: locator,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "type_text"), Description("Type text into the focused element, or into a specified locator/elementId.")]
    public static Task<TypeTextResponse> TypeText(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Text to enter")] string text,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.TypeTextAsync(
                    new TypeTextRequest(
                        Locator: locator,
                        Text: text,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_value"), Description("Set a numeric or text value by locator or elementId.")]
    public static Task<SetValueResponse> SetValue(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Numeric value to set for range/numeric controls")] double? value = null,
        [Description("Text value to set for string-valued controls")] string? text = null,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.SetValueAsync(
                    new SetValueRequest(
                        Locator: locator,
                        Value: value,
                        Text: text,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "select_item"), Description("Select an item in a combo box, list box, or tab control (locator or elementId).")]
    public static Task<SelectItemResponse> SelectItem(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Item text to select")] string? text = null,
        [Description("Item index to select (0-based)")] int? index = null,
        [Description("Optional item locator (select a specific item element)")] ElementLocator? itemLocator = null,
        [Description("Optional item elementId (select a specific item element)")] string? itemElementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.SelectItemAsync(
                    new SelectItemRequest(
                        Locator: locator,
                        Text: text,
                        Index: index,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        ItemLocator: itemLocator,
                        ElementId: elementId,
                        ItemElementId: itemElementId,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "scroll_to_element"), Description("Scroll a container to bring an element into view (locator or elementId).")]
    public static Task<ScrollToElementResponse> ScrollToElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Target element locator")] ElementLocator? locator = null,
        [Description("Target elementId (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional container locator (preferred scroll root)")] ElementLocator? containerLocator = null,
        [Description("Optional container elementId (preferred scroll root)")] string? containerElementId = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasAnyElementId = !string.IsNullOrWhiteSpace(elementId) || !string.IsNullOrWhiteSpace(containerElementId);
            return automation.RunExclusiveAsync(
                () => automation.ScrollToElementAsync(
                    new ScrollToElementRequest(
                        Locator: locator,
                        WindowHandle: hasAnyElementId ? windowHandle : effectiveWindowHandle,
                        ContainerLocator: containerLocator,
                        ElementId: elementId,
                        ContainerElementId: containerElementId,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "drag"), Description("Drag from an element to another element or to screen coordinates (locator or elementId).")]
    public static Task<DragResponse> Drag(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Source element locator")] ElementLocator? locator = null,
        [Description("Source elementId (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional target element locator")] ElementLocator? targetLocator = null,
        [Description("Optional target elementId (from resolve_element / find_elements)")] string? targetElementId = null,
        [Description("Target X screen coordinate (required if targetLocator is not set)")] int? toX = null,
        [Description("Target Y screen coordinate (required if targetLocator is not set)")] int? toY = null,
        [Description("Number of mouse move steps")] int steps = 20,
        [Description("Mouse button: left | right | middle")] string? button = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasAnyElementId = !string.IsNullOrWhiteSpace(elementId) || !string.IsNullOrWhiteSpace(targetElementId);
            return automation.RunExclusiveAsync(
                () => automation.DragAsync(
                    new DragRequest(
                        Locator: locator,
                        WindowHandle: hasAnyElementId ? windowHandle : effectiveWindowHandle,
                        TargetLocator: targetLocator,
                        ToX: toX,
                        ToY: toY,
                        Steps: steps,
                        Button: button,
                        ElementId: elementId,
                        TargetElementId: targetElementId,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs),
                    cancellationToken),
                cancellationToken);
        });

    private static ClickType ParseClickType(string? clickType)
    {
        if (string.IsNullOrWhiteSpace(clickType))
        {
            return ClickType.Single;
        }

        var value = clickType.Trim();
        if (value.Equals("single", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("leftClick", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("left_click", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Single;
        }

        if (value.Equals("double", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("doubleClick", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("double_click", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Double;
        }

        if (value.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("rightClick", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("right_click", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("context", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("contextMenu", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("context_menu", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Right;
        }

        throw new ArgumentException($"Unknown clickType '{clickType}'. Valid values: single, double, right.");
    }

    private static ClickMode ParseClickMode(string? clickMode)
    {
        if (string.IsNullOrWhiteSpace(clickMode))
        {
            return ClickMode.Auto;
        }

        var value = clickMode.Trim();
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return ClickMode.Auto;
        }

        if (value.Equals("mouseAlways", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("mouse_always", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("mouse", StringComparison.OrdinalIgnoreCase))
        {
            return ClickMode.MouseAlways;
        }

        if (value.Equals("invokePreferred", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invoke_preferred", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invokeFirst", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invoke_first", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invoke", StringComparison.OrdinalIgnoreCase))
        {
            return ClickMode.InvokePreferred;
        }

        throw new ArgumentException($"Unknown clickMode '{clickMode}'. Valid values: auto, mouseAlways, invokePreferred.");
    }
}
