using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class WaitTools
{
    [McpServerTool(Name = "wait_for", UseStructuredContent = true), Description("Wait for an element or window to satisfy a legacy state or structured condition.")]
    public static Task<WaitForResponse> WaitFor(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Inspection backend selection (ignored when elementId is set)")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Legacy wait state: attached|visible|enabled|actionable|stable|value_equals|name_contains (mutually exclusive with condition)")] string? state = null,
        [Description("Structured wait condition (mutually exclusive with state)")] WaitConditionInput? condition = null,
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 250,
        [Description("Expected numeric value (for value_equals)")] double? expectedValue = null,
        [Description("Expected text (for name_contains)")] string? expectedText = null,
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
                condition?.WindowHandle ?? windowHandle,
                condition?.ExternalWindowHandles);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var request = new WaitForRequest(
                Locator: locator,
                ElementId: elementId,
                WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                Backend: backend,
                State: state ?? "visible",
                TimeoutMs: timeoutMs,
                PollIntervalMs: pollIntervalMs,
                StableMs: stableMs,
                ExpectedValue: expectedValue,
                ExpectedText: expectedText,
                ThrowOnTimeout: throwOnTimeout ?? (condition is null))
            {
                Condition = condition?.ToContract()
            };

            return automation.RunExclusiveAsync(() => automation.WaitForAsync(request, cancellationToken), cancellationToken);
        });
}
