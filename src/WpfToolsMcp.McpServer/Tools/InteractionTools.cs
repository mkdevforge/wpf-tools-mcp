using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class InteractionTools
{
    [McpServerTool(Name = "set_active_window", UseStructuredContent = true), Description("Bring a window to the foreground and set it as the active window for this session.")]
    public static Task<FocusWindowResponse> SetActiveWindow(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Window title (exact match first, then contains)")] string? title = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle is not null && !string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Provide either windowHandle or title, not both.");
        }

        return McpToolErrors.RunAsync(() =>
            sessions.SetActiveWindowAsync(
                sessionId,
                new FocusWindowRequest(
                    windowHandle,
                    title,
                    sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                cancellationToken));
    }

    [McpServerTool(Name = "get_active_window", UseStructuredContent = true), Description("Get the active window for this session.")]
    public static Task<GetActiveWindowResponse> GetActiveWindow(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => sessions.GetActiveWindowAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "set_window_bounds", UseStructuredContent = true), Description("Move/resize a window by setting its bounds (outer window rectangle).")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        EnsureForeground: ensureForeground,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_window_viewport", UseStructuredContent = true), Description("Set an exact client-area size in physical pixels or WPF DIPs and report the resulting viewport conditions.")]
    public static Task<SetWindowViewportResponse> SetWindowViewport(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Requested client width in the selected unit")] double clientWidth,
        [Description("Requested client height in the selected unit")] double clientHeight,
        [Description("Client-size unit: physicalPixels | wpfDips")] ViewportUnit unit = ViewportUnit.PhysicalPixels,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Clamp the resulting outer window bounds to the monitor work area")] bool clampToWorkArea = false,
        [Description("Bring the window to the foreground first")] bool ensureForeground = false,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.SetWindowViewportAsync(
                    new SetWindowViewportRequest(
                        ClientWidth: clientWidth,
                        ClientHeight: clientHeight,
                        Unit: unit,
                        WindowHandle: effectiveWindowHandle,
                        ClampToWorkArea: clampToWorkArea,
                        EnsureForeground: ensureForeground,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_window_state", UseStructuredContent = true), Description("Set a window state (normal/minimized/maximized).")]
    public static Task<SetWindowStateResponse> SetWindowState(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Target window state")] WindowState state = WindowState.Normal,
        [Description("Bring window to foreground first")] bool ensureForeground = true,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.SetWindowStateAsync(
                    new SetWindowStateRequest(
                        WindowHandle: effectiveWindowHandle,
                        State: state,
                        EnsureForeground: ensureForeground,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "click_element", UseStructuredContent = true), Description("Click an element by locator or elementId.")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.ClickElementAsync(
                    new ClickElementRequest(
                        Locator: locator,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        ClickType: ParseClickType(clickType),
                        ClickMode: ParseClickMode(clickMode),
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "mouse_click", UseStructuredContent = true), Description("Click at a coordinate (Playwright-style).")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        EnsureForeground: ensureForeground,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "invoke", UseStructuredContent = true), Description("Invoke an element via InvokePattern (locator or elementId).")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "type_text", UseStructuredContent = true), Description("Type text into the focused element, or into a specified locator/elementId.")]
    public static Task<TypeTextResponse> TypeText(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Text to enter")] string text,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Text entry mode: Replace, Append, or AtSelection. Omit to preserve legacy target-dependent behavior.")] TextEntryMode? mode = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy),
                        Mode: mode),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "send_keys", UseStructuredContent = true), Description("Send an ordered sequence of physical keyboard keys or modifier chords to the focused element, or to a specified locator/elementId.")]
    public static Task<SendKeysResponse> SendKeys(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Ordered key and modifier sequence (1-100 strokes)")] IReadOnlyList<KeyStroke> sequence,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Auto-wait for actionability")] bool autoWait = true,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 150,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.SendKeysAsync(
                    new SendKeysRequest(
                        Sequence: sequence,
                        Locator: locator,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        TimeoutMs: timeoutMs,
                        AutoWait: autoWait,
                        PollIntervalMs: pollIntervalMs,
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_value", UseStructuredContent = true), Description("Set a numeric or text value by locator or elementId.")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "select_item", UseStructuredContent = true), Description("Select an item in a combo box, list box, or tab control (locator or elementId).")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "realize_item", UseStructuredContent = true), Description("Realize one virtualized UIA item by provider-order index or exact Name. This explicit mutation may change viewport position and trigger data or container loading.")]
    public static Task<RealizeItemResponse> RealizeItem(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("ItemContainer provider locator")] ElementLocator? containerLocator = null,
        [Description("ItemContainer provider elementId")] string? containerElementId = null,
        [Description("Zero-based provider-order item index (mutually exclusive with name)")] int? index = null,
        [Description("Exact UIA Name using provider-defined equality (mutually exclusive with index)")] string? name = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Maximum ItemContainer provider calls (1-1000)")] int maxProviderCalls = RealizeItemLimits.DefaultMaxProviderCalls,
        [Description("Advisory elapsed limit checked between provider calls, in milliseconds (1-60000)")] int advisoryElapsedLimitMs = RealizeItemLimits.DefaultAdvisoryElapsedLimitMs,
        [Description("Postcondition polling interval in milliseconds (10-1000)")] int pollIntervalMs = RealizeItemLimits.DefaultPollIntervalMs,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasContainerElementId = !string.IsNullOrWhiteSpace(containerElementId);
            return automation.RunExclusiveAsync(
                () => automation.RealizeItemAsync(
                    containerLocator,
                    containerElementId,
                    index,
                    name,
                    hasContainerElementId ? windowHandle : effectiveWindowHandle,
                    maxProviderCalls,
                    advisoryElapsedLimitMs,
                    pollIntervalMs,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "scroll_to_element", UseStructuredContent = true), Description("Scroll a container to bring an element into view (locator or elementId).")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "drag", UseStructuredContent = true), Description("Drag from an element to another element or to screen coordinates (locator or elementId).")]
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
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        StableMs: stableMs,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
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
