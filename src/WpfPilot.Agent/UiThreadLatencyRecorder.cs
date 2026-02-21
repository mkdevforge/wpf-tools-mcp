using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using WpfPilot.Contracts;

namespace WpfPilot.Agent;

internal sealed class UiThreadLatencyRecorder
{
    private sealed class RunState
    {
        private const int MaxSamples = 10_000;

        private readonly object _sync = new();
        private readonly Queue<int> _latenciesMs = new();
        private readonly Dispatcher _dispatcher;

        private Timer? _probeTimer;
        private CancellationTokenSource? _autoStopCts;
        private int _pendingProbe;
        private int _droppedProbeCount;

        public RunState(string runId, DateTime startedAtUtc, Dispatcher dispatcher, int probeIntervalMs, int autoStopAfterMs)
        {
            RunId = runId;
            StartedAtUtc = startedAtUtc;
            _dispatcher = dispatcher;
            ProbeIntervalMs = probeIntervalMs;
            AutoStopAfterMs = autoStopAfterMs;
        }

        public string RunId { get; }
        public DateTime StartedAtUtc { get; }
        public int ProbeIntervalMs { get; }
        public int AutoStopAfterMs { get; }
        public PerformanceSummary? Summary { get; private set; }

        public void Start()
        {
            _probeTimer = new Timer(ProbeTick, state: null, dueTime: 0, period: ProbeIntervalMs);

            _autoStopCts = new CancellationTokenSource();
            var token = _autoStopCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AutoStopAfterMs, token);
                    if (!token.IsCancellationRequested)
                    {
                        _ = TryStop(out _);
                    }
                }
                catch
                {
                }
            }, CancellationToken.None);
        }

        public bool TryStop(out PerformanceSummary? summary)
        {
            lock (_sync)
            {
                if (Summary is not null)
                {
                    summary = Summary;
                    return true;
                }

                try
                {
                    _autoStopCts?.Cancel();
                }
                catch
                {
                }
                finally
                {
                    _autoStopCts?.Dispose();
                    _autoStopCts = null;
                }

                try
                {
                    _probeTimer?.Dispose();
                }
                catch
                {
                }
                finally
                {
                    _probeTimer = null;
                }

                var stoppedAtUtc = DateTime.UtcNow;
                var samples = _latenciesMs.ToArray();
                var dropped = _droppedProbeCount;

                summary = BuildSummary(samples, dropped, stoppedAtUtc);
                Summary = summary;
                return true;
            }
        }

        private void ProbeTick(object? _)
        {
            if (Summary is not null)
            {
                return;
            }

            if (Interlocked.Exchange(ref _pendingProbe, 1) == 1)
            {
                _ = Interlocked.Increment(ref _droppedProbeCount);
                return;
            }

            var scheduledTicks = Stopwatch.GetTimestamp();
            try
            {
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => ProbeCallback(scheduledTicks)));
            }
            catch
            {
                _ = Interlocked.Exchange(ref _pendingProbe, 0);
            }
        }

        private void ProbeCallback(long scheduledTicks)
        {
            try
            {
                if (Summary is not null)
                {
                    return;
                }

                var nowTicks = Stopwatch.GetTimestamp();
                var latencyMs = (int)Math.Round((nowTicks - scheduledTicks) * 1000.0 / Stopwatch.Frequency);
                if (latencyMs < 0)
                {
                    latencyMs = 0;
                }

                lock (_sync)
                {
                    if (Summary is not null)
                    {
                        return;
                    }

                    if (_latenciesMs.Count >= MaxSamples)
                    {
                        _ = _latenciesMs.Dequeue();
                    }

                    _latenciesMs.Enqueue(latencyMs);
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref _pendingProbe, 0);
            }
        }

        private PerformanceSummary BuildSummary(int[] samples, int dropped, DateTime stoppedAtUtc)
        {
            if (samples.Length == 0)
            {
                return new PerformanceSummary(
                    RunId: RunId,
                    StartedAtUtc: StartedAtUtc,
                    StoppedAtUtc: stoppedAtUtc,
                    ProbeIntervalMs: ProbeIntervalMs,
                    SampleCount: 0,
                    DroppedProbeCount: dropped,
                    MinLatencyMs: 0,
                    P50LatencyMs: 0,
                    P95LatencyMs: 0,
                    P99LatencyMs: 0,
                    MaxLatencyMs: 0);
            }

            Array.Sort(samples);

            return new PerformanceSummary(
                RunId: RunId,
                StartedAtUtc: StartedAtUtc,
                StoppedAtUtc: stoppedAtUtc,
                ProbeIntervalMs: ProbeIntervalMs,
                SampleCount: samples.Length,
                DroppedProbeCount: dropped,
                MinLatencyMs: samples[0],
                P50LatencyMs: PercentileNearestRank(samples, 0.50),
                P95LatencyMs: PercentileNearestRank(samples, 0.95),
                P99LatencyMs: PercentileNearestRank(samples, 0.99),
                MaxLatencyMs: samples[^1]);
        }

        private static int PercentileNearestRank(int[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            percentile = Math.Clamp(percentile, 0, 1);
            var rank = (int)Math.Ceiling(percentile * sorted.Length);
            var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
            return sorted[index];
        }
    }

    private readonly object _sync = new();
    private RunState? _active;

    public PerformanceStartResponse Start(Dispatcher dispatcher, PerformanceStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(request);

        var probeIntervalMs = Math.Clamp(request.ProbeIntervalMs, 10, 1000);
        var autoStopAfterMs = Math.Clamp(request.AutoStopAfterMs, 100, 300_000);

        lock (_sync)
        {
            if (_active is { Summary: null } running)
            {
                if (!request.ResetIfRunning)
                {
                    throw new InvalidOperationException($"performance_already_running: runId={running.RunId}");
                }

                _ = running.TryStop(out _);
                _active = null;
            }

            var runId = Guid.NewGuid().ToString("N");
            var startedAtUtc = DateTime.UtcNow;
            var run = new RunState(runId, startedAtUtc, dispatcher, probeIntervalMs, autoStopAfterMs);
            run.Start();
            _active = run;

            return new PerformanceStartResponse(
                RunId: runId,
                StartedAtUtc: startedAtUtc,
                ProbeIntervalMs: probeIntervalMs,
                AutoStopAfterMs: autoStopAfterMs);
        }
    }

    public PerformanceStopResponse Stop(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_sync)
        {
            if (_active is null)
            {
                throw new InvalidOperationException("performance_not_running");
            }

            if (!string.Equals(_active.RunId, runId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"performance_run_id_mismatch: activeRunId={_active.RunId}");
            }

            _ = _active.TryStop(out var summary);
            summary ??= _active.Summary;
            if (summary is null)
            {
                throw new InvalidOperationException("performance_stop_failed");
            }

            return new PerformanceStopResponse(summary);
        }
    }
}

