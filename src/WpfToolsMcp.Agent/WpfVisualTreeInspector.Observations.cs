using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const int MaxObservationWatches = 32;
    private const int MaxObservationDurationMs = 300_000;
    private const int MaxObservationEvents = 1_000;
    private const int MaxObservationBatch = 500;
    private const int MaxObservationPollSizingRetries = 3;
    private const int MaxObservationValueLength = 4_096;
    private const int MaxObservationNodes = 20_000;
    private const int MaxObservationPathLength = 512;
    private const int MaxObservationPayloadChars = 1_048_576;

    private static readonly ObserveStateRegistry ObserveStates = new();
    private static readonly object UnavailableObservationValue = new();
    private static readonly JsonSerializerOptions ObservationJsonOptions = new();

    public static ObserveStateStartResponse StartObserveState(
        string ownerId,
        ObserveStateStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);
        EnsureObservationDispatcherAccess(ResolveObservationDispatcher(request.WindowHandle));

        var requestedWatchCount = (long)(request.DependencyProperties?.Count ?? 0)
            + (request.DataContextPaths?.Count ?? 0);
        if (requestedWatchCount > MaxObservationWatches)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"A state observation supports at most {MaxObservationWatches} watches.");
        }

        var durationMs = Math.Clamp(request.DurationMs, 1, MaxObservationDurationMs);
        var maxEvents = Math.Clamp(request.MaxEvents, 1, MaxObservationEvents);
        var maxValueLength = Math.Clamp(request.MaxValueLength, 1, MaxObservationValueLength);
        var maxNodes = Math.Clamp(request.MaxNodes, 1, MaxObservationNodes);
        var requestedDependencyProperties = NormalizeRequestedNames(
            request.DependencyProperties,
            nameof(request.DependencyProperties));
        var requestedDataContextPaths = NormalizeRequestedNames(
            request.DataContextPaths,
            nameof(request.DataContextPaths));

        if (requestedDependencyProperties.Count + requestedDataContextPaths.Count is 0)
        {
            throw new ArgumentException(
                "invalid_request: provide at least one dependency property or DataContext path.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();
        var resolved = ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            request.Locator,
            request.ElementId,
            request.WindowHandle,
            visibleOnly: true,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var element = resolved.Element;

        var definitions = BuildObservationWatchDefinitions(
            element,
            requestedDependencyProperties,
            requestedDataContextPaths,
            cancellationToken);
        if (definitions.Count is 0)
        {
            throw new ArgumentException("invalid_request: all requested watches were duplicates.");
        }

        var elementRef = BuildElementRefWpf(ownerId, element, resolved.XPath, FindReturnFields.Standard);
        var recording = new ObserveStateRecording(
            ownerId,
            element,
            elementRef,
            durationMs,
            maxEvents,
            maxValueLength,
            request.IncludeVisualMetadata);

        try
        {
            var initialEvents = recording.Attach(definitions);
            recording.Activate();
            ObserveStates.Add(recording);

            return new ObserveStateStartResponse(
                recording.Id,
                elementRef,
                recording.StartedAtUtc,
                recording.ExpiresAtUtc,
                durationMs,
                maxEvents,
                maxValueLength,
                maxNodes,
                definitions.Select(definition => definition.Watch).ToArray(),
                initialEvents);
        }
        catch
        {
            recording.Release();
            throw;
        }
    }

    public static ObserveStatePollResponse PollObserveState(string ownerId, ObserveStatePollRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);
        var recording = ObserveStates.Get(ownerId, request.ObservationId);
        return recording.Poll(request.MaxBatch, request.MaxPayloadChars);
    }

    public static ObserveStateStopResponse StopObserveState(string ownerId, ObserveStateStopRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);

        var recording = ObserveStates.Get(ownerId, request.ObservationId);
        EnsureObservationDispatcherAccess(recording.Dispatcher);
        recording = ObserveStates.Remove(ownerId, request.ObservationId);
        var stopped = recording.Complete(ObserveStateStopReason.ClientRequested);
        return recording.CreateStopResponse(stopped);
    }

    public static Dispatcher ResolveObserveStateDispatcher(string ownerId, string observationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return ObserveStates.Get(ownerId, observationId).Dispatcher;
    }

    public static async Task ReleaseOwnerObservationsAsync(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var recordings = ObserveStates.RemoveOwner(ownerId);
        await Task.WhenAll(recordings.Select(ReleaseRecordingAsync)).ConfigureAwait(false);
    }

    private static async Task ReleaseRecordingAsync(ObserveStateRecording recording)
    {
        var dispatcher = recording.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                recording.Release();
                return;
            }

            var operation = dispatcher.InvokeAsync(recording.Release, DispatcherPriority.Send);
            await operation.Task.ConfigureAwait(false);
        }
        catch
        {
            // The owning UI thread may shut down while the pipe is disconnecting.
        }
    }

    private static IReadOnlyList<string> NormalizeRequestedNames(
        IReadOnlyList<string>? requested,
        string parameterName)
    {
        if (requested is null)
        {
            return Array.Empty<string>();
        }

        var normalized = new string[requested.Count];
        for (var index = 0; index < requested.Count; index++)
        {
            var value = requested[index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"invalid_request: {parameterName} cannot contain blank values.",
                    parameterName);
            }

            value = value.Trim();
            if (value.Length > MaxObservationPathLength)
            {
                throw new ArgumentException(
                    $"invalid_request: {parameterName} entries cannot exceed {MaxObservationPathLength} characters.",
                    parameterName);
            }

            normalized[index] = value;
        }

        return normalized;
    }

    private static IReadOnlyList<ObserveStateWatchDefinition> BuildObservationWatchDefinitions(
        DependencyObject element,
        IReadOnlyList<string> dependencyPropertyNames,
        IReadOnlyList<string> dataContextPaths,
        CancellationToken cancellationToken)
    {
        var definitions = new List<ObserveStateWatchDefinition>();
        var properties = new HashSet<DependencyProperty>(GetDependencyPropertiesCached(element.GetType()));
        try
        {
            var enumerator = element.GetLocalValueEnumerator();
            while (enumerator.MoveNext())
            {
                properties.Add(enumerator.Current.Property);
            }
        }
        catch
        {
        }

        var seenProperties = new HashSet<DependencyProperty>();
        var missingProperties = new List<string>();
        foreach (var requestedName in dependencyPropertyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePropertyByName(element.GetType(), properties, requestedName, out var property))
            {
                missingProperties.Add(requestedName);
                continue;
            }

            if (!seenProperties.Add(property))
            {
                continue;
            }

            var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType())
                ?? throw new InvalidOperationException(
                    $"observe_state_unsupported: '{requestedName}' cannot raise change notifications on {element.GetType().Name}.");
            definitions.Add(new ObserveStateWatchDefinition(
                new ObserveStateWatch(ObserveStateSource.DependencyProperty, requestedName),
                property,
                descriptor));
        }

        if (missingProperties.Count > 0)
        {
            throw new ArgumentException(
                "invalid_request: unknown dependency properties: " + string.Join(", ", missingProperties));
        }

        if (dataContextPaths.Count > 0 && element is not FrameworkElement and not FrameworkContentElement)
        {
            throw new InvalidOperationException(
                $"observe_state_unsupported: {element.GetType().Name} does not expose DataContext.");
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in dataContextPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDottedIdentifierPath(path))
            {
                throw new ArgumentException(
                    $"invalid_request: DataContext path '{path}' must contain dotted identifiers only.");
            }

            if (!seenPaths.Add(path))
            {
                continue;
            }

            definitions.Add(new ObserveStateWatchDefinition(
                new ObserveStateWatch(ObserveStateSource.DataContextPath, path)));
        }

        return definitions;
    }

    private static bool IsDottedIdentifierPath(string path)
    {
        var segments = path.Split('.');
        if (segments.Length is 0 or > 16)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length is 0 || (!char.IsLetter(segment[0]) && segment[0] != '_'))
            {
                return false;
            }

            for (var index = 1; index < segment.Length; index++)
            {
                if (!char.IsLetterOrDigit(segment[index]) && segment[index] != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void EnsureObservationDispatcherAccess(Dispatcher dispatcher)
    {
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("observe_state_dispatcher_required");
        }
    }

    private static ObserveStateVisualMetadata CaptureObservationVisualMetadata(DependencyObject element) =>
        new(
            GetBoundsWpf(element),
            IsVisibleWpf(element),
            GetVisibilityWpf(element),
            GetIsEnabledWpf(element));

    private static ObservedValueSnapshot NormalizeObservedValue(object? value, int maxLength)
    {
        if (value is RawObservationError rawError)
        {
            var error = TruncateObservationText(
                rawError.Exception.GetType().Name + ": " + rawError.Exception.Message,
                maxLength,
                out var errorTruncated);
            return CreateSnapshot(new ObserveStateValue(
                ObserveStateValueState.Error,
                ValueType: GetObservationTypeName(rawError.Exception.GetType()),
                Truncated: errorTruncated,
                Error: error), error);
        }

        if (ReferenceEquals(value, UnavailableObservationValue))
        {
            return CreateSnapshot(new ObserveStateValue(ObserveStateValueState.Unavailable));
        }

        if (ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return CreateSnapshot(new ObserveStateValue(ObserveStateValueState.Unset));
        }

        if (value is null)
        {
            return CreateSnapshot(new ObserveStateValue(ObserveStateValueState.Null));
        }

        try
        {
            var type = value.GetType();
            var typeName = GetObservationTypeName(type);
            JsonNode? scalar;
            var truncated = false;
            object? comparisonValue = value;

            switch (value)
            {
                case string text:
                    comparisonValue = TruncateObservationText(text, maxLength, out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case char character:
                    scalar = JsonValue.Create(character.ToString());
                    break;
                case bool boolean:
                    scalar = JsonValue.Create(boolean);
                    break;
                case byte number:
                    scalar = JsonValue.Create(number);
                    break;
                case sbyte number:
                    scalar = JsonValue.Create(number);
                    break;
                case short number:
                    scalar = JsonValue.Create(number);
                    break;
                case ushort number:
                    scalar = JsonValue.Create(number);
                    break;
                case int number:
                    scalar = JsonValue.Create(number);
                    break;
                case uint number:
                    scalar = JsonValue.Create(number);
                    break;
                case long number:
                    scalar = JsonValue.Create(number);
                    break;
                case ulong number:
                    scalar = JsonValue.Create(number);
                    break;
                case decimal number:
                    scalar = JsonValue.Create(number);
                    break;
                case float number when float.IsFinite(number):
                    scalar = JsonValue.Create(number);
                    break;
                case double number when double.IsFinite(number):
                    scalar = JsonValue.Create(number);
                    break;
                case float number:
                    comparisonValue = TruncateObservationText(
                        number.ToString(CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case double number:
                    comparisonValue = TruncateObservationText(
                        number.ToString(CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case DateTime dateTime:
                    comparisonValue = TruncateObservationText(
                        dateTime.ToString("O", CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case DateTimeOffset dateTimeOffset:
                    comparisonValue = TruncateObservationText(
                        dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case DateOnly dateOnly:
                    comparisonValue = TruncateObservationText(
                        dateOnly.ToString("O", CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case TimeOnly timeOnly:
                    comparisonValue = TruncateObservationText(
                        timeOnly.ToString("O", CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case TimeSpan timeSpan:
                    comparisonValue = TruncateObservationText(
                        timeSpan.ToString("c", CultureInfo.InvariantCulture),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case Guid guid:
                    comparisonValue = TruncateObservationText(
                        guid.ToString("D"),
                        maxLength,
                        out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case Uri uri:
                    comparisonValue = TruncateObservationText(uri.OriginalString, maxLength, out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                case Enum enumValue:
                    comparisonValue = TruncateObservationText(enumValue.ToString(), maxLength, out truncated);
                    scalar = JsonValue.Create((string)comparisonValue);
                    break;
                default:
                    return CreateSnapshot(new ObserveStateValue(
                        ObserveStateValueState.Unavailable,
                        ValueType: typeName));
            }

            return CreateSnapshot(new ObserveStateValue(
                ObserveStateValueState.Value,
                scalar,
                typeName,
                truncated), comparisonValue);
        }
        catch (Exception exception)
        {
            var error = TruncateObservationText(
                exception.GetType().Name + ": " + exception.Message,
                maxLength,
                out var truncated);
            return CreateSnapshot(new ObserveStateValue(
                ObserveStateValueState.Error,
                ValueType: GetObservationTypeName(value.GetType()),
                Truncated: truncated,
                Error: error), error);
        }
    }

    private static ObservedValueSnapshot CreateSnapshot(
        ObserveStateValue value,
        object? comparisonValue = null) =>
        new(value, comparisonValue);

    private static string GetObservationTypeName(Type type)
    {
        var typeName = type.FullName ?? type.Name;
        return typeName.Length <= MaxObservationPathLength
            ? typeName
            : typeName[..MaxObservationPathLength];
    }

    private static string TruncateObservationText(string value, int maxLength, out bool truncated)
    {
        truncated = value.Length > maxLength;
        if (!truncated)
        {
            return value;
        }

        return maxLength <= 3
            ? value[..maxLength]
            : value[..(maxLength - 3)] + "...";
    }

    private sealed record ObserveStateWatchDefinition(
        ObserveStateWatch Watch,
        DependencyProperty? DependencyProperty = null,
        DependencyPropertyDescriptor? Descriptor = null);

    private sealed record ObservedValueSnapshot(ObserveStateValue Value, object? ComparisonValue)
    {
        public bool IsEquivalentTo(ObservedValueSnapshot other) =>
            Value.State == other.Value.State &&
            Value.Truncated == other.Value.Truncated &&
            string.Equals(Value.ValueType, other.Value.ValueType, StringComparison.Ordinal) &&
            string.Equals(Value.Error, other.Value.Error, StringComparison.Ordinal) &&
            Equals(ComparisonValue, other.ComparisonValue);

        public bool CanSuppressDuplicate =>
            !Value.Truncated &&
            Value.State is not ObserveStateValueState.Error and not ObserveStateValueState.Unavailable;
    }

    private sealed record RawObservationError(Exception Exception);

    private sealed record ObserveStateEventSizing(
        int SerializedLength,
        ObserveStateEvent PayloadTruncatedEvent,
        int PayloadTruncatedSerializedLength);

    private sealed class QueuedObserveStateEvent
    {
        private readonly Lazy<Task<ObserveStateEventSizing>> _sizing;

        public QueuedObserveStateEvent(ObserveStateEvent observationEvent)
        {
            Event = observationEvent;
            _sizing = new Lazy<Task<ObserveStateEventSizing>>(
                () => Task.Run(() =>
                {
                    var payloadTruncatedEvent = CreatePayloadTruncatedEvent(observationEvent);
                    return new ObserveStateEventSizing(
                        JsonSerializer.Serialize(observationEvent, ObservationJsonOptions).Length,
                        payloadTruncatedEvent,
                        JsonSerializer.Serialize(payloadTruncatedEvent, ObservationJsonOptions).Length);
                }),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ObserveStateEvent Event { get; }

        public Task<ObserveStateEventSizing> GetSizingAsync() => _sizing.Value;
    }

    private static ObserveStateEvent CreatePayloadTruncatedEvent(ObserveStateEvent observationEvent) =>
        observationEvent with
        {
            OldValue = observationEvent.OldValue is null
                ? null
                : CreatePayloadTruncatedValue(observationEvent.OldValue),
            NewValue = CreatePayloadTruncatedValue(observationEvent.NewValue),
            Visual = null
        };

    private static ObserveStateValue CreatePayloadTruncatedValue(ObserveStateValue value) =>
        value.State switch
        {
            ObserveStateValueState.Value or ObserveStateValueState.Error => value with
            {
                Value = null,
                ValueType = null,
                Truncated = true,
                Error = null
            },
            _ => value with
            {
                ValueType = null,
                Truncated = value.Truncated || value.ValueType is not null
            }
        };

    private sealed class ObserveStateRegistry
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ObserveStateRecording> _recordings = new(StringComparer.Ordinal);

        public void Add(ObserveStateRecording recording)
        {
            lock (_sync)
            {
                _recordings.Add(recording.Id, recording);
            }
        }

        public ObserveStateRecording Get(string ownerId, string observationId)
        {
            if (string.IsNullOrWhiteSpace(observationId))
            {
                throw ObservationNotFound();
            }

            lock (_sync)
            {
                if (_recordings.TryGetValue(observationId.Trim(), out var recording) &&
                    string.Equals(recording.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    return recording;
                }
            }

            throw ObservationNotFound();
        }

        public ObserveStateRecording Remove(string ownerId, string observationId)
        {
            if (string.IsNullOrWhiteSpace(observationId))
            {
                throw ObservationNotFound();
            }

            lock (_sync)
            {
                var normalizedId = observationId.Trim();
                if (_recordings.TryGetValue(normalizedId, out var recording) &&
                    string.Equals(recording.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    _recordings.Remove(normalizedId);
                    return recording;
                }
            }

            throw ObservationNotFound();
        }

        public ObserveStateRecording[] RemoveOwner(string ownerId)
        {
            ObserveStateRecording[] owned;
            lock (_sync)
            {
                owned = _recordings.Values
                    .Where(recording => string.Equals(recording.OwnerId, ownerId, StringComparison.Ordinal))
                    .ToArray();
                foreach (var recording in owned)
                {
                    _recordings.Remove(recording.Id);
                }
            }

            return owned;
        }

        private static InvalidOperationException ObservationNotFound() =>
            new("observe_state_not_found");
    }

    private sealed class ObserveStateRecording
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _pollGate = new(1, 1);
        private readonly Dispatcher _dispatcher;
        private readonly LinkedList<QueuedObserveStateEvent> _pendingEvents = new();
        private readonly List<Action> _detachActions = new();
        private readonly long _startedTimestamp;
        private readonly int _durationMs;
        private readonly int _maxEvents;
        private readonly int _maxValueLength;
        private readonly bool _includeVisualMetadata;
        private DependencyObject? _element;
        private DispatcherTimer? _durationTimer;
        private long _nextSequence;
        private bool _completed;
        private ObserveStateStopReason? _stopReason;
        private DateTimeOffset? _stoppedAtUtc;
        private long? _stoppedElapsedMs;
        private int _droppedSinceLastPoll;
        private long _droppedTotal;
        private int _coalescedSinceLastPoll;
        private long _coalescedTotal;
        private int _truncatedSinceLastPoll;
        private long _truncatedTotal;

        public ObserveStateRecording(
            string ownerId,
            DependencyObject element,
            ElementRef elementRef,
            int durationMs,
            int maxEvents,
            int maxValueLength,
            bool includeVisualMetadata)
        {
            OwnerId = ownerId;
            Id = Guid.NewGuid().ToString("N");
            ElementRef = elementRef;
            _element = element;
            _dispatcher = element.Dispatcher;
            _durationMs = durationMs;
            _maxEvents = maxEvents;
            _maxValueLength = maxValueLength;
            _includeVisualMetadata = includeVisualMetadata;
            StartedAtUtc = DateTimeOffset.UtcNow;
            ExpiresAtUtc = StartedAtUtc.AddMilliseconds(durationMs);
            _startedTimestamp = Stopwatch.GetTimestamp();
        }

        public string Id { get; }

        public string OwnerId { get; }

        public ElementRef ElementRef { get; }

        public Dispatcher Dispatcher => _dispatcher;

        public DateTimeOffset StartedAtUtc { get; }

        public DateTimeOffset ExpiresAtUtc { get; }

        public IReadOnlyList<ObserveStateEvent> Attach(IReadOnlyList<ObserveStateWatchDefinition> definitions)
        {
            VerifyDispatcherAccess();
            var initialEvents = new List<ObserveStateEvent>(definitions.Count);
            foreach (var definition in definitions)
            {
                initialEvents.Add(definition.Watch.Source switch
                {
                    ObserveStateSource.DependencyProperty => AttachDependencyProperty(definition),
                    ObserveStateSource.DataContextPath => AttachDataContextPath(definition),
                    _ => throw new ArgumentOutOfRangeException(nameof(definition))
                });
            }

            return initialEvents;
        }

        public void Activate()
        {
            VerifyDispatcherAccess();
            var element = _element ?? throw new InvalidOperationException("observe_state_not_active");

            RoutedEventHandler unloaded = (_, _) => Complete(ObserveStateStopReason.ElementUnloaded);
            switch (element)
            {
                case FrameworkElement frameworkElement:
                    frameworkElement.Unloaded += unloaded;
                    _detachActions.Add(() => frameworkElement.Unloaded -= unloaded);
                    break;
                case FrameworkContentElement frameworkContentElement:
                    frameworkContentElement.Unloaded += unloaded;
                    _detachActions.Add(() => frameworkContentElement.Unloaded -= unloaded);
                    break;
            }

            var remainingMs = _durationMs - ElapsedMilliseconds(Stopwatch.GetTimestamp());
            if (remainingMs <= 0)
            {
                Complete(ObserveStateStopReason.DurationElapsed);
                return;
            }

            _durationTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(remainingMs)
            };
            _durationTimer.Tick += OnDurationElapsed;
            _durationTimer.Start();
        }

        public ObserveStatePollResponse Poll(int requestedMaxBatch, int requestedMaxPayloadChars)
        {
            var maxBatch = Math.Clamp(requestedMaxBatch, 1, MaxObservationBatch);
            if (requestedMaxPayloadChars <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedMaxPayloadChars),
                    "maxPayloadChars must be greater than zero.");
            }

            var payloadBudget = Math.Min(requestedMaxPayloadChars, MaxObservationPayloadChars);
            _pollGate.Wait();
            try
            {
                var sizingRetryCount = 0;
                while (true)
                {
                    QueuedObserveStateEvent[] snapshot;
                    lock (_sync)
                    {
                        snapshot = _pendingEvents.Take(maxBatch).ToArray();
                        if (snapshot.Length is 0)
                        {
                            return DrainSizedPrefix(
                                snapshot,
                                Array.Empty<ObserveStateEventSizing>(),
                                stableCount: 0,
                                payloadBudget,
                                requestedMaxPayloadChars);
                        }
                    }

                    var sizings = new ObserveStateEventSizing[snapshot.Length];
                    var completedSizingCount = 0;
                    var retryCurrentHead = false;
                    for (var index = 0; index < snapshot.Length; index++)
                    {
                        sizings[index] = snapshot[index].GetSizingAsync().GetAwaiter().GetResult();
                        completedSizingCount = index + 1;
                        lock (_sync)
                        {
                            if (_pendingEvents.First is not { } head ||
                                !ReferenceEquals(head.Value, snapshot[0]))
                            {
                                retryCurrentHead = true;
                                break;
                            }
                        }
                    }

                    if (retryCurrentHead)
                    {
                        sizingRetryCount++;
                        if (sizingRetryCount >= MaxObservationPollSizingRetries)
                        {
                            lock (_sync)
                            {
                                return CreateContendedPollResponse(
                                    payloadBudget,
                                    requestedMaxPayloadChars);
                            }
                        }

                        continue;
                    }

                    lock (_sync)
                    {
                        var stableCount = Math.Min(
                            CountStablePrefix(snapshot),
                            completedSizingCount);
                        if (stableCount is 0)
                        {
                            sizingRetryCount++;
                            if (sizingRetryCount >= MaxObservationPollSizingRetries)
                            {
                                return CreateContendedPollResponse(
                                    payloadBudget,
                                    requestedMaxPayloadChars);
                            }

                            continue;
                        }

                        return DrainSizedPrefix(
                            snapshot,
                            sizings,
                            stableCount,
                            payloadBudget,
                            requestedMaxPayloadChars);
                    }
                }
            }
            finally
            {
                _pollGate.Release();
            }
        }

        private ObserveStatePollResponse CreateContendedPollResponse(
            int payloadBudget,
            int requestedMaxPayloadChars)
        {
            var response = BuildPollResponse(
                Array.Empty<ObserveStateEvent>(),
                _pendingEvents.First?.Value.Event.Sequence,
                hasMore: true,
                CurrentDurationMs());
            if (JsonSerializer.Serialize(response, ObservationJsonOptions).Length > payloadBudget)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedMaxPayloadChars),
                    "maxPayloadChars is too small for the observation poll metadata.");
            }

            _droppedSinceLastPoll = 0;
            _coalescedSinceLastPoll = 0;
            _truncatedSinceLastPoll = 0;
            return response;
        }

        private int CountStablePrefix(IReadOnlyList<QueuedObserveStateEvent> snapshot)
        {
            var stableCount = 0;
            var current = _pendingEvents.First;
            while (stableCount < snapshot.Count &&
                   current is not null &&
                   ReferenceEquals(current.Value, snapshot[stableCount]))
            {
                stableCount++;
                current = current.Next;
            }

            return stableCount;
        }

        private ObserveStatePollResponse DrainSizedPrefix(
            IReadOnlyList<QueuedObserveStateEvent> snapshot,
            IReadOnlyList<ObserveStateEventSizing> sizings,
            int stableCount,
            int payloadBudget,
            int requestedMaxPayloadChars)
        {
            var events = new List<ObserveStateEvent>(stableCount);
            var oldestAvailableSequence = _pendingEvents.First?.Value.Event.Sequence;
            var durationMs = CurrentDurationMs();
            var emptyEvents = Array.Empty<ObserveStateEvent>();
            var emptyResponseWithMore = BuildPollResponse(
                emptyEvents,
                oldestAvailableSequence,
                hasMore: true,
                durationMs);
            var emptyResponseWithoutMore = BuildPollResponse(
                emptyEvents,
                oldestAvailableSequence,
                hasMore: false,
                durationMs);
            var baseLengthWithMore = JsonSerializer.Serialize(
                emptyResponseWithMore,
                ObservationJsonOptions).Length;
            var baseLengthWithoutMore = JsonSerializer.Serialize(
                emptyResponseWithoutMore,
                ObservationJsonOptions).Length;
            var responseLength = _pendingEvents.Count > 0
                ? baseLengthWithMore
                : baseLengthWithoutMore;
            if (responseLength > payloadBudget)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedMaxPayloadChars),
                    "maxPayloadChars is too small for the observation poll metadata.");
            }

            var eventPayloadLength = 0;
            var consumedEventCount = 0;
            for (var index = 0; index < stableCount; index++)
            {
                var sizing = sizings[index];
                var candidateEventPayloadLength = eventPayloadLength
                    + sizing.SerializedLength
                    + (events.Count > 0 ? 1 : 0);
                var candidateHasMore = _pendingEvents.Count > events.Count + 1;
                var candidateResponseLength = (candidateHasMore
                        ? baseLengthWithMore
                        : baseLengthWithoutMore)
                    + candidateEventPayloadLength;
                if (candidateResponseLength > payloadBudget)
                {
                    if (events.Count > 0)
                    {
                        break;
                    }

                    _truncatedSinceLastPoll++;
                    _truncatedTotal++;
                    var compactMetadata = BuildPollResponse(
                        emptyEvents,
                        oldestAvailableSequence,
                        _pendingEvents.Count > 1,
                        durationMs);
                    var compactResponseLength = JsonSerializer.Serialize(
                            compactMetadata,
                            ObservationJsonOptions).Length
                        + sizing.PayloadTruncatedSerializedLength;
                    if (compactResponseLength <= payloadBudget)
                    {
                        events.Add(sizing.PayloadTruncatedEvent);
                        consumedEventCount = 1;
                        break;
                    }

                    _truncatedSinceLastPoll--;
                    _truncatedTotal--;
                    _droppedSinceLastPoll++;
                    _droppedTotal++;
                    var droppedResponse = BuildPollResponse(
                        emptyEvents,
                        oldestAvailableSequence,
                        _pendingEvents.Count > 1,
                        durationMs);
                    if (JsonSerializer.Serialize(droppedResponse, ObservationJsonOptions).Length > payloadBudget)
                    {
                        _droppedSinceLastPoll--;
                        _droppedTotal--;
                        throw new ArgumentOutOfRangeException(
                            nameof(requestedMaxPayloadChars),
                            "maxPayloadChars is too small for a bounded observation poll response.");
                    }

                    consumedEventCount = 1;
                    break;
                }

                events.Add(snapshot[index].Event);
                eventPayloadLength = candidateEventPayloadLength;
                consumedEventCount++;
            }

            var response = BuildPollResponse(
                events,
                oldestAvailableSequence,
                _pendingEvents.Count > consumedEventCount,
                durationMs);

            for (var index = 0; index < consumedEventCount; index++)
            {
                _pendingEvents.RemoveFirst();
            }

            _droppedSinceLastPoll = 0;
            _coalescedSinceLastPoll = 0;
            _truncatedSinceLastPoll = 0;
            return response;
        }

        private ObserveStatePollResponse BuildPollResponse(
            IReadOnlyList<ObserveStateEvent> events,
            long? oldestAvailableSequence,
            bool hasMore,
            long durationMs) =>
            new(
                events,
                _droppedSinceLastPoll,
                _droppedTotal,
                _coalescedSinceLastPoll,
                _coalescedTotal,
                _truncatedSinceLastPoll,
                _truncatedTotal,
                hasMore,
                _completed,
                _stopReason,
                StartedAtUtc,
                _stoppedAtUtc,
                durationMs,
                oldestAvailableSequence);

        public bool Complete(ObserveStateStopReason reason)
        {
            VerifyDispatcherAccess();
            lock (_sync)
            {
                if (_completed)
                {
                    return false;
                }

                _completed = true;
                _stopReason = reason;
                _stoppedAtUtc = DateTimeOffset.UtcNow;
                _stoppedElapsedMs = ElapsedMilliseconds(Stopwatch.GetTimestamp());
            }

            Detach();
            return true;
        }

        public ObserveStateStopResponse CreateStopResponse(bool stopped)
        {
            lock (_sync)
            {
                return new ObserveStateStopResponse(
                    stopped,
                    _stopReason ?? ObserveStateStopReason.ClientRequested,
                    _stoppedAtUtc ?? DateTimeOffset.UtcNow,
                    _pendingEvents.Count);
            }
        }

        public void Release()
        {
            VerifyDispatcherAccess();
            Complete(ObserveStateStopReason.ClientRequested);
        }

        private ObserveStateEvent AttachDependencyProperty(ObserveStateWatchDefinition definition)
        {
            var element = _element ?? throw new InvalidOperationException("observe_state_not_active");
            var property = definition.DependencyProperty
                ?? throw new InvalidOperationException("Missing dependency property definition.");
            var descriptor = definition.Descriptor
                ?? throw new InvalidOperationException("Missing dependency property descriptor.");
            var watch = CreateWatch(definition.Watch, ReadDependencyProperty(element, property));
            EventHandler handler = (_, _) =>
                ObserveChange(watch, ReadDependencyProperty(element, property));
            descriptor.AddValueChanged(element, handler);
            _detachActions.Add(() => descriptor.RemoveValueChanged(element, handler));
            return CreateInitialEvent(watch);
        }

        private ObserveStateEvent AttachDataContextPath(ObserveStateWatchDefinition definition)
        {
            var element = _element ?? throw new InvalidOperationException("observe_state_not_active");
            var sink = new ObserveStateBindingSink();
            var binding = new Binding
            {
                Source = element,
                Path = new PropertyPath("DataContext." + definition.Watch.Path),
                Mode = BindingMode.OneWay,
                FallbackValue = UnavailableObservationValue
            };
            BindingOperations.SetBinding(sink, ObserveStateBindingSink.ValueProperty, binding);
            var watch = CreateWatch(definition.Watch, sink.GetValue(ObserveStateBindingSink.ValueProperty));
            sink.SetCallback(value => ObserveChange(watch, value));
            _detachActions.Add(() =>
            {
                sink.SetCallback(null);
                BindingOperations.ClearBinding(sink, ObserveStateBindingSink.ValueProperty);
            });
            return CreateInitialEvent(watch);
        }

        private ObserveStateWatchState CreateWatch(ObserveStateWatch watch, object? rawValue)
        {
            var snapshot = NormalizeObservedValue(rawValue, _maxValueLength);
            CountTruncation(snapshot);
            return new ObserveStateWatchState(watch, snapshot, Stopwatch.GetTimestamp());
        }

        private ObserveStateEvent CreateInitialEvent(ObserveStateWatchState watch)
        {
            lock (_sync)
            {
                return new ObserveStateEvent(
                    ++_nextSequence,
                    DateTimeOffset.UtcNow,
                    ElapsedMilliseconds(watch.LastChangedTimestamp),
                    watch.Watch.Source,
                    watch.Watch.Path,
                    ObserveStateEventKind.Initial,
                    OldValue: null,
                    watch.LastValue.Value,
                    Visual: GetVisualMetadata());
            }
        }

        private void ObserveChange(ObserveStateWatchState watch, object? rawValue)
        {
            VerifyDispatcherAccess();
            if (ElapsedMilliseconds(Stopwatch.GetTimestamp()) >= _durationMs)
            {
                Complete(ObserveStateStopReason.DurationElapsed);
                return;
            }

            var snapshot = NormalizeObservedValue(rawValue, _maxValueLength);
            lock (_sync)
            {
                if (_completed ||
                    (snapshot.CanSuppressDuplicate &&
                     watch.LastValue.CanSuppressDuplicate &&
                     snapshot.IsEquivalentTo(watch.LastValue)))
                {
                    return;
                }

                var observedTimestamp = Stopwatch.GetTimestamp();
                var observedAtUtc = DateTimeOffset.UtcNow;
                var oldValue = watch.LastValue.Value;
                var previousDurationMs = Math.Max(
                    0,
                    ElapsedMilliseconds(watch.LastChangedTimestamp, observedTimestamp));
                watch.LastValue = snapshot;
                watch.LastChangedTimestamp = observedTimestamp;
                CountTruncation(snapshot);

                Enqueue(new ObserveStateEvent(
                    ++_nextSequence,
                    observedAtUtc,
                    ElapsedMilliseconds(observedTimestamp),
                    watch.Watch.Source,
                    watch.Watch.Path,
                    ObserveStateEventKind.Change,
                    oldValue,
                    snapshot.Value,
                    previousDurationMs,
                    Visual: GetVisualMetadata()));
            }
        }

        private void Enqueue(ObserveStateEvent observationEvent)
        {
            if (_pendingEvents.Count < _maxEvents)
            {
                _pendingEvents.AddLast(CreateQueuedEvent(observationEvent));
                return;
            }

            var current = _pendingEvents.Last;
            while (current is not null)
            {
                var previous = current.Previous;
                var candidate = current.Value.Event;
                if (candidate.Kind is ObserveStateEventKind.Change &&
                    candidate.Source == observationEvent.Source &&
                    string.Equals(candidate.Path, observationEvent.Path, StringComparison.Ordinal))
                {
                    var merged = observationEvent with
                    {
                        OldValue = candidate.OldValue,
                        PreviousValueDurationMs = candidate.PreviousValueDurationMs,
                        CoalescedChangeCount = candidate.CoalescedChangeCount + 1
                    };
                    _pendingEvents.Remove(current);
                    _pendingEvents.AddLast(CreateQueuedEvent(merged));
                    _coalescedSinceLastPoll++;
                    _coalescedTotal++;
                    return;
                }

                current = previous;
            }

            _pendingEvents.RemoveFirst();
            _droppedSinceLastPoll++;
            _droppedTotal++;
            _pendingEvents.AddLast(CreateQueuedEvent(observationEvent));
        }

        private static QueuedObserveStateEvent CreateQueuedEvent(ObserveStateEvent observationEvent)
            => new(observationEvent);

        private void CountTruncation(ObservedValueSnapshot snapshot)
        {
            if (!snapshot.Value.Truncated)
            {
                return;
            }

            lock (_sync)
            {
                _truncatedSinceLastPoll++;
                _truncatedTotal++;
            }
        }

        private ObserveStateVisualMetadata? GetVisualMetadata()
        {
            if (!_includeVisualMetadata || _element is not { } element)
            {
                return null;
            }

            return CaptureObservationVisualMetadata(element);
        }

        private static object? ReadDependencyProperty(DependencyObject element, DependencyProperty property)
        {
            try
            {
                return element.GetValue(property);
            }
            catch (Exception exception)
            {
                return new RawObservationError(exception);
            }
        }

        private void OnDurationElapsed(object? sender, EventArgs eventArgs) =>
            Complete(ObserveStateStopReason.DurationElapsed);

        private void Detach()
        {
            if (_durationTimer is not null)
            {
                _durationTimer.Stop();
                _durationTimer.Tick -= OnDurationElapsed;
                _durationTimer = null;
            }

            for (var index = _detachActions.Count - 1; index >= 0; index--)
            {
                try
                {
                    _detachActions[index]();
                }
                catch
                {
                }
            }

            _detachActions.Clear();
            _element = null;
        }

        private long CurrentDurationMs() =>
            _stoppedElapsedMs ?? ElapsedMilliseconds(Stopwatch.GetTimestamp());

        private long ElapsedMilliseconds(long timestamp) =>
            ElapsedMilliseconds(_startedTimestamp, timestamp);

        private static long ElapsedMilliseconds(long startTimestamp, long endTimestamp) =>
            (long)Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds;

        private void VerifyDispatcherAccess()
        {
            if (!_dispatcher.CheckAccess())
            {
                throw new InvalidOperationException("observe_state_dispatcher_required");
            }
        }
    }

    private sealed class ObserveStateWatchState(
        ObserveStateWatch watch,
        ObservedValueSnapshot lastValue,
        long lastChangedTimestamp)
    {
        public ObserveStateWatch Watch { get; } = watch;

        public ObservedValueSnapshot LastValue { get; set; } = lastValue;

        public long LastChangedTimestamp { get; set; } = lastChangedTimestamp;
    }

    private sealed class ObserveStateBindingSink : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value),
            typeof(object),
            typeof(ObserveStateBindingSink),
            new PropertyMetadata(UnavailableObservationValue, OnValueChanged));

        private Action<object?>? _callback;

        public object? Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public void SetCallback(Action<object?>? callback) => _callback = callback;

        private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs) =>
            ((ObserveStateBindingSink)dependencyObject)._callback?.Invoke(eventArgs.NewValue);
    }
}
