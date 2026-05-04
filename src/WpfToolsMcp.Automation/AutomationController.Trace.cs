using System.Diagnostics;
using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private TraceSession? _traceSession;

    public ToolTraceSpan? BeginToolTrace(string tool)
    {
        var trace = _traceSession;
        if (trace is null)
        {
            return null;
        }

        tool = string.IsNullOrWhiteSpace(tool) ? "tool" : tool.Trim();
        return new ToolTraceSpan(trace, tool);
    }

    public Task<TraceStartResponse> TraceStartAsync(
        bool resetIfRunning,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_traceSession is not null && !resetIfRunning)
        {
            return Task.FromResult(new TraceStartResponse(
                TraceId: _traceSession.TraceId,
                StartedAtUtc: _traceSession.StartedAtUtc,
                Started: false,
                Message: "Trace already running. Use resetIfRunning=true to restart."));
        }

        var traceId = Guid.NewGuid().ToString("N");
        var startedAt = DateTime.UtcNow;
        _traceSession = new TraceSession(traceId, startedAt);

        return Task.FromResult(new TraceStartResponse(
            TraceId: traceId,
            StartedAtUtc: startedAt,
            Started: true));
    }

    public async Task<TraceStopResponse> TraceStopAsync(
        string traceId,
        string? outputPath,
        bool includeEvents,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);

        var session = _traceSession;
        if (session is null)
        {
            throw new InvalidOperationException("No active trace. Call trace_start first.");
        }

        traceId = traceId.Trim();
        if (!string.Equals(session.TraceId, traceId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"traceId '{traceId}' does not match the active trace '{session.TraceId}'.");
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

        TraceEvent[] events;
        lock (session.Sync)
        {
            events = session.Events.ToArray();
        }

        var payload = new TracePayload(
            TraceId: traceId,
            StartedAtUtc: session.StartedAtUtc,
            StoppedAtUtc: stoppedAt,
            Events: events);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);

        _traceSession = null;

        return new TraceStopResponse(
            TraceId: traceId,
            StoppedAtUtc: stoppedAt,
            OutputPath: path,
            EventCount: payload.Events.Count,
            Events: includeEvents ? payload.Events : null);
    }

    private ToolTraceSpan? BeginTraceSpan(string tool)
    {
        return BeginToolTrace(tool);
    }

    public sealed class ToolTraceSpan : IDisposable
    {
        private readonly TraceSession _trace;
        private readonly string _tool;
        private readonly DateTime _startedAtUtc;
        private readonly long _startTimestamp;
        private string? _summary;
        private string? _error;

        internal ToolTraceSpan(TraceSession trace, string tool)
        {
            _trace = trace;
            _tool = tool;
            _startedAtUtc = DateTime.UtcNow;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public void SetSummary(string? summary)
        {
            if (!string.IsNullOrWhiteSpace(summary))
            {
                _summary = summary.Trim();
            }
        }

        public void SetError(Exception ex)
        {
            var message = ex.GetBaseException().Message;
            if (!string.IsNullOrWhiteSpace(message))
            {
                _error = message.Trim();
            }
        }

        public void Dispose()
        {
            var durationMs = (int)Math.Round(
                Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds,
                MidpointRounding.AwayFromZero);

            lock (_trace.Sync)
            {
                _trace.Events.Add(new TraceEvent(
                    Tool: _tool,
                    StartedAtUtc: _startedAtUtc,
                    DurationMs: durationMs,
                    Summary: _summary,
                    Error: _error));
            }
        }
    }

    internal sealed record TraceSession(string TraceId, DateTime StartedAtUtc)
    {
        public object Sync { get; } = new();

        public List<TraceEvent> Events { get; } = [];
    }

    private sealed record TracePayload(
        string TraceId,
        DateTime StartedAtUtc,
        DateTime StoppedAtUtc,
        IReadOnlyList<TraceEvent> Events);
}
