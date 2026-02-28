using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class PerformanceSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);

        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task PerformanceStartStop_snapshot()
    {
        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
            return;
        }

        var start = await _mcp.CallToolAsync<PerformanceStartResponse>("performance_start", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["probeIntervalMs"] = 50,
            ["autoStopAfterMs"] = 2000
        });

        await Task.Delay(350);

        var stop = await _mcp.CallToolAsync<PerformanceStopResponse>("performance_stop", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["runId"] = start.RunId
        });

        var stop2 = await _mcp.CallToolAsync<PerformanceStopResponse>("performance_stop", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["runId"] = start.RunId
        });

        var stable = new
        {
            Start = new
            {
                HasRunId = !string.IsNullOrWhiteSpace(start.RunId),
                start.ProbeIntervalMs,
                start.AutoStopAfterMs
            },
            Summary = ScrubSummary(stop.Summary),
            SecondStopMatchesFirst = stop2.Summary == stop.Summary
        };

        Assert.That(stop.Summary.SampleCount, Is.GreaterThan(0), "Expected at least one latency sample.");

        await Verifier.Verify(stable);
    }

    private static object ScrubSummary(PerformanceSummary summary) => new
    {
        HasRunId = !string.IsNullOrWhiteSpace(summary.RunId),
        summary.ProbeIntervalMs,
        SampleCountPositive = summary.SampleCount > 0,
        DroppedProbeCountNonNegative = summary.DroppedProbeCount >= 0,
        P95 = Bucket(summary.P95LatencyMs),
        Max = Bucket(summary.MaxLatencyMs)
    };

    private static string Bucket(double latencyMs) =>
        latencyMs < 50 ? "<50"
        : latencyMs < 100 ? "50-99"
        : latencyMs < 250 ? "100-249"
        : latencyMs < 1000 ? "250-999"
        : ">=1000";

    private static bool ShouldSkipForMissingAssets(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }
}
