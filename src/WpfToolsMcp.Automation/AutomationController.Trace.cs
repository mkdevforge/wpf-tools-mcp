using System.Diagnostics;
using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private const int MaxTraceToolChars = 128;
    private const int MaxTraceTextChars = 1_000;

    private readonly object _traceLifecycleSync = new();
    private TraceSession? _traceSession;

    public ToolTraceSpan? BeginToolTrace(string tool)
    {
        TraceSession? trace;
        lock (_traceLifecycleSync)
        {
            trace = _traceSession;
        }

        if (trace is null)
        {
            return null;
        }

        tool = string.IsNullOrWhiteSpace(tool) ? "tool" : tool.Trim();
        return new ToolTraceSpan(trace, BoundTraceText(tool, MaxTraceToolChars));
    }

    public Task<TraceStartResponse> TraceStartAsync(
        bool resetIfRunning,
        CancellationToken cancellationToken = default) =>
        TraceStartAsync("standalone", resetIfRunning, cancellationToken);

    public Task<TraceStartResponse> TraceStartAsync(
        string sessionId,
        bool resetIfRunning,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        sessionId = sessionId.Trim();

        lock (_traceLifecycleSync)
        {
            if (_traceSession is not null && !resetIfRunning)
            {
                return Task.FromResult(new TraceStartResponse(
                    TraceId: _traceSession.TraceId,
                    StartedAtUtc: _traceSession.StartedAtUtc,
                    Started: false,
                    Message: "Trace already running. Use resetIfRunning=true to restart."));
            }

            _traceSession?.CloseAndSnapshot();

            var traceId = Guid.NewGuid().ToString("N");
            var startedAt = DateTime.UtcNow;
            _traceSession = new TraceSession(sessionId, traceId, startedAt);

            return Task.FromResult(new TraceStartResponse(
                TraceId: traceId,
                StartedAtUtc: startedAt,
                Started: true));
        }
    }

    public async Task<TraceStopResponse> TraceStopAsync(
        string traceId,
        string? outputPath,
        bool includeEvents = false,
        int maxEvents = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        traceId = traceId.Trim();

        TraceSnapshot snapshot;
        lock (_traceLifecycleSync)
        {
            var session = _traceSession;
            if (session is null)
            {
                throw new InvalidOperationException("No active trace. Call trace_start first.");
            }

            if (!string.Equals(session.TraceId, traceId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"traceId '{traceId}' does not match the active trace '{session.TraceId}'.");
            }

            _traceSession = null;
            snapshot = session.CloseAndSnapshot();
        }

        var stoppedAt = DateTime.UtcNow;
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-trace-{traceId}.json")
            : outputPath.Trim();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new TracePayload(
            Version: RuntimeEventVersions.V1,
            TraceId: traceId,
            SessionId: snapshot.SessionId,
            StreamId: traceId,
            StartedAtUtc: snapshot.StartedAtUtc,
            StoppedAtUtc: stoppedAt,
            ObservedEventCount: snapshot.ObservedEventCount,
            RetainedEventCount: snapshot.Events.Count,
            DroppedEventCount: snapshot.DroppedEventCount,
            RetentionLimit: TraceSession.MaxRetainedEvents,
            RetentionTruncated: snapshot.DroppedEventCount > 0,
            Events: snapshot.Events);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TraceEvent>? responseEvents = null;
        if (includeEvents)
        {
            var boundedMaxEvents = Math.Clamp(maxEvents, 1, TraceSession.MaxRetainedEvents);
            responseEvents = payload.Events.Take(boundedMaxEvents).ToArray();
        }

        var returnedEventCount = responseEvents?.Count ?? 0;
        var truncated = includeEvents && returnedEventCount < payload.Events.Count;

        return new TraceStopResponse(
            TraceId: traceId,
            StoppedAtUtc: stoppedAt,
            OutputPath: path,
            EventCount: payload.RetainedEventCount,
            ReturnedEventCount: returnedEventCount,
            Truncated: truncated,
            TruncatedReason: truncated ? "maxEvents" : null,
            Events: responseEvents)
        {
            ObservedEventCount = payload.ObservedEventCount,
            RetainedEventCount = payload.RetainedEventCount,
            DroppedEventCount = payload.DroppedEventCount,
            RetentionLimit = payload.RetentionLimit,
            RetentionTruncated = payload.RetentionTruncated
        };
    }

    private void CloseActiveTrace()
    {
        lock (_traceLifecycleSync)
        {
            var trace = _traceSession;
            _traceSession = null;
            trace?.CloseAndSnapshot();
        }
    }

    private ToolTraceSpan? BeginTraceSpan(string tool) => BeginToolTrace(tool);

    private static string BoundTraceText(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        var length = maxChars;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    public sealed class ToolTraceSpan : IDisposable
    {
        private const int MaxTraversedExceptions = 16;

        private readonly TraceSession _trace;
        private readonly string _tool;
        private readonly DateTimeOffset _startedAtUtc;
        private readonly long _startTimestamp;
        private string? _summary;
        private string? _error;
        private int _disposed;

        internal ToolTraceSpan(TraceSession trace, string tool)
        {
            _trace = trace;
            _tool = tool;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public void SetSummary(string? summary)
        {
            if (!string.IsNullOrWhiteSpace(summary))
            {
                _summary = BoundTraceText(summary.Trim(), MaxTraceTextChars);
            }
        }

        public void SetError(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);

            var actionable = FindException<ActionableFailureException>(ex);
            _error = actionable is null
                ? "tool_failed: The tool operation failed."
                : BoundTraceText(
                    $"{actionable.Failure.Code}: {actionable.Failure.Detail}",
                    MaxTraceTextChars);
        }

        private static TException? FindException<TException>(Exception root)
            where TException : Exception
        {
            var pending = new Stack<Exception>();
            var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            pending.Push(root);

            while (pending.Count > 0 && visited.Count < MaxTraversedExceptions)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current is TException match)
                {
                    return match;
                }

                if (current is AggregateException aggregate)
                {
                    var enqueueCount = Math.Min(
                        aggregate.InnerExceptions.Count,
                        MaxTraversedExceptions - visited.Count - pending.Count);
                    for (var index = enqueueCount - 1; index >= 0; index--)
                    {
                        pending.Push(aggregate.InnerExceptions[index]);
                    }
                }
                else if (current.InnerException is not null &&
                         visited.Count + pending.Count < MaxTraversedExceptions)
                {
                    pending.Push(current.InnerException);
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            var durationMs = elapsedMs >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Round(elapsedMs, MidpointRounding.AwayFromZero);

            _trace.TryRecord(
                _tool,
                _startedAtUtc,
                durationMs,
                _summary,
                _error);
        }
    }

    internal sealed class TraceSession
    {
        internal const int MaxRetainedEvents = 1_000;

        private readonly object _sync = new();
        private readonly Queue<TraceEvent> _events = new();
        private long _observedEventCount;
        private long _droppedEventCount;
        private bool _closed;

        internal TraceSession(string sessionId, string traceId, DateTime startedAtUtc)
        {
            SessionId = sessionId;
            TraceId = traceId;
            StartedAtUtc = startedAtUtc;
        }

        internal string SessionId { get; }
        internal string TraceId { get; }
        internal DateTime StartedAtUtc { get; }

        internal void TryRecord(
            string tool,
            DateTimeOffset startedAtUtc,
            int durationMs,
            string? summary,
            string? error)
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return;
                }

                var sequence = ++_observedEventCount;
                var traceEvent = new TraceEvent(
                    Tool: tool,
                    StartedAtUtc: startedAtUtc.UtcDateTime,
                    DurationMs: durationMs,
                    Summary: summary,
                    Error: error)
                {
                    Envelope = new RuntimeEventEnvelope(
                        Version: RuntimeEventVersions.V1,
                        ObservedAtUtc: startedAtUtc.ToUniversalTime(),
                        SourceKind: RuntimeEventSourceKinds.ToolTrace,
                        SessionId: SessionId,
                        StreamId: TraceId,
                        Sequence: sequence)
                };

                if (_events.Count >= MaxRetainedEvents)
                {
                    _events.Dequeue();
                    _droppedEventCount++;
                }

                _events.Enqueue(traceEvent);
            }
        }

        internal TraceSnapshot CloseAndSnapshot()
        {
            lock (_sync)
            {
                _closed = true;
                return new TraceSnapshot(
                    SessionId,
                    TraceId,
                    StartedAtUtc,
                    _observedEventCount,
                    _droppedEventCount,
                    _events.ToArray());
            }
        }
    }

    internal sealed record TraceSnapshot(
        string SessionId,
        string TraceId,
        DateTime StartedAtUtc,
        long ObservedEventCount,
        long DroppedEventCount,
        IReadOnlyList<TraceEvent> Events);

    private sealed record TracePayload(
        int Version,
        string TraceId,
        string SessionId,
        string StreamId,
        DateTime StartedAtUtc,
        DateTime StoppedAtUtc,
        long ObservedEventCount,
        int RetainedEventCount,
        long DroppedEventCount,
        int RetentionLimit,
        bool RetentionTruncated,
        IReadOnlyList<TraceEvent> Events);
}
