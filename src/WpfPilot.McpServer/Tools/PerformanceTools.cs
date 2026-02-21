using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class PerformanceTools
{
    [McpServerTool(Name = "performance_start"), Description("Start lightweight UI-thread latency sampling via the injected agent.")]
    public static Task<PerformanceStartResponse> PerformanceStart(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Probe interval in milliseconds")] int probeIntervalMs = 50,
        [Description("Auto-stop after milliseconds")] int autoStopAfterMs = 30000,
        [Description("Stop and discard any active run before starting")] bool resetIfRunning = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(
                () => automation.PerformanceStartAsync(probeIntervalMs, autoStopAfterMs, resetIfRunning, cancellationToken),
                cancellationToken);
        });

    [McpServerTool(Name = "performance_stop"), Description("Stop a performance run and return a summary.")]
    public static Task<PerformanceStopResponse> PerformanceStop(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Run ID (from performance_start)")] string runId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(
                () => automation.PerformanceStopAsync(runId, cancellationToken),
                cancellationToken);
        });
}

