using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class DiagnosticTools
{
    [McpServerTool(Name = "capture_diagnostic_snapshot"), Description("Capture selected, bounded diagnostic evidence for one pinned window or element. WPF sections share one dispatcher turn; cross-backend timing skew is reported.")]
    public static Task<CaptureDiagnosticSnapshotResponse> CaptureDiagnosticSnapshot(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Diagnostic sections to capture (1-8 unique values)")] IReadOnlyList<DiagnosticSection> sections,
        [Description("Optional target locator; omit locator and elementId to target the window")] CoreElementLocator? locator = null,
        [Description("Optional resolved target elementId; mutually exclusive with locator")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Shared depth, item, node, value-length, and payload budgets")] DiagnosticSnapshotBudget? budget = null,
        [Description("Required dependency-property allowlist when WpfProperties is requested")] IReadOnlyList<string>? propertyNames = null,
        [Description("Optional root DataContext property allowlist")] IReadOnlyList<string>? dataContextProperties = null,
        [Description("Overall capture deadline in milliseconds (100-30000)")] int timeoutMs = 10_000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return automation.RunExclusiveAsync(
                () => automation.CaptureDiagnosticSnapshotAsync(
                    new CaptureDiagnosticSnapshotRequest(
                        SessionId: sessionId,
                        Sections: sections,
                        WindowHandle: effectiveWindowHandle,
                        Locator: locator?.ToElementLocator(),
                        ElementId: elementId,
                        Budget: budget,
                        PropertyNames: propertyNames,
                        DataContextProperties: dataContextProperties,
                        TimeoutMs: timeoutMs),
                    cancellationToken),
                cancellationToken);
        });
}
