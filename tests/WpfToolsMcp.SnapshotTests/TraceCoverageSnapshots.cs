using System.Text.Json;
using NUnit.Framework;
using VerifyNUnit;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class TraceCoverageSnapshots
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
    public async Task Trace_records_session_tools_even_on_failures()
    {
        var traceStart = await _mcp.CallToolAsync<TraceStartResponse>("trace_start", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["resetIfRunning"] = true
        });

        _ = await _mcp.CallToolAsync<GetActiveWindowResponse>("get_active_window", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId
        });

        SubscribeBindingErrorsResponse? sub = null;
        try
        {
            sub = await _mcp.CallToolAsync<SubscribeBindingErrorsResponse>("subscribe_binding_errors", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["pollIntervalMs"] = 100,
                ["maxQueue"] = 10
            });
        }
        catch
        {
        }

        try
        {
            _ = await _mcp.CallToolAsync<PollSubscriptionResponse>("poll_subscription", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["subscriptionId"] = "does-not-exist",
                ["timeoutMs"] = 0,
                ["maxBatch"] = 5
            });
        }
        catch
        {
        }

        if (sub is not null)
        {
            _ = await _mcp.CallToolAsync<UnsubscribeResponse>("unsubscribe", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["subscriptionId"] = sub.SubscriptionId
            });
        }

        _ = await _mcp.CallToolAsync<UnsubscribeResponse>("unsubscribe", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["subscriptionId"] = "does-not-exist"
        });

        _ = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId
        });

        var traceStop = await _mcp.CallToolAsync<TraceStopResponse>("trace_stop", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["traceId"] = traceStart.TraceId,
            ["includeEvents"] = true
        });

        var expectedTools = new[]
        {
            "get_active_window",
            "subscribe_binding_errors",
            "poll_subscription",
            "unsubscribe",
            "take_screenshot"
        };

        var stable = new
        {
            traceStop.TraceId,
            Tools = expectedTools.Select(tool =>
            {
                var events = traceStop.Events!.Where(e => string.Equals(e.Tool, tool, StringComparison.Ordinal)).ToArray();
                return new
                {
                    Tool = tool,
                    Present = events.Length > 0,
                    AnySummary = events.Any(e => !string.IsNullOrWhiteSpace(e.Summary)),
                    AnyError = events.Any(e => !string.IsNullOrWhiteSpace(e.Error))
                };
            }).ToArray()
        };

        await Verifier.Verify(stable);
    }
}

[TestFixture]
public sealed class TraceResponseTests
{
    [Test]
    public async Task Trace_stop_omits_inline_events_by_default_and_preserves_full_artifact()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(resetIfRunning: false);
        var outputPath = CreateOutputPath();

        try
        {
            using (controller.BeginToolTrace("first_tool"))
            {
            }

            using (controller.BeginToolTrace("second_tool"))
            {
            }

            var response = await controller.TraceStopAsync(traceStart.TraceId, outputPath);

            Assert.Multiple(() =>
            {
                Assert.That(response.EventCount, Is.EqualTo(2));
                Assert.That(response.ReturnedEventCount, Is.Zero);
                Assert.That(response.Truncated, Is.False);
                Assert.That(response.TruncatedReason, Is.Null);
                Assert.That(response.Events, Is.Null);
            });

            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var artifactEvents = artifact.RootElement.GetProperty("Events");
            Assert.That(artifactEvents.GetArrayLength(), Is.EqualTo(2));
            Assert.That(artifactEvents[0].GetProperty("Tool").GetString(), Is.EqualTo("first_tool"));
            Assert.That(artifactEvents[1].GetProperty("Tool").GetString(), Is.EqualTo("second_tool"));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Test]
    public async Task Trace_stop_bounds_inline_events_and_reports_truncation_without_truncating_artifact()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(resetIfRunning: false);
        var outputPath = CreateOutputPath();

        try
        {
            foreach (var tool in new[] { "first_tool", "second_tool", "third_tool" })
            {
                using (controller.BeginToolTrace(tool))
                {
                }
            }

            var response = await controller.TraceStopAsync(
                traceStart.TraceId,
                outputPath,
                includeEvents: true,
                maxEvents: 2);

            Assert.Multiple(() =>
            {
                Assert.That(response.EventCount, Is.EqualTo(3));
                Assert.That(response.ReturnedEventCount, Is.EqualTo(2));
                Assert.That(response.Truncated, Is.True);
                Assert.That(response.TruncatedReason, Is.EqualTo("maxEvents"));
                Assert.That(response.Events!.Select(traceEvent => traceEvent.Tool),
                    Is.EqualTo(new[] { "first_tool", "second_tool" }));
            });

            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var artifactEvents = artifact.RootElement.GetProperty("Events");
            Assert.That(artifactEvents.GetArrayLength(), Is.EqualTo(3));
            Assert.That(artifactEvents[2].GetProperty("Tool").GetString(), Is.EqualTo("third_tool"));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string CreateOutputPath() =>
        Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-trace-test-{Guid.NewGuid():N}.json");
}
