using NUnit.Framework;
using VerifyNUnit;
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
