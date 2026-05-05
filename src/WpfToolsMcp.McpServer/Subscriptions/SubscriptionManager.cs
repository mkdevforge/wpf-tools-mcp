using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Subscriptions;

public sealed class SubscriptionManager : IDisposable
{
    internal sealed record SubscriptionRegistration(string SubscriptionId, Task Worker);

    internal interface ISubscriptionEventSink
    {
        void Enqueue(string kind, JsonNode payload);
    }

    private sealed class SubscriptionEventSink : ISubscriptionEventSink
    {
        private readonly SubscriptionState _state;

        public SubscriptionEventSink(SubscriptionState state)
        {
            _state = state;
        }

        public void Enqueue(string kind, JsonNode payload)
            => _state.Enqueue(kind, payload);
    }

    private enum SubscriptionLifecycle
    {
        Active,
        Faulted,
        Disposed
    }

    private readonly record struct DrainResult(
        IReadOnlyList<SubscriptionEvent> Events,
        int Dropped,
        bool HasMore,
        bool FaultedAndDrained);

    private sealed class SubscriptionState : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<SubscriptionEvent> _queue = new();

        private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dropped;
        private int _sequence;
        private SubscriptionLifecycle _lifecycle = SubscriptionLifecycle.Active;

        public SubscriptionState(
            string subscriptionId,
            string sessionId,
            SubscriptionKind kind,
            int maxQueue,
            CancellationTokenSource cts)
        {
            SubscriptionId = subscriptionId;
            SessionId = sessionId;
            Kind = kind;
            MaxQueue = maxQueue;
            Cts = cts;
        }

        public string SubscriptionId { get; }
        public string SessionId { get; }
        public SubscriptionKind Kind { get; }
        public int MaxQueue { get; }
        public CancellationTokenSource Cts { get; }

        public Task? Worker { get; set; }

        public void Enqueue(string kind, JsonNode payload)
        {
            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                if (_lifecycle == SubscriptionLifecycle.Disposed)
                {
                    return;
                }

                toSignal = EnqueueLocked(kind, payload);
            }

