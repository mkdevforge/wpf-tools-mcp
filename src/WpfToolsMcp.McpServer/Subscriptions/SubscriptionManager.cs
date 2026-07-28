using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Subscriptions;

public sealed class SubscriptionManager : IDisposable
{
    private static readonly TimeSpan CompletedSubscriptionRetention = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResourceReleaseRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxPropertySubscriptionsPerSession = 8;
    private const int MaxPropertySubscriptionsTotal = 64;

    private sealed record QueuedSubscriptionEvent(SubscriptionEvent Event, int PayloadChars);

    public sealed class PropertySubscriptionReservation : IDisposable
    {
        private Action? _release;

        internal PropertySubscriptionReservation(string sessionId, Action release)
        {
            SessionId = sessionId;
            _release = release;
        }

        internal string SessionId { get; }

        internal Action Transfer()
        {
            var release = Interlocked.Exchange(ref _release, null);
            return release
                ?? throw new InvalidOperationException("Property subscription reservation is no longer active.");
        }

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed record SubscriptionDrain(
        IReadOnlyList<SubscriptionEvent> Events,
        int Dropped,
        int DroppedTotal,
        int Coalesced,
        int CoalescedTotal,
        int Truncated,
        int TruncatedTotal,
        bool HasMore,
        bool Completed,
        string? CompletionReason,
        string? CompletedAtUtc)
    {
        public bool HasDeliveryMetrics => Dropped > 0 || Coalesced > 0 || Truncated > 0;
    }

    private sealed class SubscriptionState : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<QueuedSubscriptionEvent> _queue = new();
        private readonly Func<Task>? _releaseResource;
        private readonly Action? _releaseCapacity;
        private readonly SemaphoreSlim _releaseGate = new(1, 1);
        private readonly CancellationToken _token;

        private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dropped;
        private int _droppedTotal;
        private int _coalesced;
        private int _coalescedTotal;
        private int _truncated;
        private int _truncatedTotal;
        private int _sequence;
        private int _ctsDisposed;
        private int _resourceReleased;
        private int _capacityReleased;
        private int _retirementScheduled;
        private int _stopRequested;
        private bool _completed;
        private string? _completionReason;
        private string? _completedAtUtc;
        private DateTimeOffset? _retentionTouchedAtUtc;

        public SubscriptionState(
            string subscriptionId,
            string sessionId,
            SubscriptionKind kind,
            int maxQueue,
            CancellationTokenSource cts,
            int maxPayloadChars = int.MaxValue,
            Func<Task>? releaseResource = null,
            Action? releaseCapacity = null)
        {
            SubscriptionId = subscriptionId;
            SessionId = sessionId;
            Kind = kind;
            MaxQueue = maxQueue;
            MaxPayloadChars = maxPayloadChars;
            Cts = cts;
            _token = cts.Token;
            _releaseResource = releaseResource;
            _releaseCapacity = releaseCapacity;
        }

        public string SubscriptionId { get; }
        public string SessionId { get; }
        public SubscriptionKind Kind { get; }
        public int MaxQueue { get; }
        public int MaxPayloadChars { get; }
        public CancellationTokenSource Cts { get; }
        public CancellationToken Token => _token;

        public Task? Worker { get; set; }
        public bool IsStopping => Volatile.Read(ref _stopRequested) != 0;
        public bool ResourceReleased => Volatile.Read(ref _resourceReleased) != 0;

