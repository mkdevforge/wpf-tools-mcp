using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class AgentTools
{
    [McpServerTool(Name = "inject_agent"), Description("Inject the WpfPilot in-process inspection agent (Snoop-based) into the attached application.")]
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

    [McpServerTool(Name = "get_data_context"), Description("Serialize the DataContext of a WPF element via the injected agent.")]
    public static Task<GetDataContextResponse> GetDataContext(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator (WPF XPath recommended)")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Maximum object graph depth")] int maxDepth = 2,
        [Description("Maximum properties per object")] int maxPropertiesPerObject = 50,
        [Description("Maximum string length")] int maxStringLength = 2000,
        [Description("Include null values")] bool includeNulls = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.GetDataContextAsync(locator, elementId, hasElementId ? windowHandle : effectiveWindowHandle, maxDepth, maxPropertiesPerObject, maxStringLength, includeNulls, cancellationToken),
                cancellationToken);
        });
}
