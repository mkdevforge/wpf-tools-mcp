using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class TraceTools
{
    [McpServerTool(Name = "trace_start"), Description("Start a lightweight trace of MCP tool actions for a session.")]
    public static Task<TraceStartResponse> TraceStart(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Stop and discard any active trace before starting")] bool resetIfRunning = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.TraceStartAsync(resetIfRunning, cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "trace_stop"), Description("Stop an active trace, write it to a JSON file, and return a summary.")]
    public static Task<TraceStopResponse> TraceStop(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Trace ID (from trace_start)")] string traceId,
        [Description("Optional output file path (auto-generated when omitted)")] string? outputPath = null,
        [Description("Include events in response")] bool includeEvents = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.TraceStopAsync(traceId, outputPath, includeEvents, cancellationToken), cancellationToken);
        });
}

