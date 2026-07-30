using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Subscriptions;

namespace WpfToolsMcp.McpServer.Tools;

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
    [McpServerTool(Name = "launch_app", UseStructuredContent = true, OutputSchemaType = typeof(LaunchAppResponse)), Description("Start a WPF application. Existing-instance fallback returns structured candidates when ambiguous.")]
    public static Task<CallToolResult> LaunchApp(
        SessionManager sessions,
        [Description("Executable path")] string exePath,
        [Description("Optional arguments")] string[]? args = null,
        [Description("Optional working directory")] string? workingDirectory = null,
        [Description("Session interaction policy")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunLaunchAppAsync(() =>
            sessions.LaunchAppAsync(
                new LaunchAppRequest(
                    ExePath: exePath,
                    Args: args,
                    WorkingDirectory: workingDirectory,
                    InteractionPolicy: interactionPolicy),
                cancellationToken));

    [McpServerTool(Name = "attach_to_app", UseStructuredContent = true, OutputSchemaType = typeof(AttachToAppResponse)), Description("Attach to one unambiguous process, or replace an exited session while preserving durable policy. Ambiguous names return structured candidates.")]
    public static Task<CallToolResult> AttachToApp(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Process ID")] int? pid = null,
        [Description("Process name (supports dotted names and optional .exe suffix)")] string? processName = null,
        [Description("Opaque process instance ID returned by an ambiguous_process candidate")] string? processInstanceId = null,
        [Description("Exited session to replace; omit all target selectors to reuse its process name")] string? sessionId = null,
        [Description("Session interaction policy")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default)
    {
        sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var selectors = (pid is not null ? 1 : 0) +
                        (!string.IsNullOrWhiteSpace(processName) ? 1 : 0) +
                        (!string.IsNullOrWhiteSpace(processInstanceId) ? 1 : 0);
        if (selectors > 1 || (string.IsNullOrWhiteSpace(sessionId) && selectors != 1))
        {
            throw new ArgumentException(
                "Provide exactly one of pid, processName, or processInstanceId; an exited sessionId may omit the target selector.");
        }

        return McpToolErrors.RunAttachToAppAsync(() =>
            sessions.AttachToAppAsync(
                new AttachToAppRequest(pid, processName, interactionPolicy, sessionId, processInstanceId),
                string.IsNullOrWhiteSpace(sessionId)
                    ? null
                    : () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));
    }

    [McpServerTool(Name = "detach_session", UseStructuredContent = true), Description("Remove an inspection session and release its resources without closing or terminating the target application.")]
    public static Task<DetachSessionResponse> DetachSession(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.DetachSessionAsync(
                sessionId,
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "close_app", UseStructuredContent = true), Description("Request a graceful application close, remove the inspection session, and report the close request and observed process outcome separately.")]
    public static Task<CloseAppResponse> CloseApp(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Wait timeout (ms) for process exit")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.CloseApplicationAsync(
                sessionId,
                timeoutMs,
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "terminate_app", UseStructuredContent = true), Description("Forcefully terminate the target application, remove the inspection session, and report the observed process outcome.")]
    public static Task<CloseAppResponse> TerminateApp(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Wait timeout (ms) for process exit")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.TerminateApplicationAsync(
                sessionId,
                timeoutMs,
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "close_session", UseStructuredContent = true), Description("Compatibility path that removes the session and closes the application, optionally force-terminating it. Prefer detach_session, close_app, or terminate_app for explicit intent.")]
    public static Task<CloseAppResponse> CloseSession(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Force kill if graceful close fails")] bool force = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.CloseSessionAsync(
                sessionId,
                new CloseAppRequest(force),
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "list_sessions", UseStructuredContent = true), Description("List active sessions; BackendCapabilities lists confirmed-ready backends, and BackendCapabilityStates reports ready/unavailable/not_initialized.")]
    public static Task<ListSessionsResponse> ListSessions(
        SessionManager sessions,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => sessions.ListSessionsAsync(cancellationToken));

    [McpServerTool(Name = "list_windows", UseStructuredContent = true), Description("Enumerate visible top-level windows of the attached process, including native dialogs and owner, modal, and framework context.")]
    public static Task<ListWindowsResponse> ListWindows(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.ListWindowsAsync(cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "set_active_window", UseStructuredContent = true), Description("Bring a window to the foreground and set it as active for the session.")]
    public static Task<FocusWindowResponse> SetActiveWindow(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Window title")] string? title = null,
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
}

