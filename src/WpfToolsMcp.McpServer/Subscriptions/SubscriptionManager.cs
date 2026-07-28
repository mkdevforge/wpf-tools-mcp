using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Subscriptions;

public sealed class SubscriptionManager : IDisposable
{
    private sealed class SubscriptionState : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<SubscriptionEvent> _queue = new();

        private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stopTask;
        private int _dropped;
        private int _sequence;
        private int _ctsDisposed;
        private int _stopRequested;

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
            Token = cts.Token;
        }

        public string SubscriptionId { get; }
        public string SessionId { get; }
        public SubscriptionKind Kind { get; }
        public int MaxQueue { get; }
        public CancellationTokenSource Cts { get; }
        public CancellationToken Token { get; }

        public Task? Worker { get; set; }
        public bool IsStopping => Volatile.Read(ref _stopRequested) != 0;

        public void Enqueue(string kind, JsonNode payload)
        {
            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                if (_queue.Count >= MaxQueue)
                {
                    _queue.Dequeue();
                    _dropped++;
                }

                _sequence++;
                _queue.Enqueue(new SubscriptionEvent(_sequence, kind, payload));

                toSignal = _wake;
                _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            toSignal.TrySetResult(true);
        }

        public (IReadOnlyList<SubscriptionEvent> Events, int Dropped, bool HasMore) Drain(int maxBatch)
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

                return (batch.ToArray(), dropped, _queue.Count > 0);
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

        public void RequestStop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);

            try
            {
                Cts.Cancel();
            }
            catch
            {
            }

        }

        public bool TryRequestStop()
        {
            if (Interlocked.CompareExchange(ref _stopRequested, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                Cts.Cancel();
            }
            catch
            {
            }

            return true;
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                return _stopTask ??= StopCoreAsync();
            }
        }

        private async Task StopCoreAsync()
        {
            RequestStop();
            var worker = Worker;
            try
            {
                if (worker is not null)
                {
                    await worker.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // The worker has already ended; teardown must still release its resources.
            }
            finally
            {
                DisposeCancellationSource();
            }
        }

        public void DisposeCancellationSource()
        {
            if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
            {
                Cts.Dispose();
            }
        }

        public void Dispose() => StopAsync().GetAwaiter().GetResult();
    }

    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        var subscriptions = _subscriptions.Values.ToArray();
        foreach (var sub in subscriptions)
        {
            sub.RequestStop();
        }

        foreach (var sub in subscriptions)
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

        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();

        var state = new SubscriptionState(
            subscriptionId: subscriptionId,
            sessionId: sessionId,
            kind: SubscriptionKind.BindingErrors,
            maxQueue: Math.Clamp(maxQueue, 1, 10_000),
            cts: cts);

        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            throw new InvalidOperationException("Failed to register subscription.");
        }

        state.Worker = Task.Run(async () =>
        {
            var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(pollIntervalMs, 50, 60_000));
            var lastKeys = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await TickBindingErrorsAsync(
                        state,
                        automation,
                        windowHandleUsed,
                        rootXPath,
                        depth,
                        maxErrors,
                        maxNodes,
                        lastKeys,
                        cts.Token)
                        .ConfigureAwait(false);

                    await Task.Delay(pollDelay, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                state.Enqueue(
                    kind: "subscription_error",
                    payload: JsonSerializer.SerializeToNode(new { message = ex.Message })!);
            }
            finally
            {
                _ = _subscriptions.TryRemove(subscriptionId, out _);
                state.DisposeCancellationSource();
            }
        });

        return new SubscribeBindingErrorsResponse(subscriptionId);
    }

    private static async Task TickBindingErrorsAsync(
        SubscriptionState state,
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
            state.Enqueue(
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
            state.Enqueue(
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

        var (events, dropped, hasMore) = state.Drain(batchSize);
        if (events.Count > 0 || timeoutMs <= 0)
        {
            return new PollSubscriptionResponse(events, dropped, hasMore);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, 60_000));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Token);
        linked.CancelAfter(timeout);

        try
        {
            await state.WaitForEventAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        (events, dropped, hasMore) = state.Drain(batchSize);
        return new PollSubscriptionResponse(events, dropped, hasMore);
    }

    public async Task<UnsubscribeResponse> UnsubscribeAsync(string sessionId, string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        if (!_subscriptions.TryGetValue(subscriptionId, out var state))
        {
            return new UnsubscribeResponse(false);
        }

        if (!string.Equals(state.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("subscriptionId does not belong to sessionId.");
        }

        if (!state.TryRequestStop())
        {
            return new UnsubscribeResponse(false);
        }

        await state.StopAsync().ConfigureAwait(false);
        _subscriptions.TryRemove(subscriptionId, out _);
        return new UnsubscribeResponse(true);
    }

    public UnsubscribeResponse Unsubscribe(string sessionId, string subscriptionId) =>
        UnsubscribeAsync(sessionId, subscriptionId).GetAwaiter().GetResult();

    public async Task UnsubscribeAllForSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var states = new List<SubscriptionState>();
        foreach (var kvp in _subscriptions)
        {
            if (!string.Equals(kvp.Value.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kvp.Value.RequestStop();
            states.Add(kvp.Value);
        }

        await Task.WhenAll(states.Select(state => state.StopAsync())).ConfigureAwait(false);
        foreach (var state in states)
        {
            _subscriptions.TryRemove(state.SubscriptionId, out _);
        }
    }

    public void UnsubscribeAllForSession(string sessionId) =>
        UnsubscribeAllForSessionAsync(sessionId).GetAwaiter().GetResult();

    private SubscriptionState Get(string subscriptionId)
    {
        if (_subscriptions.TryGetValue(subscriptionId, out var state) && !state.IsStopping)
        {
            return state;
        }

        throw new InvalidOperationException($"Unknown subscriptionId '{subscriptionId}'.");
    }
}
