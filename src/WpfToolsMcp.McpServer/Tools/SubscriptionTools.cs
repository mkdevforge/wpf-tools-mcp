using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Subscriptions;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class SubscriptionTools
{
    [McpServerTool(Name = "subscribe_property_changes", UseStructuredContent = true), Description("Capture event-driven WPF dependency-property and DataContext changes for one element. cadenceMs controls bounded delivery, not sampling. Diagnostics profile only; the WPF agent is injected automatically when needed.")]
    public static Task<SubscribePropertyChangesResponse> SubscribePropertyChanges(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Element locator; provide exactly one of locator or elementId")] ElementLocator? locator = null,
        [Description("Resolved WPF element handle; provide exactly one of locator or elementId")] string? elementId = null,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Allowlisted dependency-property names, for example Text, IsEnabled, or Width; at most 32 total watches")] string[]? dependencyProperties = null,
        [Description("Allowlisted dotted DataContext paths, for example Phase or Nested.Mode; at most 32 total watches")] string[]? dataContextPaths = null,
        [Description("Include bounds, visibility, and enabled state with each observation")] bool includeVisualMetadata = false,
        [Description("Delivery cadence in milliseconds (20-10000); target-side notifications are captured between deliveries")] int cadenceMs = 50,
        [Description("Observation duration in milliseconds (1-300000)")] int durationMs = 30_000,
        [Description("Maximum WPF nodes scanned when resolving a locator (1-20000; ignored for elementId)")] int maxNodes = 5_000,
        [Description("Maximum target and server queued events (1-1000; oldest events are dropped when full)")] int maxQueue = 256,
        [Description("Maximum characters retained for each scalar value (1-4096)")] int maxValueLength = 512,
        [Description("Maximum serialized subscription-event characters, including envelope and payload, returned per source and poll (4096-1048576)")] int maxPayloadChars = 262_144,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(async () =>
        {
            var watchCount = (long)(dependencyProperties?.Length ?? 0) +
                             (dataContextPaths?.Length ?? 0);
            if (watchCount is 0)
            {
                throw new ArgumentException(
                    "subscribe_property_changes requires at least one dependency property or DataContext path.");
            }

            if (watchCount > 32)
            {
                throw new ArgumentException(
                    "subscribe_property_changes supports at most 32 combined " +
                    "dependency-property and DataContext watches.");
            }

            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            return await automation.RunExclusiveAsync(async () =>
            {
                using var reservation = subscriptions.ReservePropertySubscription(sessionId);
                var trace = automation.BeginToolTrace("subscribe_property_changes");
                WpfStateObservation? observation = null;
                var registered = false;
                try
                {
                    observation = await automation.ObserveStateStartAsync(
                        new ObserveStateStartRequest(
                            WindowHandle: effectiveWindowHandle,
                            Locator: locator,
                            ElementId: elementId,
                            DependencyProperties: dependencyProperties,
                            DataContextPaths: dataContextPaths,
                            MaxNodes: maxNodes,
                            DurationMs: durationMs,
                            MaxEvents: maxQueue,
                            MaxValueLength: maxValueLength,
                            IncludeVisualMetadata: includeVisualMetadata),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var response = sessions.RegisterSessionResource(
                        sessionId,
                        () => subscriptions.SubscribePropertyChanges(
                            sessionId,
                            automation,
                            effectiveWindowHandle,
                            observation,
                            reservation,
                            cadenceMs,
                            maxQueue,
                            maxPayloadChars));
                    registered = true;
                    trace?.SetSummary(
                        $"id={response.SubscriptionId} watches={response.Watches.Count} " +
                        $"cadenceMs={response.CadenceMs} durationMs={response.DurationMs} " +
                        $"maxNodes={response.MaxNodes}");
                    return response;
                }
                catch (Exception ex)
                {
                    trace?.SetError(ex);
                    if (observation is not null && !registered)
                    {
                        await ReleaseStartedObservationBestEffortAsync(automation, observation).ConfigureAwait(false);
                    }

                    throw;
                }
                finally
                {
                    trace?.Dispose();
                }
            }, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "subscribe_binding_errors", UseStructuredContent = true), Description("Subscribe to binding errors in the WPF visual tree (poll-based). A source failure emits one terminal event and stops the worker. Requires inject_agent.")]
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
        [Description("Maximum queued events (1-1000; oldest events are dropped when full)")] int maxQueue = 200,
        [Description("Maximum serialized subscription-event characters, including envelope and payload, returned per event and poll (4096-1048576)")] int maxPayloadChars = 262_144,
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

                    var response = sessions.RegisterSessionResource(
                        sessionId,
                        () => subscriptions.SubscribeBindingErrors(
                            sessionId: sessionId,
                            automation: automation,
                            windowHandleUsed: effectiveWindowHandle,
                            rootXPath: rootXPath,
                            depth: depth,
                            maxErrors: maxErrors,
                            maxNodes: maxNodes,
                            pollIntervalMs: pollIntervalMs,
                            maxQueue: maxQueue,
                            maxPayloadChars: maxPayloadChars));

                    trace?.SetSummary(
                        $"id={response.SubscriptionId} pollMs={response.PollIntervalMs} " +
                        $"maxQueue={response.MaxQueue} maxPayloadChars={response.MaxPayloadChars}");
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

    [McpServerTool(Name = "poll_subscription", UseStructuredContent = true), Description("Poll ordered, versioned subscription events with explicit per-poll and cumulative delivery-loss counters.")]
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
            sessions.EnsureSessionActive(sessionId);

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
                    trace?.SetSummary(
                        $"events={response.Events.Count} dropped={response.Dropped} " +
                        $"coalesced={response.Coalesced} truncated={response.Truncated} " +
                        $"hasMore={response.HasMore} completed={response.Completed}");
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

    [McpServerTool(Name = "unsubscribe", UseStructuredContent = true), Description("Unsubscribe a subscription.")]
    public static Task<UnsubscribeResponse> Unsubscribe(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Subscription ID")] string subscriptionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(async () =>
        {
            sessions.EnsureSessionActive(sessionId);

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
                var response = await subscriptions.UnsubscribeAsync(sessionId, subscriptionId).ConfigureAwait(false);
                trace?.SetSummary($"unsubscribed={response.Unsubscribed}");
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
        });

    private static async Task ReleaseStartedObservationBestEffortAsync(
        AutomationController automation,
        WpfStateObservation observation)
    {
        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await automation.ReleaseObserveStateAsync(observation, cleanupCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The owning pipe connection releases the observation if rollback cannot reach it.
        }
    }
}