        public void Enqueue(string kind, JsonNode payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(payload);

            var subscriptionEvent = new SubscriptionEvent(0, kind, payload);
            var payloadChars = kind.Length + payload.ToJsonString().Length + 64;
            var payloadTruncated = false;
            if (payloadChars > MaxPayloadChars)
            {
                payload = TryCompactObservationPayload(kind, payload) ??
                          JsonSerializer.SerializeToNode(new
                          {
                              truncated = true,
                              reason = "subscription_payload_limit",
                              originalPayloadChars = payloadChars,
                              maxPayloadChars = MaxPayloadChars
                          })!;
                subscriptionEvent = subscriptionEvent with { Payload = payload };
                payloadChars = kind.Length + payload.ToJsonString().Length + 64;
                payloadTruncated = true;
            }

            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                if (_completed || IsStopping)
                {
                    return;
                }

                if (payloadChars > MaxPayloadChars)
                {
                    _dropped = SaturatingAdd(_dropped, 1);
                    _droppedTotal = SaturatingAdd(_droppedTotal, 1);
                    _truncated = SaturatingAdd(_truncated, 1);
                    _truncatedTotal = SaturatingAdd(_truncatedTotal, 1);
                    toSignal = RotateWakeLocked();
                }
                else
                {
                    if (payloadTruncated)
                    {
                        _truncated = SaturatingAdd(_truncated, 1);
                        _truncatedTotal = SaturatingAdd(_truncatedTotal, 1);
                    }

                    if (_queue.Count >= MaxQueue)
                    {
                        _queue.Dequeue();
                        _dropped = SaturatingAdd(_dropped, 1);
                        _droppedTotal = SaturatingAdd(_droppedTotal, 1);
                    }

                    _sequence++;
                    _queue.Enqueue(new QueuedSubscriptionEvent(
                        subscriptionEvent with { Sequence = _sequence },
                        payloadChars));

                    toSignal = RotateWakeLocked();
                }
            }

            toSignal.TrySetResult(true);
        }

        public void AddDeliveryMetrics(long dropped, long coalesced, long truncated)
        {
            dropped = Math.Max(0, dropped);
            coalesced = Math.Max(0, coalesced);
            truncated = Math.Max(0, truncated);
            if (dropped == 0 && coalesced == 0 && truncated == 0)
            {
                return;
            }

            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                _dropped = SaturatingAdd(_dropped, dropped);
                _droppedTotal = SaturatingAdd(_droppedTotal, dropped);
                _coalesced = SaturatingAdd(_coalesced, coalesced);
                _coalescedTotal = SaturatingAdd(_coalescedTotal, coalesced);
                _truncated = SaturatingAdd(_truncated, truncated);
                _truncatedTotal = SaturatingAdd(_truncatedTotal, truncated);
                toSignal = RotateWakeLocked();
            }

