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
    private const int MaxEnvelopeXPathChars = 2_000;
    private const int MaxEnvelopeIdentityChars = 128;

    internal sealed record QueuedSubscriptionEvent(SubscriptionEvent Event, int SerializedChars);

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

    internal sealed record SubscriptionDrain(
        IReadOnlyList<SubscriptionEvent> Events,
        int DroppedSinceLastPoll,
        int DroppedTotal,
        int CoalescedSinceLastPoll,
        int CoalescedTotal,
        int TruncatedSinceLastPoll,
        int TruncatedTotal,
        bool HasMore,
        bool Completed,
        string? CompletionReason,
        string? CompletedAtUtc)
    {
        public bool HasDeliveryMetrics =>
            DroppedSinceLastPoll > 0 ||
            CoalescedSinceLastPoll > 0 ||
            TruncatedSinceLastPoll > 0;
    }

    internal sealed class SubscriptionState : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<QueuedSubscriptionEvent> _queue = new();
        private readonly Func<Task>? _releaseResource;
        private readonly Action? _releaseCapacity;
        private readonly SemaphoreSlim _releaseGate = new(1, 1);
        private readonly CancellationToken _token;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly string _sourceKind;
        private readonly long? _windowHandle;
        private readonly string? _elementId;
        private readonly string? _xpath;
        private readonly bool? _xpathOmitted;

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
            Action? releaseCapacity = null,
            long? windowHandle = null,
            string? elementId = null,
            string? xpath = null,
            Func<DateTimeOffset>? utcNow = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            if (maxQueue < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueue));
            }

            if (maxPayloadChars < 1_024)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPayloadChars));
            }

            SubscriptionId = subscriptionId;
            SessionId = sessionId;
            Kind = kind;
            MaxQueue = maxQueue;
            MaxPayloadChars = maxPayloadChars;
            Cts = cts;
            _token = cts.Token;
            _releaseResource = releaseResource;
            _releaseCapacity = releaseCapacity;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _sourceKind = kind switch
            {
                SubscriptionKind.BindingErrors => RuntimeEventSourceKinds.BindingErrors,
                SubscriptionKind.PropertyChanges => RuntimeEventSourceKinds.PropertyChanges,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            _windowHandle = windowHandle is > 0 ? windowHandle : null;
            _elementId = BoundIdentity(elementId);

            xpath = string.IsNullOrWhiteSpace(xpath) ? null : xpath.Trim();
            if (xpath is not null && xpath.Length > MaxEnvelopeXPathChars)
            {
                _xpathOmitted = true;
            }
            else
            {
                _xpath = xpath;
            }
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
        internal DateTimeOffset UtcNow => _utcNow().ToUniversalTime();

        public void Enqueue(
            string kind,
            JsonNode payload,
            DateTimeOffset? observedAtUtc = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(payload);
            if (IsStopping)
            {
                return;
            }

            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                if (_completed || IsStopping)
                {
                    return;
                }

                var sequence = ++_sequence;
                var subscriptionEvent = CreateEvent(
                    sequence,
                    kind,
                    payload,
                    (observedAtUtc ?? _utcNow()).ToUniversalTime());
                var serializedChars = GetSerializedChars(subscriptionEvent);
                var payloadTruncated = false;
                if (serializedChars > MaxPayloadChars)
                {
                    payload = TryCompactObservationPayload(kind, payload) ??
                              JsonSerializer.SerializeToNode(new
                              {
                                  truncated = true,
                                  reason = "subscription_event_limit",
                                  originalEventChars = serializedChars,
                                  maxPayloadChars = MaxPayloadChars
                              })!;
                    subscriptionEvent = CreateEvent(
                        sequence,
                        kind,
                        payload,
                        subscriptionEvent.Envelope!.ObservedAtUtc);
                    serializedChars = GetSerializedChars(subscriptionEvent);
                    payloadTruncated = true;
                }

                if (serializedChars > MaxPayloadChars)
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

                    _queue.Enqueue(new QueuedSubscriptionEvent(
                        subscriptionEvent,
                        serializedChars));

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

        public bool Complete(string reason, DiagnosticCauseInfo? cause = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            TaskCompletionSource<bool> toSignal;
            lock (_sync)
            {
                if (_completed)
                {
                    return false;
                }

                var completedAtUtc = _utcNow().ToUniversalTime();
                var sequence = ++_sequence;
                var terminalEvent = CreateTerminalEvent(
                    sequence,
                    reason,
                    completedAtUtc,
                    cause,
                    out var serializedChars);
                if (serializedChars > MaxPayloadChars)
                {
                    throw new InvalidOperationException(
                        "The configured subscription event budget cannot contain the terminal event.");
                }

                if (_queue.Count >= MaxQueue)
                {
                    _queue.Dequeue();
                    _dropped = SaturatingAdd(_dropped, 1);
                    _droppedTotal = SaturatingAdd(_droppedTotal, 1);
                }

                _queue.Enqueue(new QueuedSubscriptionEvent(terminalEvent, serializedChars));
                _completed = true;
                _completionReason = reason;
                _retentionTouchedAtUtc = completedAtUtc;
                _completedAtUtc = _retentionTouchedAtUtc.Value.ToString("O");
                toSignal = RotateWakeLocked();
            }

            toSignal.TrySetResult(true);
            return true;
        }

        private SubscriptionEvent CreateTerminalEvent(
            int sequence,
            string reason,
            DateTimeOffset completedAtUtc,
            DiagnosticCauseInfo? cause,
            out int serializedChars)
        {
            var terminalEvent = BuildTerminalEvent(
                sequence,
                reason,
                completedAtUtc,
                cause,
                causeTruncated: false);
            serializedChars = GetSerializedChars(terminalEvent);
            if (serializedChars <= MaxPayloadChars || cause is null)
            {
                return terminalEvent;
            }

            var compactCause = new DiagnosticCauseInfo(
                BoundTerminalText(cause.Type, 256) ?? nameof(Exception))
            {
                Message = BoundTerminalText(cause.Message, 512),
                Details = cause.Message is null
                    ? BoundTerminalText(cause.Details, 512)
                    : null,
                MessageUnavailableReason = cause.Message is null && cause.Details is null
                    ? BoundTerminalText(cause.MessageUnavailableReason, 512)
                    : null
            };
            terminalEvent = BuildTerminalEvent(
                sequence,
                reason,
                completedAtUtc,
                compactCause,
                causeTruncated: true);
            serializedChars = GetSerializedChars(terminalEvent);
            if (serializedChars <= MaxPayloadChars)
            {
                return terminalEvent;
            }

            terminalEvent = BuildTerminalEvent(
                sequence,
                reason,
                completedAtUtc,
                new DiagnosticCauseInfo(BoundTerminalText(cause.Type, 128) ?? nameof(Exception)),
                causeTruncated: true);
            serializedChars = GetSerializedChars(terminalEvent);
            if (serializedChars <= MaxPayloadChars)
            {
                return terminalEvent;
            }

            terminalEvent = BuildTerminalEvent(
                sequence,
                reason,
                completedAtUtc,
                cause: null,
                causeTruncated: true);
            serializedChars = GetSerializedChars(terminalEvent);
            return terminalEvent;
        }

        private SubscriptionEvent BuildTerminalEvent(
            int sequence,
            string reason,
            DateTimeOffset completedAtUtc,
            DiagnosticCauseInfo? cause,
            bool causeTruncated)
        {
            var payload = JsonSerializer.SerializeToNode(
                new SubscriptionTerminalEvent(reason, completedAtUtc)
                {
                    Cause = cause,
                    CauseTruncated = causeTruncated ? true : null
                })!;
            return CreateEvent(
                sequence,
                SubscriptionEventKinds.Terminal,
                payload,
                completedAtUtc);
        }

        internal static string? BoundTerminalText(string? value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= maximumLength)
            {
                return trimmed;
            }

            var length = maximumLength;
            if (length > 0 &&
                char.IsHighSurrogate(trimmed[length - 1]) &&
                char.IsLowSurrogate(trimmed[length]))
            {
                length--;
            }

            return trimmed[..length];
        }

        public SubscriptionDrain Drain(int maxBatch)
        {
            lock (_sync)
            {
                if (_completed)
                {
                    _retentionTouchedAtUtc = _utcNow();
                }

                var batch = new List<SubscriptionEvent>(Math.Min(maxBatch, _queue.Count));
                var payloadChars = 0;
                while (batch.Count < maxBatch && _queue.Count > 0)
                {
                    var next = _queue.Peek();
                    if ((long)payloadChars + next.SerializedChars > MaxPayloadChars)
                    {
                        break;
                    }

                    _queue.Dequeue();
                    batch.Add(next.Event);
                    payloadChars = SaturatingAdd(payloadChars, next.SerializedChars);
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

                var now = _utcNow();
                var touchedAt = _retentionTouchedAtUtc ?? now;
                retryAfter = retention - (now - touchedAt);
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

        private SubscriptionEvent CreateEvent(
            int sequence,
            string kind,
            JsonNode payload,
            DateTimeOffset observedAtUtc) =>
            new(sequence, kind, payload)
            {
                Envelope = new RuntimeEventEnvelope(
                    Version: RuntimeEventVersions.V1,
                    ObservedAtUtc: observedAtUtc.ToUniversalTime(),
                    SourceKind: _sourceKind,
                    SessionId: SessionId,
                    StreamId: SubscriptionId,
                    Sequence: sequence,
                    WindowHandle: _windowHandle,
                    ElementId: _elementId,
                    XPath: _xpath,
                    XPathOmitted: _xpathOmitted)
            };

        private static int GetSerializedChars(SubscriptionEvent subscriptionEvent) =>
            JsonSerializer.Serialize(subscriptionEvent).Length;

        private static string? BoundIdentity(string? value)
        {
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            return value is not null && value.Length <= MaxEnvelopeIdentityChars
                ? value
                : null;
        }

        private static JsonNode? TryCompactObservationPayload(string kind, JsonNode payload)
        {
            if (!string.Equals(kind, SubscriptionEventKinds.PropertyInitial, StringComparison.Ordinal) &&
                !string.Equals(kind, SubscriptionEventKinds.PropertyChanged, StringComparison.Ordinal))
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
            finally
            {
                sub.ReleaseCapacity();
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
        int maxQueue,
        int maxPayloadChars = 262_144)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(automation);

        var subscriptionId = Guid.NewGuid().ToString("N");
        var effectivePollIntervalMs = Math.Clamp(pollIntervalMs, 50, 60_000);
        var effectiveMaxQueue = Math.Clamp(maxQueue, 1, 1_000);
        var effectiveMaxPayloadChars = Math.Clamp(maxPayloadChars, 4_096, 1_048_576);
        var cts = new CancellationTokenSource();

        var state = new SubscriptionState(
            subscriptionId: subscriptionId,
            sessionId: sessionId,
            kind: SubscriptionKind.BindingErrors,
            maxQueue: effectiveMaxQueue,
            cts: cts,
            maxPayloadChars: effectiveMaxPayloadChars,
            windowHandle: windowHandleUsed,
            xpath: rootXPath);

        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            throw new InvalidOperationException("Failed to register subscription.");
        }

        state.Worker = Task.Run(() => RunBindingSubscriptionAsync(
            state,
            cancellationToken => automation.RunExclusiveAsync(
                () => automation.GetBindingErrorsAsync(
                    windowHandleUsed,
                    rootXPath,
                    depth,
                    maxErrors,
                    maxNodes,
                    cancellationToken),
                cancellationToken),
            TimeSpan.FromMilliseconds(effectivePollIntervalMs),
            exception => ClassifySubscriptionFailure(exception, automation)));

        return new SubscribeBindingErrorsResponse(
            subscriptionId,
            effectivePollIntervalMs,
            effectiveMaxQueue,
            effectiveMaxPayloadChars);
    }

    internal async Task RunBindingSubscriptionAsync(
        SubscriptionState state,
        Func<CancellationToken, Task<GetBindingErrorsResponse>> scan,
        TimeSpan pollDelay,
        Func<Exception, string> classifyFailure)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(classifyFailure);
        var lastKeys = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (!state.Token.IsCancellationRequested)
            {
                var response = await scan(state.Token).ConfigureAwait(false);
                PublishBindingErrors(state, response, lastKeys, state.Token);
                await Task.Delay(pollDelay, state.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!state.IsStopping && state.Complete(
                    classifyFailure(ex),
                    FailureDiagnostics.CreateDiagnosticCause(ex)))
            {
                RetainCompletedSubscription(state);
            }
        }
    }

    public SubscribePropertyChangesResponse SubscribePropertyChanges(
        string sessionId,
        AutomationController automation,
        WpfStateObservation observation,
        PropertySubscriptionReservation reservation,
        int cadenceMs,
        int maxQueue,
        int maxPayloadChars) =>
        SubscribePropertyChanges(
            sessionId,
            automation,
            windowHandleUsed: null,
            observation,
            reservation,
            cadenceMs,
            maxQueue,
            maxPayloadChars);

    public SubscribePropertyChangesResponse SubscribePropertyChanges(
        string sessionId,
        AutomationController automation,
        long? windowHandleUsed,
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
                releaseResource: () => ReleasePropertyObservationAsync(automation, observation),
                releaseCapacity: releaseSlot,
                windowHandle: windowHandleUsed,
                elementId: observation.Started.Element.ElementId,
                xpath: observation.Started.Element.XPath);

            if (!_subscriptions.TryAdd(subscriptionId, state))
            {
                cts.Dispose();
                throw new InvalidOperationException("Failed to register subscription.");
            }

            slotTransferred = true;

            foreach (var initialEvent in observation.Started.InitialEvents)
            {
                state.Enqueue(
                    SubscriptionEventKinds.PropertyInitial,
                    JsonSerializer.SerializeToNode(initialEvent)!,
                    initialEvent.ObservedAtUtc);
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
                        SubscriptionEventKinds.PropertyChanged,
                        JsonSerializer.SerializeToNode(observationEvent)!,
                        observationEvent.ObservedAtUtc);
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
                await CompletePropertySubscriptionAsync(
                    state,
                    automation.IsAttached
                        ? SubscriptionTerminalCodes.AgentConnectionLost
                        : SubscriptionTerminalCodes.TargetExited,
                    ex).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (!state.IsStopping)
            {
                await CompletePropertySubscriptionAsync(
                    state,
                    ClassifySubscriptionFailure(ex, automation),
                    ex).ConfigureAwait(false);
            }
        }
    }

    internal async Task CompletePropertySubscriptionAsync(
        SubscriptionState state,
        string reason,
        Exception? failure = null)
    {
        Exception? releaseFailure = null;
        try
        {
            await state.ReleaseResourceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (failure is null)
            {
                reason = SubscriptionTerminalCodes.SourceReleaseFailed;
                failure = ex;
            }
            else
            {
                releaseFailure = ex;
            }
        }

        var cause = failure is null
            ? null
            : FailureDiagnostics.CreateDiagnosticCause(failure);
        if (cause is not null && releaseFailure is not null)
        {
            cause = AppendResourceReleaseFailure(cause, releaseFailure);
        }

        if (state.Complete(reason, cause))
        {
            RetainCompletedSubscription(state);
        }
    }

    private static DiagnosticCauseInfo AppendResourceReleaseFailure(
        DiagnosticCauseInfo primaryCause,
        Exception releaseFailure)
    {
        var releaseCause = FailureDiagnostics.CreateDiagnosticCause(releaseFailure);
        var releaseSummary = $"{SubscriptionTerminalCodes.SourceReleaseFailed}: {releaseCause.Type}";
        if (!string.IsNullOrWhiteSpace(releaseCause.Message))
        {
            releaseSummary += $": {releaseCause.Message}";
        }
        else if (!string.IsNullOrWhiteSpace(releaseCause.MessageUnavailableReason))
        {
            releaseSummary += $" (message unavailable: {releaseCause.MessageUnavailableReason})";
        }

        if (!string.IsNullOrWhiteSpace(releaseCause.Details))
        {
            releaseSummary += $" Details: {releaseCause.Details}";
        }

        var boundedReleaseSummary = SubscriptionState.BoundTerminalText(releaseSummary, 1_024)!;
        var boundedPrimaryDetails = SubscriptionState.BoundTerminalText(
            primaryCause.Details,
            4_096 - boundedReleaseSummary.Length - 1);
        var combinedDetails = boundedPrimaryDetails is null
            ? boundedReleaseSummary
            : $"{boundedPrimaryDetails}\n{boundedReleaseSummary}";
        return primaryCause with
        {
            Details = combinedDetails
        };
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
        ObserveStateStopReason.DurationElapsed => SubscriptionTerminalCodes.DurationElapsed,
        ObserveStateStopReason.ElementUnloaded => SubscriptionTerminalCodes.ElementUnloaded,
        ObserveStateStopReason.ClientRequested => SubscriptionTerminalCodes.ClientRequested,
        _ => SubscriptionTerminalCodes.Completed
    };

    internal static string ClassifySubscriptionFailure(
        Exception exception,
        AutomationController automation)
    {
        for (Exception? current = exception, previous = null;
             current is not null && !ReferenceEquals(current, previous);
             previous = current, current = current.InnerException)
        {
            if (current is not ActionableFailureException actionable)
            {
                continue;
            }

            if (string.Equals(actionable.Failure.Code, "target_exited", StringComparison.Ordinal) ||
                string.Equals(actionable.Failure.Code, "process_replaced", StringComparison.Ordinal))
            {
                return SubscriptionTerminalCodes.TargetExited;
            }

            return SubscriptionTerminalCodes.SourceError;
        }

        return automation.IsAttached
            ? SubscriptionTerminalCodes.SourceError
            : SubscriptionTerminalCodes.TargetExited;
    }

    private static void PublishBindingErrors(
        SubscriptionState state,
        GetBindingErrorsResponse response,
        HashSet<string> lastKeys,
        CancellationToken cancellationToken)
    {
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var newErrors = new List<BindingErrorInfo>();
        var observedAtUtc = state.UtcNow;

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
                kind: SubscriptionEventKinds.BindingErrorAdded,
                payload: JsonSerializer.SerializeToNode(error)!,
                observedAtUtc: observedAtUtc);
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
            DroppedSinceLastPoll = SaturatingAdd(
                drain.DroppedSinceLastPoll,
                afterWait.DroppedSinceLastPoll),
            CoalescedSinceLastPoll = SaturatingAdd(
                drain.CoalescedSinceLastPoll,
                afterWait.CoalescedSinceLastPoll),
            TruncatedSinceLastPoll = SaturatingAdd(
                drain.TruncatedSinceLastPoll,
                afterWait.TruncatedSinceLastPoll)
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
        state.ReleaseCapacity();
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

        throw new InvalidOperationException($"subscription_not_found: Unknown subscriptionId '{subscriptionId}'.");
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
                state.ReleaseCapacity();
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
            drain.DroppedSinceLastPoll,
            drain.HasMore,
            drain.DroppedTotal,
            drain.CoalescedSinceLastPoll,
            drain.CoalescedTotal,
            drain.TruncatedSinceLastPoll,
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
