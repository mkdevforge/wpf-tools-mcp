using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

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

    [McpServerTool(Name = "click_element"), Description("Click an element by locator or elementId.")]
    public static Task<ClickElementResponse> ClickElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Click type: single | double | right")] string? clickType = null,
        [Description("Click mode: auto | mouseAlways | invokePreferred")] string? clickMode = null,
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
                        ClickMode: ParseClickMode(clickMode)),
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
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "type_text"), Description("Type text into a focused or specified element (locator or elementId).")]
    public static Task<TypeTextResponse> TypeText(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Text to enter")] string text,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
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
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_value"), Description("Set a numeric value (RangeValue/ValuePattern) by locator or elementId.")]
    public static Task<SetValueResponse> SetValue(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Numeric value to set")] double value,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
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
                        ElementId: elementId,
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle),
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
                        ItemElementId: itemElementId),
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
                        ContainerElementId: containerElementId),
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
                        TargetElementId: targetElementId),
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
