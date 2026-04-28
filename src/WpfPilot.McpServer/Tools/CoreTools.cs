using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;
using WpfPilot.McpServer.Subscriptions;

namespace WpfPilot.McpServer.Tools;

public sealed record CoreElementLocator(
    [property: JsonPropertyName("automationId")] string? AutomationId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("nameContains")] string? NameContains = null,
    [property: JsonPropertyName("className")] string? ClassName = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("xpath")] string? XPath = null,
    [property: JsonPropertyName("index")] int? Index = null,
    [property: JsonPropertyName("strict")] bool Strict = true)
{
    internal ElementLocator ToElementLocator() =>
        new(
            AutomationId: AutomationId,
            Name: Name,
            NameContains: NameContains,
            ClassName: ClassName,
            TypeEquals: Type,
            ControlTypeEquals: Type,
            XPath: XPath,
            Index: Index,
            Strict: Strict);
}

public sealed record CoreFindQuery(
    [property: JsonPropertyName("automationId")] string? AutomationId = null,
    [property: JsonPropertyName("automationIdContains")] string? AutomationIdContains = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("nameContains")] string? NameContains = null,
    [property: JsonPropertyName("type")] string? Type = null)
{
    internal FindElementsQuery ToFindElementsQuery() =>
        new(
            AutomationIdEquals: AutomationId,
            AutomationIdContains: AutomationIdContains,
            NameEquals: Name,
            NameContains: NameContains,
            TypeEquals: Type);
}

public static class CoreAppTools
{
    [McpServerTool(Name = "launch_app"), Description("Start a WPF application.")]
    public static Task<LaunchAppResponse> LaunchApp(
        SessionManager sessions,
        [Description("Executable path")] string exePath,
        [Description("Optional arguments")] string[]? args = null,
        [Description("Optional working directory")] string? workingDirectory = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.LaunchAppAsync(
                new LaunchAppRequest(exePath, args, workingDirectory),
                cancellationToken));

    [McpServerTool(Name = "attach_to_app"), Description("Attach to an already running process.")]
    public static Task<AttachToAppResponse> AttachToApp(
        SessionManager sessions,
        [Description("Process ID")] int? pid = null,
        [Description("Process name")] string? processName = null,
        CancellationToken cancellationToken = default)
    {
        if (pid is not null && !string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Provide either pid or processName, not both.");
        }

        return McpToolErrors.RunAsync(() =>
            sessions.AttachToAppAsync(new AttachToAppRequest(pid, processName), cancellationToken));
    }

    [McpServerTool(Name = "close_session"), Description("Close and dispose a session.")]
    public static Task<CloseAppResponse> CloseSession(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Force kill if graceful close fails")] bool force = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            subscriptions.UnsubscribeAllForSession(sessionId);
            return sessions.CloseSessionAsync(sessionId, new CloseAppRequest(force), cancellationToken);
        });

    [McpServerTool(Name = "list_sessions"), Description("List active sessions.")]
    public static Task<ListSessionsResponse> ListSessions(
        SessionManager sessions,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => Task.FromResult(sessions.ListSessions()));

    [McpServerTool(Name = "list_windows"), Description("Enumerate top-level windows of the attached process.")]
    public static Task<ListWindowsResponse> ListWindows(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.ListWindowsAsync(cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "set_active_window"), Description("Bring a window to the foreground and set it as active for the session.")]
    public static Task<FocusWindowResponse> SetActiveWindow(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Window title")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle is not null && !string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Provide either windowHandle or title, not both.");
        }

        return McpToolErrors.RunAsync(() =>
            sessions.SetActiveWindowAsync(sessionId, new FocusWindowRequest(windowHandle, title), cancellationToken));
    }
}

