using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Subscriptions;

namespace WpfToolsMcp.McpServer.Tools;

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

            return await automation.RunExclusiveAsync(async () =>
            {
                var trace = automation.BeginToolTrace("subscribe_binding_errors");
                try
                {
                    // Fail fast with a clear message if the agent is not connected.
                    try
                    {
                        _ = await automation.AgentPingAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Agent is not connected. Call inject_agent first. ({ex.Message})");
                    }

                    var response = subscriptions.SubscribeBindingErrors(
                        sessionId: sessionId,
                        automation: automation,
                        windowHandleUsed: effectiveWindowHandle,
                        rootXPath: rootXPath,
                        depth: depth,
                        maxErrors: maxErrors,
                        maxNodes: maxNodes,
                        pollIntervalMs: pollIntervalMs,
                        maxQueue: maxQueue);

                    trace?.SetSummary($"id={response.SubscriptionId} pollMs={pollIntervalMs} maxQueue={maxQueue}");
                    return response;
                }
                catch (Exception ex)
                {
                    trace?.SetError(ex);
                    throw;
                }
                finally
                {
                    trace?.Dispose();
                }
            }, cancellationToken);
        });

    [McpServerTool(Name = "poll_subscription"), Description("Poll a subscription for queued events.")]
    public static Task<PollSubscriptionResponse> PollSubscription(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Subscription ID")] string subscriptionId,
        [Description("Maximum events returned")] int maxBatch = 50,
        [Description("Wait up to timeout for at least one event (ms). 0 = do not wait.")] int timeoutMs = 0,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            AutomationController? automation = null;
            try
            {
                (automation, _) = sessions.GetController(sessionId);
            }
            catch
            {
            }

            var trace = automation?.BeginToolTrace("poll_subscription");
            return TraceAsync();

            async Task<PollSubscriptionResponse> TraceAsync()
            {
                try
                {
                    var response = await subscriptions.PollAsync(sessionId, subscriptionId, maxBatch, timeoutMs, cancellationToken);
                    trace?.SetSummary($"events={response.Events.Count} dropped={response.Dropped} hasMore={response.HasMore}");
                    return response;
                }
                catch (Exception ex)
                {
                    trace?.SetError(ex);
                    throw;
                }
                finally
                {
                    trace?.Dispose();
                }
            }
        });

    [McpServerTool(Name = "unsubscribe"), Description("Unsubscribe a subscription.")]
    public static Task<UnsubscribeResponse> Unsubscribe(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Subscription ID")] string subscriptionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            AutomationController? automation = null;
            try
            {
                (automation, _) = sessions.GetController(sessionId);
            }
            catch
            {
            }

            var trace = automation?.BeginToolTrace("unsubscribe");
            try
            {
                var response = subscriptions.Unsubscribe(sessionId, subscriptionId);
                trace?.SetSummary($"unsubscribed={response.Unsubscribed}");
                return Task.FromResult(response);
            }
            catch (Exception ex)
            {
                trace?.SetError(ex);
                throw;
            }
            finally
            {
                trace?.Dispose();
            }
        });
}
