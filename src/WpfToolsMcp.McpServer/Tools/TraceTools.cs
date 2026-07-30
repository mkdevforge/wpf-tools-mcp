using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class TraceTools
{
    [McpServerTool(Name = "trace_start", UseStructuredContent = true), Description("Start a lightweight trace of MCP tool actions for a session.")]
    public static Task<TraceStartResponse> TraceStart(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Stop and discard any active trace before starting")] bool resetIfRunning = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(
                () => automation.TraceStartAsync(sessionId, resetIfRunning, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "trace_stop", UseStructuredContent = true), Description("Stop an active trace, write the newest 1,000 versioned events to a bounded JSON artifact, and report retention loss separately from inline truncation.")]
    public static Task<TraceStopResponse> TraceStop(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Trace ID (from trace_start)")] string traceId,
        [Description("Optional output file path (auto-generated when omitted)")] string? outputPath = null,
        [Description("Include events in response (defaults to false)")] bool includeEvents = false,
        [Description("Maximum events returned when includeEvents=true (clamped to 1-1000)")] int maxEvents = 100,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.TraceStopAsync(traceId, outputPath, includeEvents, maxEvents, cancellationToken), cancellationToken);
        });
}