            toSignal.TrySetResult(true);
        }

        public void Fault(Exception ex)
        {
            var payload = JsonSerializer.SerializeToNode(new { message = ex.Message })!;

            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                if (_lifecycle == SubscriptionLifecycle.Disposed)
                {
                    return;
                }

                _lifecycle = SubscriptionLifecycle.Faulted;
                toSignal = EnqueueLocked("subscription_error", payload);
            }

            toSignal.TrySetResult(true);
        }

        public DrainResult Drain(int maxBatch)
        {
            lock (_sync)
            {
                var batch = new List<SubscriptionEvent>(Math.Min(maxBatch, _queue.Count));
                while (batch.Count < maxBatch && _queue.Count > 0)
                {
                    batch.Add(_queue.Dequeue());
                }

                var dropped = _dropped;
                _dropped = 0;

                var hasMore = _queue.Count > 0;
                var faultedAndDrained = _lifecycle == SubscriptionLifecycle.Faulted && !hasMore;

                return new DrainResult(batch.ToArray(), dropped, hasMore, faultedAndDrained);
            }
        }

        public Task WaitForEventAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_queue.Count > 0)
                {
                    return Task.CompletedTask;
                }

                return _wake.Task.WaitAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                _lifecycle = SubscriptionLifecycle.Disposed;
                toSignal = _wake;
                _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                Cts.Cancel();
            }
            catch
            {
            }

            try
            {
                Worker?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            toSignal.TrySetResult(true);
        }

        private TaskCompletionSource<bool> EnqueueLocked(string kind, JsonNode payload)
        {
            if (_queue.Count >= MaxQueue)
            {
                _queue.Dequeue();
                _dropped++;
            }

            _sequence++;
            _queue.Enqueue(new SubscriptionEvent(_sequence, kind, payload));

            var toSignal = _wake;
            _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return toSignal;
        }
    }

    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
        {
            try
            {
                sub.Dispose();
            }
            catch
            {
            }
        }

        _subscriptions.Clear();
    }

    public SubscribeBindingErrorsResponse SubscribeBindingErrors(
        string sessionId,
        AutomationController automation,
        long? windowHandleUsed,
        string? rootXPath,
        int depth,
        int maxErrors,
        int maxNodes,
        int pollIntervalMs,
        int maxQueue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(automation);

        var registration = StartSubscription(
            sessionId,
            SubscriptionKind.BindingErrors,
            maxQueue,
            async (events, cancellationToken) =>
        {
            var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(pollIntervalMs, 50, 60_000));
            var lastKeys = new HashSet<string>(StringComparer.Ordinal);

            while (!cancellationToken.IsCancellationRequested)
            {
                await TickBindingErrorsAsync(
                    events,
                    automation,
                    windowHandleUsed,
                    rootXPath,
                    depth,
                    maxErrors,
                    maxNodes,
                    lastKeys,
                    cancellationToken)
                    .ConfigureAwait(false);

                await Task.Delay(pollDelay, cancellationToken).ConfigureAwait(false);
            }
        });

        return new SubscribeBindingErrorsResponse(registration.SubscriptionId);
    }

    internal SubscriptionRegistration StartSubscription(
        string sessionId,
        SubscriptionKind kind,
        int maxQueue,
        Func<ISubscriptionEventSink, CancellationToken, Task> runAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(runAsync);

        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();

        var state = new SubscriptionState(
            subscriptionId: subscriptionId,
            sessionId: sessionId,
            kind: kind,
            maxQueue: Math.Clamp(maxQueue, 1, 10_000),
            cts: cts);

        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            throw new InvalidOperationException("Failed to register subscription.");
        }

        var sink = new SubscriptionEventSink(state);
        var worker = Task.Run(async () =>
        {
            try
            {
                await runAsync(sink, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                state.Fault(ex);
            }
        });

        state.Worker = worker;
        return new SubscriptionRegistration(subscriptionId, worker);
    }

    private static async Task TickBindingErrorsAsync(
        ISubscriptionEventSink events,
        AutomationController automation,
        long? windowHandleUsed,
        string? rootXPath,
        int depth,
        int maxErrors,
        int maxNodes,
        HashSet<string> lastKeys,
        CancellationToken cancellationToken)
    {
        GetBindingErrorsResponse response;
        try
        {
            response = await automation.RunExclusiveAsync(
                () => automation.GetBindingErrorsAsync(windowHandleUsed, rootXPath, depth, maxErrors, maxNodes, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            events.Enqueue(
                kind: "subscription_error",
                payload: JsonSerializer.SerializeToNode(new { message = ex.Message })!);
            return;
        }

        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var newErrors = new List<BindingErrorInfo>();

        foreach (var error in response.Errors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = ComputeBindingErrorKey(error);
            currentKeys.Add(key);

            if (!lastKeys.Contains(key))
            {
                newErrors.Add(error);
            }
        }

        lastKeys.Clear();
        foreach (var key in currentKeys)
        {
            lastKeys.Add(key);
        }

        foreach (var error in newErrors.OrderBy(e => ComputeBindingErrorKey(e), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Enqueue(
                kind: "binding_error_added",
                payload: JsonSerializer.SerializeToNode(error)!);
        }
    }

    private static string ComputeBindingErrorKey(BindingErrorInfo error)
    {
        static string N(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

        return string.Join(
            "|",
            N(error.ElementXPath),
            N(error.ElementType),
            N(error.AutomationId),
            N(error.TargetProperty),
            N(error.Path),
            N(error.ErrorMessage),
            N(error.Status));
    }

    public async Task<PollSubscriptionResponse> PollAsync(
        string sessionId,
        string subscriptionId,
        int maxBatch,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var state = Get(subscriptionId);
        if (!string.Equals(state.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("subscriptionId does not belong to sessionId.");
        }

        var batchSize = Math.Clamp(maxBatch, 1, 500);

        var firstDrain = state.Drain(batchSize);
        RemoveIfFaultedAndDrained(subscriptionId, state, firstDrain);

        if (firstDrain.Events.Count > 0 || timeoutMs <= 0)
        {
            return new PollSubscriptionResponse(firstDrain.Events, firstDrain.Dropped, firstDrain.HasMore);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, 60_000));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Cts.Token);
        linked.CancelAfter(timeout);

        try
        {
            await state.WaitForEventAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        var secondDrain = state.Drain(batchSize);
        RemoveIfFaultedAndDrained(subscriptionId, state, secondDrain);

        return new PollSubscriptionResponse(secondDrain.Events, secondDrain.Dropped, secondDrain.HasMore);
    }

    public UnsubscribeResponse Unsubscribe(string sessionId, string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        if (!_subscriptions.TryRemove(subscriptionId, out var state))
        {
            return new UnsubscribeResponse(false);
        }

        if (!string.Equals(state.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            _subscriptions.TryAdd(subscriptionId, state);
            throw new InvalidOperationException("subscriptionId does not belong to sessionId.");
        }

        state.Dispose();
        return new UnsubscribeResponse(true);
    }

    public void UnsubscribeAllForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        foreach (var kvp in _subscriptions)
        {
            if (!string.Equals(kvp.Value.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_subscriptions.TryRemove(kvp.Key, out var state))
            {
                state.Dispose();
            }
        }
    }

    private SubscriptionState Get(string subscriptionId)
    {
        if (_subscriptions.TryGetValue(subscriptionId, out var state))
        {
            return state;
        }

        throw new InvalidOperationException($"Unknown subscriptionId '{subscriptionId}'.");
    }

    private void RemoveIfFaultedAndDrained(string subscriptionId, SubscriptionState state, DrainResult drain)
    {
        if (!drain.FaultedAndDrained)
        {
            return;
        }

        if (!_subscriptions.TryGetValue(subscriptionId, out var current) || !ReferenceEquals(current, state))
        {
            return;
        }

        if (_subscriptions.TryRemove(subscriptionId, out var removed) && ReferenceEquals(removed, state))
        {
            removed.Dispose();
        }
    }
}