public static class CoreInspectionTools
{
    [McpServerTool(Name = "take_screenshot"), Description("Capture a screenshot of the active window or a target element.")]
    public static Task<TakeScreenshotResponse> TakeScreenshot(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional target locator")] CoreElementLocator? locator = null,
        [Description("Optional target elementId")] string? elementId = null,
        [Description("Optional output file path")] string? outputPath = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.TakeScreenshotAsync(
                    new TakeScreenshotRequest(
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        Locator: locator?.ToElementLocator(),
                        ElementId: elementId,
                        Backend: InspectionBackend.Auto,
                        OutputPath: outputPath),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_visual_tree"), Description("Return a compact UI tree. Uses WPF inspection when available, otherwise UIA.")]
    public static Task<GetVisualTreeResponse> GetVisualTree(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional root locator for subtree")] CoreElementLocator? root = null,
        [Description("Maximum depth")] int depth = 4,
        [Description("Maximum returned nodes")] int maxNodes = 500,
        [Description("Only include visible elements")] bool visibleOnly = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetVisualTreeAsync(
                    InspectionBackend.Auto,
                    effectiveWindowHandle,
                    root?.ToElementLocator(),
                    depth,
                    maxNodes,
                    visibleOnly,
                    includeOffViewport: false,
                    interactiveOnly: false,
                    InteractiveMode.Heuristic,
                    TreePreset.Minimal,
                    fields: null,
                    cancellationToken,
                    autoInject: true),
                cancellationToken);
        });

    [McpServerTool(Name = "find_elements"), Description("Find matching elements without dumping the full tree.")]
    public static Task<FindElementsResponse> FindElements(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Search query")] CoreFindQuery query,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Only include visible elements")] bool visibleOnly = true,
        [Description("Maximum returned matches")] int maxResults = 25,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.FindElementsAsync(
                    InspectionBackend.Auto,
                    effectiveWindowHandle,
                    root: null,
                    query.ToFindElementsQuery(),
                    visibleOnly,
                    includeOffViewport: true,
                    interactiveOnly: false,
                    InteractiveMode.Heuristic,
                    maxResults,
                    maxNodes: 5000,
                    FindReturnFields.Minimal,
                    includeElementIds: true,
                    cancellationToken,
                    autoInject: true),
                cancellationToken);
        });

    [McpServerTool(Name = "resolve_element"), Description("Resolve an element and return an elementId handle for reuse.")]
    public static Task<ResolveElementResponse> ResolveElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator locator,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.ResolveElementAsync(
                    InspectionBackend.Auto,
                    locator.ToElementLocator(),
                    effectiveWindowHandle,
                    autoInject: true,
                    cancellationToken: cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_element_properties"), Description("Return UI Automation properties and supported patterns for one element.")]
    public static Task<GetElementPropertiesResponse> GetElementProperties(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetElementPropertiesAsync(
                    locator?.ToElementLocator(),
                    elementId,
                    hasElementId ? windowHandle : effectiveWindowHandle,
                    cancellationToken),
                cancellationToken);
        });
}

