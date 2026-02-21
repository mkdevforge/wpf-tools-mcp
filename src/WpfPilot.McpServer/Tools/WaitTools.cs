using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class WaitTools
{
    [McpServerTool(Name = "wait_for"), Description("Wait for an element to satisfy a state (attached|visible|enabled|actionable|stable|value_equals|name_contains).")]
    public static Task<WaitForResponse> WaitFor(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator")] ElementLocator? locator = null,
        [Description("Element ID (from resolve_element / find_elements)")] string? elementId = null,
        [Description("Optional native window handle")] long? windowHandle = null,
        [Description("Inspection backend selection (ignored when elementId is set)")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Wait state: attached|visible|enabled|actionable|stable|value_equals|name_contains")] string state = "visible",
        [Description("Timeout (ms)")] int timeoutMs = 5000,
        [Description("Polling interval (ms)")] int pollIntervalMs = 100,
        [Description("Stable duration (ms)")] int stableMs = 250,
        [Description("Expected numeric value (for value_equals)")] double? expectedValue = null,
        [Description("Expected text (for name_contains)")] string? expectedText = null,
        [Description("Throw on timeout")] bool throwOnTimeout = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            var request = new WaitForRequest(
                Locator: locator,
                ElementId: elementId,
                WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                Backend: backend,
                State: state,
                TimeoutMs: timeoutMs,
                PollIntervalMs: pollIntervalMs,
                StableMs: stableMs,
                ExpectedValue: expectedValue,
                ExpectedText: expectedText,
                ThrowOnTimeout: throwOnTimeout);

            return automation.RunExclusiveAsync(() => automation.WaitForAsync(request, cancellationToken), cancellationToken);
        });
}
