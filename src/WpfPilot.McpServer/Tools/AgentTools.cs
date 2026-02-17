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
        AutomationController automation,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(() => automation.InjectAgentAsync(cancellationToken), cancellationToken));

    [McpServerTool(Name = "agent_ping"), Description("Ping the injected agent over the named pipe.")]
    public static Task<AgentPingResponse> AgentPing(
        AutomationController automation,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(() => automation.AgentPingAsync(cancellationToken), cancellationToken));

    [McpServerTool(Name = "get_wpf_visual_tree"), Description("Return the in-process WPF visual tree via the injected agent.")]
    public static Task<GetWpfVisualTreeResponse> GetWpfVisualTree(
        AutomationController automation,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Maximum depth (1 = root only)")] int depth = 4,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(
                () => automation.GetWpfVisualTreeAsync(windowHandle, depth, cancellationToken),
                cancellationToken));
}