public static class CoreWpfDiagnosticsTools
{
    [McpServerTool(Name = "get_binding_errors"), Description("List WPF binding errors in the visual tree.")]
    public static Task<GetBindingErrorsResponse> GetBindingErrors(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional WPF XPath root")] string? rootXPath = null,
        [Description("Maximum depth")] int depth = 6,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetBindingErrorsAsync(effectiveWindowHandle, rootXPath, depth, maxErrors: 200, maxNodes: 2000, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_binding_info"), Description("Inspect bindings for a WPF element.")]
    public static Task<GetBindingInfoResponse> GetBindingInfo(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetBindingInfoAsync(
                    locator?.ToElementLocator(),
                    elementId,
                    hasElementId ? windowHandle : effectiveWindowHandle,
                    includeUnbound: false,
                    maxProperties: 2000,
                    valueFormat: "string",
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_data_context"), Description("Serialize the DataContext of a WPF element.")]
    public static Task<GetDataContextResponse> GetDataContext(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Maximum object graph depth")] int maxDepth = 2,
        [Description("Optional root property allowlist")] string[]? properties = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetDataContextAsync(
                    locator?.ToElementLocator(),
                    elementId,
                    hasElementId ? windowHandle : effectiveWindowHandle,
                    DataContextMode.Summary,
                    maxDepth,
                    maxPropertiesPerObject: 50,
                    maxStringLength: 2000,
                    includeNulls: false,
                    includeFrameworkProperties: false,
                    propertyAllowList: properties,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_computed_properties"), Description("Inspect selected computed dependency property values for a WPF element.")]
    public static Task<GetComputedPropertiesResponse> GetComputedProperties(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Dependency property names to inspect")] string[] propertyNames,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            if (propertyNames.Length == 0)
            {
                throw new ArgumentException("Provide at least one property name.");
            }

            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetComputedPropertiesAsync(
                    locator?.ToElementLocator(),
                    elementId,
                    hasElementId ? windowHandle : effectiveWindowHandle,
                    propertyNames,
                    includeSources: true,
                    includeDefault: false,
                    includeUnset: false,
                    maxProperties: Math.Min(propertyNames.Length, 200),
                    valueFormat: "string",
                    cancellationToken),
                cancellationToken);
        });
}

public static class CoreInteractionTools
{
    [McpServerTool(Name = "wait_for"), Description("Wait for an element state.")]
    public static Task<WaitForResponse> WaitFor(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Wait state")] string state = "visible",
        [Description("Expected text for name_contains")] string? expectedText = null,
        [Description("Expected value for value_equals")] double? expectedValue = null,
        [Description("Timeout in milliseconds")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var request = new WaitForRequest(
                Locator: locator?.ToElementLocator(),
                ElementId: elementId,
                WindowHandle: hasElementId ? null : effectiveWindowHandle,
                Backend: InspectionBackend.Auto,
                State: state,
                TimeoutMs: timeoutMs,
                ExpectedValue: expectedValue,
                ExpectedText: expectedText);

            return automation.RunExclusiveAsync(() => automation.WaitForAsync(request, cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "click_element"), Description("Click an element by locator or elementId.")]
    public static Task<ClickElementResponse> ClickElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Click type: single, double, or right")] string? clickType = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.ClickElementAsync(
                    new ClickElementRequest(
                        Locator: locator?.ToElementLocator(),
                        ElementId: elementId,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
                        ClickType: ParseClickType(clickType)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "type_text"), Description("Type text into a focused or specified element.")]
    public static Task<TypeTextResponse> TypeText(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Text to enter")] string text,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.TypeTextAsync(
                    new TypeTextRequest(
                        Locator: locator?.ToElementLocator(),
                        Text: text,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "set_value"), Description("Set a control value by locator or elementId.")]
    public static Task<SetValueResponse> SetValue(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Value to set")] double value,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.SetValueAsync(
                    new SetValueRequest(
                        Locator: locator?.ToElementLocator(),
                        Value: value,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "select_item"), Description("Select an item in a combo box, list box, tab control, or tree.")]
    public static Task<SelectItemResponse> SelectItem(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Container locator")] CoreElementLocator? locator = null,
        [Description("Container elementId")] string? elementId = null,
        [Description("Item text")] string? text = null,
        [Description("Item index")] int? index = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.SelectItemAsync(
                    new SelectItemRequest(
                        Locator: locator?.ToElementLocator(),
                        Text: text,
                        Index: index,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
                        ElementId: elementId),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "invoke"), Description("Invoke an element via UI Automation.")]
    public static Task<InvokeResponse> Invoke(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.InvokeAsync(
                    new InvokeRequest(
                        Locator: locator?.ToElementLocator(),
                        ElementId: elementId,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "scroll_to_element"), Description("Scroll a target element into view.")]
    public static Task<ScrollToElementResponse> ScrollToElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Target locator")] CoreElementLocator? locator = null,
        [Description("Target elementId")] string? elementId = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.ScrollToElementAsync(
                    new ScrollToElementRequest(
                        Locator: locator?.ToElementLocator(),
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
                        ElementId: elementId),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "drag"), Description("Drag from an element to another element or to screen coordinates.")]
    public static Task<DragResponse> Drag(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Source locator")] CoreElementLocator? locator = null,
        [Description("Source elementId")] string? elementId = null,
        [Description("Target locator")] CoreElementLocator? targetLocator = null,
        [Description("Target elementId")] string? targetElementId = null,
        [Description("Target X screen coordinate")] int? toX = null,
        [Description("Target Y screen coordinate")] int? toY = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasAnyElementId = !string.IsNullOrWhiteSpace(elementId) || !string.IsNullOrWhiteSpace(targetElementId);
            return automation.RunExclusiveAsync(
                () => automation.DragAsync(
                    new DragRequest(
                        Locator: locator?.ToElementLocator(),
                        WindowHandle: hasAnyElementId ? null : effectiveWindowHandle,
                        TargetLocator: targetLocator?.ToElementLocator(),
                        ToX: toX,
                        ToY: toY,
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
            value.Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Single;
        }

        if (value.Equals("double", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Double;
        }

        if (value.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("context", StringComparison.OrdinalIgnoreCase))
        {
            return ClickType.Right;
        }

        throw new ArgumentException($"Unknown clickType '{clickType}'. Valid values: single, double, right.");
    }
}
