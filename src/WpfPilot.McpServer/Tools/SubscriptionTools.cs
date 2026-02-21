using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;
using WpfPilot.McpServer.Subscriptions;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class SubscriptionTools
{
    [McpServerTool(Name = "subscribe_binding_errors"), Description("Subscribe to binding errors in the WPF visual tree (poll-based). Requires inject_agent.")]
    public static Task<SubscribeBindingErrorsResponse> SubscribeBindingErrors(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional WPF XPath root for subtree")] string? rootXPath = null,
        [Description("Maximum depth (1 = root only)")] int depth = 12,
        [Description("Maximum errors returned per scan")] int maxErrors = 200,
        [Description("Maximum nodes scanned per scan")] int maxNodes = 5000,
        [Description("Polling interval (ms)")] int pollIntervalMs = 1000,
        [Description("Max queued events (drops oldest when full)")] int maxQueue = 200,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(async () =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);

            // Fail fast with a clear message if the agent is not connected.
            try
            {
                _ = await automation.RunExclusiveAsync(
                    () => automation.AgentPingAsync(cancellationToken),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Agent is not connected. Call inject_agent first. ({ex.Message})");
            }

            return subscriptions.SubscribeBindingErrors(
                sessionId: sessionId,
                automation: automation,
                windowHandleUsed: effectiveWindowHandle,
                rootXPath: rootXPath,
                depth: depth,
                maxErrors: maxErrors,
                maxNodes: maxNodes,
                pollIntervalMs: pollIntervalMs,
                maxQueue: maxQueue);
        });

    [McpServerTool(Name = "poll_subscription"), Description("Poll a subscription for queued events.")]
    public static Task<PollSubscriptionResponse> PollSubscription(
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Subscription ID")] string subscriptionId,
        [Description("Maximum events returned")] int maxBatch = 50,
        [Description("Wait up to timeout for at least one event (ms). 0 = do not wait.")] int timeoutMs = 0,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            subscriptions.PollAsync(sessionId, subscriptionId, maxBatch, timeoutMs, cancellationToken));

    [McpServerTool(Name = "unsubscribe"), Description("Unsubscribe a subscription.")]
    public static Task<UnsubscribeResponse> Unsubscribe(
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Subscription ID")] string subscriptionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => Task.FromResult(subscriptions.Unsubscribe(sessionId, subscriptionId)));
}
