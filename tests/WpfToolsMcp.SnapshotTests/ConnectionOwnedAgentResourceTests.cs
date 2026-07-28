using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ConnectionOwnedAgentResourceTests
{
    [Test]
    public async Task Detaching_one_session_releases_only_its_agent_resources()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellationToken = testCts.Token;

        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        McpTestContext? mcp = null;
        var firstSessionId = "";
        var secondSessionId = "";
        var pid = 0;

        try
        {
            mcp = await McpTestContext.StartAsync(
                serverExe,
                toolProfile: "diagnostics",
                cancellationToken: cancellationToken);

            var launched = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = exePath,
                ["workingDirectory"] = workingDirectory,
                ["reuseExistingInstance"] = false
            }, cancellationToken);
            firstSessionId = launched.SessionId;
            pid = launched.Pid;

            var attached = await mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = pid
            }, cancellationToken);
            secondSessionId = attached.SessionId;

            Assert.That(attached.Pid, Is.EqualTo(pid));
            Assert.That(secondSessionId, Is.Not.EqualTo(firstSessionId));

            InjectAgentResponse firstInjection;
            try
            {
                firstInjection = await mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
                {
                    ["sessionId"] = firstSessionId
                }, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
            {
                Assert.Ignore(ex.Message);
                return;
            }

            var secondInjection = await mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = secondSessionId
            }, cancellationToken);

            Assert.Multiple(() =>
            {
                Assert.That(firstInjection.Injected, Is.True, "The new test process should require the first injection.");
                Assert.That(secondInjection.Injected, Is.False, "The second session should connect to the existing agent.");
                Assert.That(secondInjection.PipeName, Is.EqualTo(firstInjection.PipeName));
            });

            var firstElement = await ResolveTextBoxAsync(mcp, firstSessionId, cancellationToken);
            var secondElement = await ResolveTextBoxAsync(mcp, secondSessionId, cancellationToken);

            Assert.Multiple(() =>
            {
                Assert.That(firstElement.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
                Assert.That(secondElement.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
                Assert.That(firstElement.Element.ElementId, Does.StartWith("wpf_"));
                Assert.That(secondElement.Element.ElementId, Does.StartWith("wpf_"));
                Assert.That(firstElement.Element.ElementId, Is.Not.EqualTo(secondElement.Element.ElementId));
                Assert.That(firstElement.Element.XPath, Is.EqualTo(secondElement.Element.XPath));
            });

            var subscription = await mcp.CallToolAsync<SubscribeBindingErrorsResponse>("subscribe_binding_errors", new Dictionary<string, object?>
            {
                ["sessionId"] = firstSessionId,
                ["pollIntervalMs"] = 1000,
                ["maxQueue"] = 20
            }, cancellationToken);

            _ = await mcp.CallToolAsync<PollSubscriptionResponse>("poll_subscription", new Dictionary<string, object?>
            {
                ["sessionId"] = firstSessionId,
                ["subscriptionId"] = subscription.SubscriptionId,
                ["timeoutMs"] = 1000,
                ["maxBatch"] = 20
            }, cancellationToken);

            var activePoll = mcp.CallToolAsync<PollSubscriptionResponse>("poll_subscription", new Dictionary<string, object?>
            {
                ["sessionId"] = firstSessionId,
                ["subscriptionId"] = subscription.SubscriptionId,
                ["timeoutMs"] = 30_000,
                ["maxBatch"] = 20
            }, cancellationToken);

            await Task.Delay(200, cancellationToken);
            Assert.That(activePoll.IsCompleted, Is.False, "The regression requires a poll that is active while the session detaches.");

            var firstPerformance = await mcp.CallToolAsync<PerformanceStartResponse>("performance_start", new Dictionary<string, object?>
            {
                ["sessionId"] = firstSessionId,
                ["probeIntervalMs"] = 50,
                ["autoStopAfterMs"] = 120_000
            }, cancellationToken);
            Assert.That(firstPerformance.RunId, Is.Not.Empty);

            var crossOwnerStopFailure = await CaptureToolFailureAsync(() =>
                mcp.CallToolAsync<PerformanceStopResponse>("performance_stop", new Dictionary<string, object?>
                {
                    ["sessionId"] = secondSessionId,
                    ["runId"] = firstPerformance.RunId
                }, cancellationToken));
            Assert.That(crossOwnerStopFailure.Message, Does.Contain("performance_run_not_owned").IgnoreCase);

            var detached = await mcp.CallToolAsync<DetachSessionResponse>("detach_session", new Dictionary<string, object?>
            {
                ["sessionId"] = firstSessionId
            }, cancellationToken);

            _ = await activePoll.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Multiple(() =>
            {
                Assert.That(detached.Pid, Is.EqualTo(pid));
                Assert.That(detached.SessionRemoved, Is.True);
                Assert.That(detached.ProcessWasRunningObserved, Is.True);
                Assert.That(detached.ProcessWasRunning, Is.True);
                Assert.That(detached.ProcessStillRunningObserved, Is.True);
                Assert.That(detached.ProcessStillRunning, Is.True);
                Assert.That(IsProcessRunning(pid), Is.True, "Detaching must not close or terminate the target process.");
            });

            var sessions = await mcp.CallToolAsync<ListSessionsResponse>(
                "list_sessions",
                cancellationToken: cancellationToken);
            Assert.Multiple(() =>
            {
                Assert.That(sessions.Sessions.Select(session => session.SessionId), Does.Not.Contain(firstSessionId));
                Assert.That(sessions.Sessions.Select(session => session.SessionId), Does.Contain(secondSessionId));
            });

            var detachedSessionFailure = await CaptureToolFailureAsync(() =>
                mcp.CallToolAsync<GetPathToElementResponse>("get_path_to_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = firstSessionId,
                    ["elementId"] = firstElement.Element.ElementId
                }, cancellationToken));
            Assert.That(detachedSessionFailure.Message, Does.Contain("Unknown sessionId").IgnoreCase);

            var detachedHandleFailure = await CaptureToolFailureAsync(() =>
                mcp.CallToolAsync<GetPathToElementResponse>("get_path_to_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = secondSessionId,
                    ["elementId"] = firstElement.Element.ElementId
                }, cancellationToken));
            Assert.That(detachedHandleFailure.Message, Does.Contain("Unknown elementId").IgnoreCase);

            var detachedSubscriptionFailure = await CaptureToolFailureAsync(() =>
                mcp.CallToolAsync<PollSubscriptionResponse>("poll_subscription", new Dictionary<string, object?>
                {
                    ["sessionId"] = secondSessionId,
                    ["subscriptionId"] = subscription.SubscriptionId,
                    ["timeoutMs"] = 0,
                    ["maxBatch"] = 20
                }, cancellationToken));
            Assert.That(detachedSubscriptionFailure.Message, Does.Contain("Unknown subscriptionId").IgnoreCase);

            var secondPerformance = await StartPerformanceAfterOwnerReleaseAsync(
                mcp,
                secondSessionId,
                cancellationToken);
            await Task.Delay(350, cancellationToken);

            var secondPerformanceStop = await mcp.CallToolAsync<PerformanceStopResponse>("performance_stop", new Dictionary<string, object?>
            {
                ["sessionId"] = secondSessionId,
                ["runId"] = secondPerformance.RunId
            }, cancellationToken);
            Assert.That(secondPerformanceStop.Summary.SampleCount, Is.GreaterThan(0));

            var secondPath = await mcp.CallToolAsync<GetPathToElementResponse>("get_path_to_element", new Dictionary<string, object?>
            {
                ["sessionId"] = secondSessionId,
                ["elementId"] = secondElement.Element.ElementId
            }, cancellationToken);

            Assert.Multiple(() =>
            {
                Assert.That(secondPath.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
                Assert.That(secondPath.XPath, Is.EqualTo(secondElement.Element.XPath));
                Assert.That(IsProcessRunning(pid), Is.True, "The surviving session must remain attached to a live process.");
            });
        }
        finally
        {
            await CleanupAsync(mcp, secondSessionId, firstSessionId, pid);
        }
    }

    private static Task<ResolveElementResponse> ResolveTextBoxAsync(
        McpTestContext mcp,
        string sessionId,
        CancellationToken cancellationToken) =>
        mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["backend"] = "wpf",
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_TextBox"
            }
        }, cancellationToken);

    private static async Task<PerformanceStartResponse> StartPerformanceAfterOwnerReleaseAsync(
        McpTestContext mcp,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        InvalidOperationException? lastOwnerConflict = null;

        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                return await mcp.CallToolAsync<PerformanceStartResponse>("performance_start", new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["probeIntervalMs"] = 50,
                    ["autoStopAfterMs"] = 5000
                }, cancellationToken);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("performance_already_running", StringComparison.OrdinalIgnoreCase))
            {
                lastOwnerConflict = ex;
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new AssertionException(
            "The detached connection still owned the active performance run after five seconds.",
            lastOwnerConflict);
    }

    private static async Task<InvalidOperationException> CaptureToolFailureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new AssertionException("Expected the MCP tool call to fail.");
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ShouldSkipForMissingAssets(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CleanupAsync(
        McpTestContext? mcp,
        string secondSessionId,
        string firstSessionId,
        int pid)
    {
        if (mcp is not null)
        {
            var cleanupSessionId = !string.IsNullOrWhiteSpace(secondSessionId)
                ? secondSessionId
                : firstSessionId;

            if (!string.IsNullOrWhiteSpace(cleanupSessionId))
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    _ = await mcp.CallToolAsync<CloseAppResponse>("terminate_app", new Dictionary<string, object?>
                    {
                        ["sessionId"] = cleanupSessionId,
                        ["timeoutMs"] = 3000
                    }, cleanupCts.Token);
                }
                catch
                {
                }
            }
        }

        if (pid > 0)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(exitCts.Token);
                }
            }
            catch
            {
            }
        }

        if (mcp is not null)
        {
            try
            {
                await mcp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }
        }
    }
}