public static class CoreInspectionTools
{
    public static Task<TakeScreenshotResponse> TakeScreenshot(
        SessionManager sessions,
        string sessionId,
        long? windowHandle = null,
        CoreElementLocator? locator = null,
        string? elementId = null,
        string? outputPath = null,
        CancellationToken cancellationToken = default) =>
        TakeScreenshotWithViewport(
            sessions,
            sessionId,
            windowHandle,
            locator,
            elementId,
            outputPath,
            includeViewport: false,
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "take_screenshot", UseStructuredContent = true), Description("Capture a screenshot of the active window or a target element.")]
    public static Task<TakeScreenshotResponse> TakeScreenshotWithViewport(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional target locator")] CoreElementLocator? locator = null,
        [Description("Optional target elementId")] string? elementId = null,
        [Description("Optional output file path")] string? outputPath = null,
        [Description("Include the window's client, outer, DPI, monitor, and state conditions in the response")] bool includeViewport = false,
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
                        OutputPath: outputPath)
                    {
                        IncludeViewport = includeViewport
                    },
                    cancellationToken,
                    autoInject: true),
                cancellationToken);
        });

    [McpServerTool(Name = "get_visual_tree", UseStructuredContent = true), Description("Return a compact UI tree. Uses WPF inspection when available, otherwise UIA.")]
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

    [McpServerTool(Name = "find_elements", UseStructuredContent = true), Description("Find deterministic, bounded matches without dumping the full tree.")]
    public static Task<FindElementsResponse> FindElements(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Search query")] CoreFindQuery query,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Only include visible elements")] bool visibleOnly = true,
        [Description("Maximum returned matches")] int maxResults = 25,
        [Description("Match verbosity preset")] FindReturnFields returnFields = FindReturnFields.Minimal,
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
                    returnFields,
                    includeElementIds: true,
                    cancellationToken,
                    autoInject: true),
                cancellationToken);
        });

    [McpServerTool(Name = "resolve_element", UseStructuredContent = true, OutputSchemaType = typeof(ResolveElementResponse)), Description("Resolve an element for reuse; ambiguity returns structured candidates.")]
    public static Task<CallToolResult> ResolveElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator locator,
        [Description("Optional native window handle")] long? windowHandle = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunResolveElementAsync(() =>
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

    [McpServerTool(Name = "get_element_properties", UseStructuredContent = true), Description("Return bounded UI Automation properties and supported patterns. Values cap strings at 2,000 characters, collections at 50 items, depth at 2, and share a 20,000-character budget; oversized XPaths are omitted.")]
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
                    ElementPropertiesPreset.Summary,
                    maxProperties: 25,
                    cancellationToken: cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_uia_locators", UseStructuredContent = true), Description("Return UIA locator suggestions and FlaUI snippets for a WPF or UIA element.")]
    public static Task<GetUiaLocatorsResponse> GetUiaLocators(
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
                () => automation.GetUiaLocatorsAsync(
                    locator?.ToElementLocator(),
                    elementId,
                    hasElementId ? windowHandle : effectiveWindowHandle,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_uia_tree", UseStructuredContent = true), Description("Return a bounded UIA automation tree for a window or subtree.")]
    public static Task<GetUiaTreeResponse> GetUiaTree(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Optional root locator for subtree")] CoreElementLocator? root = null,
        [Description("Maximum depth (1 = root only)")] int depth = 4,
        [Description("Maximum number of nodes returned")] int maxNodes = 200,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetUiaTreeAsync(
                    effectiveWindowHandle,
                    root?.ToElementLocator(),
                    depth,
                    maxNodes,
                    visibleOnly: true,
                    includeOffViewport: true,
                    cancellationToken),
                cancellationToken);
        });
}

public static class CoreWpfDiagnosticsTools
{
    [McpServerTool(Name = "get_binding_errors", UseStructuredContent = true), Description("List WPF binding errors in the visual tree.")]
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

    [McpServerTool(Name = "get_binding_info", UseStructuredContent = true), Description("Inspect bindings for a WPF element.")]
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

    [McpServerTool(Name = "get_data_context", UseStructuredContent = true), Description("Serialize the DataContext of a WPF element.")]
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