            toSignal.TrySetResult(true);
        }

        public void Complete(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            TaskCompletionSource<bool>? toSignal = null;
            lock (_sync)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _completionReason = reason;
                _retentionTouchedAtUtc = DateTimeOffset.UtcNow;
                _completedAtUtc = _retentionTouchedAtUtc.Value.ToString("O");
                toSignal = RotateWakeLocked();
            }

            toSignal.TrySetResult(true);
        }

        public SubscriptionDrain Drain(int maxBatch)
        {
            lock (_sync)
            {
                if (_completed)
                {
                    _retentionTouchedAtUtc = DateTimeOffset.UtcNow;
                }

                var batch = new List<SubscriptionEvent>(Math.Min(maxBatch, _queue.Count));
                var payloadChars = 0;
                while (batch.Count < maxBatch && _queue.Count > 0)
                {
                    var next = _queue.Peek();
                    if ((long)payloadChars + next.PayloadChars > MaxPayloadChars)
                    {
                        break;
                    }

                    _queue.Dequeue();
                    batch.Add(next.Event);
                    payloadChars = SaturatingAdd(payloadChars, next.PayloadChars);
                }

                var dropped = _dropped;
                var coalesced = _coalesced;
                var truncated = _truncated;
                _dropped = 0;
                _coalesced = 0;
                _truncated = 0;

                return new SubscriptionDrain(
                    batch.ToArray(),
                    dropped,
                    _droppedTotal,
                    coalesced,
                    _coalescedTotal,
                    truncated,
                    _truncatedTotal,
                    _queue.Count > 0,
                    _completed,
                    _completionReason,
                    _completedAtUtc);
            }
        }

        public Task WaitForEventAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_queue.Count > 0 || _completed)
                {
                    return Task.CompletedTask;
                }

                return _wake.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task ReleaseResourceAsync()
        {
            await _releaseGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _resourceReleased) != 0)
                {
                    return;
                }

                await (_releaseResource?.Invoke() ?? Task.CompletedTask).ConfigureAwait(false);
                Volatile.Write(ref _resourceReleased, 1);
                ReleaseCapacity();
            }
            finally
            {
                _releaseGate.Release();
            }
        }

        public void ReleaseCapacity()
        {
            if (Interlocked.Exchange(ref _capacityReleased, 1) == 0)
            {
                _releaseCapacity?.Invoke();
            }
        }

        public void RequestStop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            CancelWorker();
        }

        public bool TryScheduleRetirement() =>
            Interlocked.Exchange(ref _retirementScheduled, 1) == 0;

        public bool TryRequestRetirement(TimeSpan retention, out TimeSpan retryAfter)
        {
            var shouldRetire = false;
            lock (_sync)
            {
                if (IsStopping)
                {
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                var touchedAt = _retentionTouchedAtUtc ?? DateTimeOffset.UtcNow;
                retryAfter = retention - (DateTimeOffset.UtcNow - touchedAt);
                if (_completed && retryAfter <= TimeSpan.Zero)
                {
                    Interlocked.Exchange(ref _stopRequested, 1);
                    retryAfter = TimeSpan.Zero;
                    shouldRetire = true;
                }
            }

            if (shouldRetire)
            {
                CancelWorker();
            }

            return shouldRetire;
        }

        private void CancelWorker()
        {

            try
            {
                Cts.Cancel();
            }
            catch
            {
            }
        }

        public async Task StopAsync()
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
                await ReleaseResourceAsync().ConfigureAwait(false);
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

        private TaskCompletionSource<bool> RotateWakeLocked()
        {
            var wake = _wake;
            _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return wake;
        }

        private static JsonNode? TryCompactObservationPayload(string kind, JsonNode payload)
        {
            if (!string.Equals(kind, "property_initial", StringComparison.Ordinal) &&
                !string.Equals(kind, "property_changed", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var observationEvent = payload.Deserialize<ObserveStateEvent>();
                if (observationEvent is null)
                {
                    return null;
                }

                return JsonSerializer.SerializeToNode(observationEvent with
                {
                    OldValue = observationEvent.OldValue is null
                        ? null
                        : CompactObservationValue(observationEvent.OldValue),
                    NewValue = CompactObservationValue(observationEvent.NewValue),
                    Visual = null
                });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static ObserveStateValue CompactObservationValue(ObserveStateValue value) =>
            value.State switch
            {
                ObserveStateValueState.Value or ObserveStateValueState.Error => value with
                {
                    Value = null,
                    ValueType = null,
                    Truncated = true,
                    Error = null
                },
                _ => value with { ValueType = null }
            };

        private static int SaturatingAdd(int current, long value)
        {
            var sum = (long)current + value;
            return sum >= int.MaxValue ? int.MaxValue : (int)sum;
        }
    }

    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _propertySubscriptionSlotsSync = new();
    private readonly Dictionary<string, int> _propertySubscriptionSlotsBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private int _propertySubscriptionSlots;

    public void Dispose()
    {
        _lifetimeCts.Cancel();
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
        _lifetimeCts.Dispose();
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

    public SubscribePropertyChangesResponse SubscribePropertyChanges(
        string sessionId,
        AutomationController automation,
        WpfStateObservation observation,
        PropertySubscriptionReservation reservation,
        int cadenceMs,
        int maxQueue,
        int maxPayloadChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(reservation);
        if (!string.Equals(reservation.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Property subscription reservation belongs to another session.");
        }

        var releaseSlot = reservation.Transfer();
        var slotTransferred = false;
        try
        {
            var subscriptionId = Guid.NewGuid().ToString("N");
            var effectiveCadenceMs = Math.Clamp(cadenceMs, 20, 10_000);
            var effectiveMaxQueue = Math.Clamp(maxQueue, 1, 1_000);
            var effectiveMaxPayloadChars = Math.Clamp(maxPayloadChars, 4_096, 1_048_576);
            var cts = new CancellationTokenSource();
            var state = new SubscriptionState(
                subscriptionId,
                sessionId,
                SubscriptionKind.PropertyChanges,
                effectiveMaxQueue,
                cts,
                effectiveMaxPayloadChars,
                () => ReleasePropertyObservationAsync(automation, observation),
                releaseSlot);

            if (!_subscriptions.TryAdd(subscriptionId, state))
            {
                cts.Dispose();
                throw new InvalidOperationException("Failed to register subscription.");
            }

            slotTransferred = true;

            foreach (var initialEvent in observation.Started.InitialEvents)
            {
                state.Enqueue("property_initial", JsonSerializer.SerializeToNode(initialEvent)!);
            }

            state.Worker = Task.Run(() => RunPropertySubscriptionAsync(
                state,
                automation,
                observation,
                effectiveCadenceMs));

            var started = observation.Started;
            return new SubscribePropertyChangesResponse(
                subscriptionId,
                started.Element,
                started.Watches,
                started.StartedAtUtc,
                started.ExpiresAtUtc,
                effectiveCadenceMs,
                started.DurationMs,
                started.MaxNodes,
                effectiveMaxQueue,
                started.MaxValueLength,
                effectiveMaxPayloadChars);
        }
        finally
        {
            if (!slotTransferred)
            {
                releaseSlot();
            }
        }
    }

    private async Task RunPropertySubscriptionAsync(
        SubscriptionState state,
        AutomationController automation,
        WpfStateObservation observation,
        int cadenceMs)
    {
        try
        {
            while (!state.Token.IsCancellationRequested)
            {
                var source = await automation.RunExclusiveAsync(
                    () => automation.ObserveStatePollAsync(
                        observation,
                        maxBatch: 500,
                        maxPayloadChars: state.MaxPayloadChars,
                        CancellationToken.None),
                    state.Token).ConfigureAwait(false);

                state.AddDeliveryMetrics(
                    source.DroppedSinceLastPoll,
                    source.CoalescedSinceLastPoll,
                    source.TruncatedSinceLastPoll);

                foreach (var observationEvent in source.Events)
                {
                    state.Enqueue(
                        "property_changed",
                        JsonSerializer.SerializeToNode(observationEvent)!);
                }

                if (source.HasMore)
                {
                    continue;
                }

                if (source.Completed)
                {
                    await CompletePropertySubscriptionAsync(
                        state,
                        GetCompletionReason(source.StopReason)).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(cadenceMs), state.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Token.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException ex) when (IsObservationConnectionLost(ex))
        {
            if (!state.IsStopping)
            {
                await CompletePropertySubscriptionAsync(state, "agent_connection_lost").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (!state.IsStopping)
            {
                await CompletePropertySubscriptionAsync(state, "source_error", ex).ConfigureAwait(false);
            }
        }
    }

    private async Task CompletePropertySubscriptionAsync(
        SubscriptionState state,
        string reason,
        Exception? sourceError = null)
    {
        if (sourceError is not null)
        {
            state.Enqueue(
                "subscription_error",
                JsonSerializer.SerializeToNode(new { message = sourceError.GetBaseException().Message })!);
        }

        try
        {
            await state.ReleaseResourceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            state.Enqueue(
                "subscription_error",
                JsonSerializer.SerializeToNode(new
                {
                    message = ex.GetBaseException().Message,
                    operation = "release_source"
                })!);
            reason = "source_release_failed";
        }

        state.Complete(reason);
        RetainCompletedSubscription(state);
    }

    private static async Task ReleasePropertyObservationAsync(
        AutomationController automation,
        WpfStateObservation observation)
    {
        using var releaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await automation.RunExclusiveAsync(
            () => automation.ReleaseObserveStateAsync(observation, releaseCts.Token),
            releaseCts.Token).ConfigureAwait(false);
    }

    public PropertySubscriptionReservation ReservePropertySubscription(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_propertySubscriptionSlotsSync)
        {
            _propertySubscriptionSlotsBySession.TryGetValue(sessionId, out var sessionCount);
            if (sessionCount >= MaxPropertySubscriptionsPerSession)
            {
                throw new InvalidOperationException(
                    $"subscription_limit_exceeded: a session supports at most " +
                    $"{MaxPropertySubscriptionsPerSession} active property subscriptions.");
            }

            if (_propertySubscriptionSlots >= MaxPropertySubscriptionsTotal)
            {
                throw new InvalidOperationException(
                    $"subscription_limit_exceeded: the server supports at most " +
                    $"{MaxPropertySubscriptionsTotal} active property subscriptions.");
            }

            _propertySubscriptionSlotsBySession[sessionId] = sessionCount + 1;
            _propertySubscriptionSlots++;
        }

        var released = 0;
        return new PropertySubscriptionReservation(sessionId, () =>
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return;
            }

            lock (_propertySubscriptionSlotsSync)
            {
                if (_propertySubscriptionSlotsBySession.TryGetValue(sessionId, out var sessionCount))
                {
                    if (sessionCount <= 1)
                    {
                        _propertySubscriptionSlotsBySession.Remove(sessionId);
                    }
                    else
                    {
                        _propertySubscriptionSlotsBySession[sessionId] = sessionCount - 1;
                    }
                }

                _propertySubscriptionSlots = Math.Max(0, _propertySubscriptionSlots - 1);
            }
        });
    }

    private static bool IsObservationConnectionLost(Exception ex) =>
        ex.Message.StartsWith(
            "observe_state_connection_lost:",
            StringComparison.OrdinalIgnoreCase);

    private static string GetCompletionReason(ObserveStateStopReason? reason) => reason switch
    {
        ObserveStateStopReason.DurationElapsed => "duration_elapsed",
        ObserveStateStopReason.ElementUnloaded => "element_unloaded",
        ObserveStateStopReason.ClientRequested => "client_requested",
        _ => "completed"
    };

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

        var drain = state.Drain(batchSize);
        if (drain.Events.Count > 0 || drain.HasDeliveryMetrics || drain.Completed || timeoutMs <= 0)
        {
            return ToPollSubscriptionResponse(drain);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, 60_000));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Token);
        linked.CancelAfter(timeout);

        try
        {
            await state.WaitForEventAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        var afterWait = state.Drain(batchSize);
        var combined = afterWait with
        {
            Dropped = SaturatingAdd(drain.Dropped, afterWait.Dropped),
            Coalesced = SaturatingAdd(drain.Coalesced, afterWait.Coalesced),
            Truncated = SaturatingAdd(drain.Truncated, afterWait.Truncated)
        };
        return ToPollSubscriptionResponse(combined);
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

        state.RequestStop();
        try
        {
            await state.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            RetainCompletedSubscription(state);
            throw;
        }

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

        await Task.WhenAll(states.Select(StopForSessionEndAsync)).ConfigureAwait(false);
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

    private void RetainCompletedSubscription(SubscriptionState state)
    {
        if (state.TryScheduleRetirement())
        {
            _ = RetireCompletedSubscriptionAsync(state, _lifetimeCts.Token);
        }
    }

    private async Task RetireCompletedSubscriptionAsync(
        SubscriptionState state,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_subscriptions.TryGetValue(state.SubscriptionId, out var current) ||
                !ReferenceEquals(current, state))
            {
                return;
            }

            if (!state.ResourceReleased)
            {
                try
                {
                    await Task.Delay(ResourceReleaseRetryDelay, cancellationToken).ConfigureAwait(false);
                    await state.ReleaseResourceAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    continue;
                }
            }

            if (!state.TryRequestRetirement(CompletedSubscriptionRetention, out var retryAfter))
            {
                try
                {
                    await Task.Delay(
                        retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMilliseconds(100),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await state.StopAsync().ConfigureAwait(false);
                _subscriptions.TryRemove(state.SubscriptionId, out _);
                return;
            }
            catch
            {
                // Preserve the handle and retry; a healthy owning connection may still hold it.
            }

            try
            {
                await Task.Delay(ResourceReleaseRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task StopForSessionEndAsync(SubscriptionState state)
    {
        try
        {
            await state.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Controller disposal closes the owning connection and releases target-side resources.
        }
        finally
        {
            // Session teardown disposes the owning controller immediately after this callback.
            state.ReleaseCapacity();
        }
    }

    private static PollSubscriptionResponse ToPollSubscriptionResponse(SubscriptionDrain drain) =>
        new(
            drain.Events,
            drain.Dropped,
            drain.HasMore,
            drain.DroppedTotal,
            drain.Coalesced,
            drain.CoalescedTotal,
            drain.Truncated,
            drain.TruncatedTotal,
            drain.Completed,
            drain.CompletionReason,
            drain.CompletedAtUtc);

    private static int SaturatingAdd(int left, int right)
    {
        var sum = (long)left + right;
        return sum >= int.MaxValue ? int.MaxValue : (int)sum;
    }
}
