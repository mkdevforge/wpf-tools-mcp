using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class AgentTools
{
    [McpServerTool(Name = "inject_agent"), Description("Inject the WPF Tools MCP in-process inspection agent (Snoop-based) into the attached application.")]
    public static Task<InjectAgentResponse> InjectAgent(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.InjectAgentAsync(cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "agent_ping"), Description("Ping the injected agent over the named pipe.")]
    public static Task<AgentPingResponse> AgentPing(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.AgentPingAsync(cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "get_binding_info"), Description("Inspect bindings for a WPF element via the injected agent.")]
    public static Task<GetBindingInfoResponse> GetBindingInfo(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator (WPF XPath recommended)")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Include unbound properties")] bool includeUnbound = false,
        [Description("Maximum number of properties inspected")] int maxProperties = 2000,
        [Description("Value format (string|type)")] string valueFormat = "string",
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetBindingInfoAsync(locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, includeUnbound, maxProperties, valueFormat, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_binding_errors"), Description("List binding errors in the WPF visual tree via the injected agent.")]
    public static Task<GetBindingErrorsResponse> GetBindingErrors(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional WPF XPath root for subtree")] string? rootXPath = null,
        [Description("Maximum depth (1 = root only)")] int depth = 6,
        [Description("Maximum errors returned")] int maxErrors = 200,
        [Description("Maximum nodes scanned")] int maxNodes = 2000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetBindingErrorsAsync(effectiveWindowHandle, rootXPath, depth, maxErrors, maxNodes, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "uia_coverage_report"), Description("Report UIA automation coverage gaps for WPF visual elements via the injected agent. Requires inject_agent.")]
    public static Task<GetUiaCoverageReportResponse> GetUiaCoverageReport(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional WPF XPath root for subtree")] string? rootXPath = null,
        [Description("Only include visible elements")] bool visibleOnly = true,
        [Description("Include off-viewport elements even when visibleOnly=true")] bool includeOffViewport = false,
        [Description("Only include interactive-looking elements")] bool interactiveOnly = true,
        [Description("Interactive detection mode")] InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        [Description("Maximum nodes scanned")] int maxNodes = 5000,
        [Description("Maximum findings returned")] int maxFindings = 200,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.GetUiaCoverageReportAsync(
                    effectiveWindowHandle,
                    rootXPath,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    maxNodes,
                    maxFindings,
                    cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_data_context"), Description("Serialize the DataContext of a WPF element via the injected agent.")]
    public static Task<GetDataContextResponse> GetDataContext(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator (WPF XPath recommended)")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Serialization mode (summary|full)")] DataContextMode mode = DataContextMode.Summary,
        [Description("Maximum object graph depth")] int maxDepth = 2,
        [Description("Maximum properties per object")] int maxPropertiesPerObject = 50,
        [Description("Maximum string length")] int maxStringLength = 2000,
        [Description("Include null values")] bool includeNulls = false,
        [Description("Include framework object properties (WPF/Dispatcher/etc) in summary mode")] bool includeFrameworkProperties = false,
        [Description("Optional root property allowlist (summary mode)")] string[]? propertyAllowList = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetDataContextAsync(
                    locator: locator,
                    elementId: elementId,
                    windowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                    mode: mode,
                    maxDepth: maxDepth,
                    maxPropertiesPerObject: maxPropertiesPerObject,
                    maxStringLength: maxStringLength,
                    includeNulls: includeNulls,
                    includeFrameworkProperties: includeFrameworkProperties,
                    propertyAllowList: propertyAllowList,
                    cancellationToken: cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_computed_properties"), Description("Inspect computed dependency property values for a WPF element via the injected agent.")]
    public static Task<GetComputedPropertiesResponse> GetComputedProperties(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator (WPF XPath recommended)")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional dependency property names to include (e.g. Width, Control.Width)")] string[]? propertyNames = null,
        [Description("Include value source details")] bool includeSources = true,
        [Description("Include default-valued properties when propertyNames is omitted")] bool includeDefault = false,
        [Description("Include UnsetValue properties when propertyNames is omitted")] bool includeUnset = false,
        [Description("Maximum number of properties returned")] int maxProperties = 500,
        [Description("Value format (string|type)")] string valueFormat = "string",
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetComputedPropertiesAsync(locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, propertyNames, includeSources, includeDefault, includeUnset, maxProperties, valueFormat, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_style_chain"), Description("Inspect the style chain for a WPF element via the injected agent.")]
    public static Task<GetStyleChainResponse> GetStyleChain(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator (WPF XPath recommended)")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Include ThemeStyle")] bool includeThemeStyle = true,
        [Description("Include best-effort resource keys for style/template")] bool includeResourceKeys = false,
        [Description("Maximum BasedOn chain depth")] int maxBasedOnDepth = 10,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetStyleChainAsync(locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, includeThemeStyle, includeResourceKeys, maxBasedOnDepth, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "get_template_info"), Description("Inspect the applied template for a WPF element via the injected agent.")]
    public static Task<GetTemplateInfoResponse> GetTemplateInfo(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator (WPF XPath recommended)")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Include named elements in the applied template (Control only)")] bool includeNamedElements = false,
        [Description("Maximum named elements returned")] int maxNamedElements = 50,
        [Description("Include best-effort resource keys for style/template")] bool includeResourceKeys = false,
        [Description("Include template part element references (XPath + bounds)")] bool includePartElementRefs = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetTemplateInfoAsync(locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, includeNamedElements, maxNamedElements, includeResourceKeys, includePartElementRefs, cancellationToken),
                cancellationToken);
        });
}