    [McpServerTool(Name = "get_computed_properties", UseStructuredContent = true), Description("Inspect selected computed dependency property values for a WPF element.")]
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
                    cancellationToken: cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_layout_context", UseStructuredContent = true), Description("Inspect bounded WPF layout metrics and nearby visual context for an element.")]
    public static Task<GetLayoutContextResponse> GetLayoutContext(
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
                () => automation.GetLayoutContextAsync(
                    locator?.ToElementLocator(),
                    elementId,
                    hasElementId ? windowHandle : effectiveWindowHandle,
                    maxAncestors: 6,
                    maxSiblings: 8,
                    maxGridDefinitions: 32,
                    cancellationToken),
                cancellationToken);
        });
}

public static class CoreInteractionTools
{
    [McpServerTool(Name = "wait_for", UseStructuredContent = true), Description("Wait for an element or window to satisfy a legacy state or structured condition.")]
    public static Task<WaitForResponse> WaitFor(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Legacy wait state (mutually exclusive with condition)")] string? state = null,
        [Description("Structured wait condition (mutually exclusive with state)")] WaitConditionInput? condition = null,
        [Description("Expected text for name_contains")] string? expectedText = null,
        [Description("Expected value for value_equals")] double? expectedValue = null,
        [Description("Timeout in milliseconds")] int timeoutMs = 5000,
        [Description("Throw on timeout; defaults to true for legacy state waits and false for structured conditions")] bool? throwOnTimeout = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            if (state is not null && condition is not null)
            {
                throw new ArgumentException("wait_for accepts either state or condition, not both.");
            }

            var (automation, effectiveWindowHandle) = sessions.GetController(
                sessionId,
                condition?.WindowHandle,
                condition?.ExternalWindowHandles);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var request = new WaitForRequest(
                Locator: locator?.ToElementLocator(),
                ElementId: elementId,
                WindowHandle: hasElementId ? null : effectiveWindowHandle,
                Backend: InspectionBackend.Auto,
                State: state ?? "visible",
                TimeoutMs: timeoutMs,
                ExpectedValue: expectedValue,
                ExpectedText: expectedText,
                ThrowOnTimeout: throwOnTimeout ?? (condition is null))
            {
                Condition = condition?.ToContract()
            };

            return automation.RunExclusiveAsync(() => automation.WaitForAsync(request, cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "click_element", UseStructuredContent = true), Description("Click an element by locator or elementId.")]
    public static Task<ClickElementResponse> ClickElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Click type: single, double, or right")] string? clickType = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        ClickType: ParseClickType(clickType),
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "type_text", UseStructuredContent = true), Description("Type text into the focused element, or into a specified locator/elementId.")]
    public static Task<TypeTextResponse> TypeText(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Text to enter")] string text,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Text entry mode: Replace, Append, or AtSelection. Omit to preserve legacy target-dependent behavior.")] TextEntryMode? mode = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
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
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.SendKeysAsync(
                    new SendKeysRequest(
                        Sequence: sequence,
                        Locator: locator?.ToElementLocator(),
                        ElementId: elementId,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
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
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        Text: text,
                        ElementId: elementId,
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "select_item", UseStructuredContent = true), Description("Select an item in a combo box, list box, tab control, or tree.")]
    public static Task<SelectItemResponse> SelectItem(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Container locator")] CoreElementLocator? locator = null,
        [Description("Container elementId")] string? elementId = null,
        [Description("Item text")] string? text = null,
        [Description("Item index")] int? index = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        ElementId: elementId,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "invoke", UseStructuredContent = true), Description("Invoke an element via UI Automation.")]
    public static Task<InvokeResponse> Invoke(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] CoreElementLocator? locator = null,
        [Description("Element ID")] string? elementId = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        WindowHandle: hasElementId ? null : effectiveWindowHandle,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "scroll_to_element", UseStructuredContent = true), Description("Scroll a target element into view.")]
    public static Task<ScrollToElementResponse> ScrollToElement(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Target locator")] CoreElementLocator? locator = null,
        [Description("Target elementId")] string? elementId = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        ElementId: elementId,
                        InteractionPolicy: sessions.ResolveInteractionPolicy(sessionId, interactionPolicy)),
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "drag", UseStructuredContent = true), Description("Drag from an element to another element or to screen coordinates.")]
    public static Task<DragResponse> Drag(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Source locator")] CoreElementLocator? locator = null,
        [Description("Source elementId")] string? elementId = null,
        [Description("Target locator")] CoreElementLocator? targetLocator = null,
        [Description("Target elementId")] string? targetElementId = null,
        [Description("Target X screen coordinate")] int? toX = null,
        [Description("Target Y screen coordinate")] int? toY = null,
        [Description("Interaction policy override")] InteractionPolicy? interactionPolicy = null,
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
                        TargetElementId: targetElementId,
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
