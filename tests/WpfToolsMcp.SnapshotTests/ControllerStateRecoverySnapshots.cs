using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ControllerStateRecoverySnapshots
{
    private McpTestContext _mcp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task Attach_failure_does_not_block_followup_launch_snapshot()
    {
        var attachFailure = await CaptureAttachFailureToCurrentProcessAsync();

        var launch = await LaunchTestAppAsync();
        try
        {
            var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId
            });
            var stableWindows = windows.Windows
                .Select(w => w with { Handle = 0, Bounds = w.Bounds with { X = 0, Y = 0 } })
                .ToArray();

            await Verifier.Verify(new
            {
                AttachFailure = attachFailure,
                Launch = launch with { SessionId = "<session>", Pid = -1 },
                Windows = stableWindows
            });
        }
        finally
        {
            await CloseSessionAsync(launch.SessionId);
        }
    }

    [Test]
    public async Task CloseSession_removes_session_snapshot()
    {
        var firstLaunch = await LaunchTestAppAsync();
        try
        {
            var sessionsBeforeClose = await _mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
            var firstProcessAliveBeforeClose = IsProcessAlive(firstLaunch.Pid);

            var close = await CloseSessionAsync(firstLaunch.SessionId);
            var firstProcessAliveAfterClose = IsProcessAlive(firstLaunch.Pid);
            var sessionsAfterClose = await _mcp.CallToolAsync<ListSessionsResponse>("list_sessions");

            await Verifier.Verify(new
            {
                FirstLaunch = firstLaunch with { SessionId = "<session>", Pid = -1 },
                SessionsBeforeClose = sessionsBeforeClose.Sessions.Select(s => s with { SessionId = "<session>", Pid = -1, ActiveWindowHandle = 0, CreatedAtUtc = "<time>" }).ToArray(),
                FirstProcessAliveBeforeClose = firstProcessAliveBeforeClose,
                Close = close,
                FirstProcessAliveAfterClose = firstProcessAliveAfterClose,
                SessionsAfterClose = sessionsAfterClose.Sessions.Select(s => s with { SessionId = "<session>", Pid = -1, ActiveWindowHandle = 0, CreatedAtUtc = "<time>" }).ToArray(),
            });
        }
        finally
        {
            KillProcessIfRunning(firstLaunch.Pid);
        }
    }

    [Test]
    public async Task DetachSession_keeps_launched_process_alive_and_allows_reattach()
    {
        var markerPath = CreateLifecycleMarkerPath();
        LaunchAppResponse? launch = null;
        string? activeSessionId = null;
        var pid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath);
            activeSessionId = launch.SessionId;
            pid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");

            var detached = await _mcp.CallToolAsync<DetachSessionResponse>("detach_session", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId
            });
            activeSessionId = null;

            Assert.Multiple(() =>
            {
                Assert.That(detached.Pid, Is.EqualTo(launch.Pid));
                Assert.That(detached.SessionRemoved, Is.True);
                Assert.That(detached.ProcessWasRunning, Is.True);
                Assert.That(detached.ProcessWasRunningObserved, Is.True);
                Assert.That(detached.ProcessStillRunning, Is.True);
                Assert.That(detached.ProcessStillRunningObserved, Is.True);
                Assert.That(IsProcessAlive(launch.Pid), Is.True);
            });
            await AssertSessionRemovedAsync(launch.SessionId);

            var reattached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = launch.Pid
            });
            activeSessionId = reattached.SessionId;

            await AssertLifecycleProbeUsableAsync(reattached.SessionId);
            Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started" }));
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(pid);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task DetachSession_keeps_attached_process_alive_and_allows_reattach()
    {
        var markerPath = CreateLifecycleMarkerPath();
        using var process = StartLifecycleProbe(markerPath);
        string? activeSessionId = null;
        try
        {
            await WaitForLifecycleMarkerAsync(markerPath, "started");
            var attached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = process.Id
            });
            activeSessionId = attached.SessionId;

            var detached = await _mcp.CallToolAsync<DetachSessionResponse>("detach_session", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId
            });
            activeSessionId = null;

            Assert.Multiple(() =>
            {
                Assert.That(detached.SessionRemoved, Is.True);
                Assert.That(detached.ProcessWasRunning, Is.True);
                Assert.That(detached.ProcessWasRunningObserved, Is.True);
                Assert.That(detached.ProcessStillRunning, Is.True);
                Assert.That(detached.ProcessStillRunningObserved, Is.True);
                Assert.That(IsProcessAlive(process.Id), Is.True);
            });
            await AssertSessionRemovedAsync(attached.SessionId);

            var reattached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = process.Id
            });
            activeSessionId = reattached.SessionId;

            await AssertLifecycleProbeUsableAsync(reattached.SessionId);
            Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started" }));
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(process.Id);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task CloseApp_dispatches_graceful_close_and_records_window_lifecycle()
    {
        var markerPath = CreateLifecycleMarkerPath();
        LaunchAppResponse? launch = null;
        string? activeSessionId = null;
        var pid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath);
            activeSessionId = launch.SessionId;
            pid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");

            var close = await CloseApplicationAsync(launch.SessionId);
            activeSessionId = null;
            await WaitForLifecycleMarkerAsync(markerPath, "closed");

            Assert.Multiple(() =>
            {
                Assert.That(close.Closed, Is.True);
                Assert.That(close.SessionRemoved, Is.True);
                Assert.That(close.CloseRequested, Is.True);
                Assert.That(close.CloseRequestDispatched, Is.True);
                Assert.That(close.ForceTerminationRequested, Is.False);
                Assert.That(close.ForceTerminationAttempted, Is.False);
                Assert.That(close.ProcessExited, Is.True);
                Assert.That(close.ProcessAlreadyExited, Is.False);
                Assert.That(IsProcessAlive(launch.Pid), Is.False);
                Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started", "closing", "closed" }));
            });
            await AssertSessionRemovedAsync(launch.SessionId);
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(pid);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task TerminateApp_exits_without_dispatching_window_close()
    {
        var markerPath = CreateLifecycleMarkerPath();
        LaunchAppResponse? launch = null;
        string? activeSessionId = null;
        var pid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath);
            activeSessionId = launch.SessionId;
            pid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");

            var close = await TerminateApplicationAsync(launch.SessionId);
            activeSessionId = null;

            Assert.Multiple(() =>
            {
                Assert.That(close.Closed, Is.True);
                Assert.That(close.SessionRemoved, Is.True);
                Assert.That(close.CloseRequested, Is.False);
                Assert.That(close.CloseRequestDispatched, Is.False);
                Assert.That(close.ForceTerminationRequested, Is.True);
                Assert.That(close.ForceTerminationAttempted, Is.True);
                Assert.That(close.ProcessExited, Is.True);
                Assert.That(close.ProcessAlreadyExited, Is.False);
                Assert.That(IsProcessAlive(launch.Pid), Is.False);
                Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started" }));
            });
            await AssertSessionRemovedAsync(launch.SessionId);
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(pid);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task TerminateApp_leaves_child_process_running()
    {
        var markerPath = CreateLifecycleMarkerPath();
        var childPidPath = CreateLifecycleChildPidPath();
        LaunchAppResponse? launch = null;
        string? activeSessionId = null;
        var parentPid = 0;
        var childPid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath, childPidPath: childPidPath);
            activeSessionId = launch.SessionId;
            parentPid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");
            childPid = await WaitForChildPidAsync(childPidPath);

            Assert.Multiple(() =>
            {
                Assert.That(IsProcessAlive(parentPid), Is.True);
                Assert.That(IsProcessAlive(childPid), Is.True);
            });

            var close = await TerminateApplicationAsync(launch.SessionId);
            activeSessionId = null;
            await Task.Delay(500);

            Assert.Multiple(() =>
            {
                Assert.That(close.ProcessExited, Is.True);
                Assert.That(close.ForceTerminationRequested, Is.True);
                Assert.That(close.ForceTerminationAttempted, Is.True);
                Assert.That(IsProcessAlive(parentPid), Is.False);
                Assert.That(IsProcessAlive(childPid), Is.True);
            });
            await AssertSessionRemovedAsync(launch.SessionId);
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(parentPid);
            KillProcessIfRunning(childPid);
            DeleteFileBestEffort(markerPath);
            DeleteFileBestEffort(childPidPath);
        }
    }

    [Test]
    public async Task CloseApp_veto_reports_dispatched_request_without_conflating_session_removal_and_exit()
    {
        var markerPath = CreateLifecycleMarkerPath();
        LaunchAppResponse? launch = null;
        string? activeSessionId = null;
        var pid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath, vetoClose: true);
            activeSessionId = launch.SessionId;
            pid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");

            var close = await CloseApplicationAsync(launch.SessionId, timeoutMs: 250);
            activeSessionId = null;
            await WaitForLifecycleMarkerAsync(markerPath, "close-vetoed");

            Assert.Multiple(() =>
            {
                Assert.That(close.Closed, Is.False);
                Assert.That(close.SessionRemoved, Is.True);
                Assert.That(close.CloseRequested, Is.True);
                Assert.That(close.CloseRequestDispatched, Is.True);
                Assert.That(close.ForceTerminationRequested, Is.False);
                Assert.That(close.ForceTerminationAttempted, Is.False);
                Assert.That(close.ProcessExited, Is.False);
                Assert.That(close.ProcessAlreadyExited, Is.False);
                Assert.That(IsProcessAlive(launch.Pid), Is.True);
                Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started", "closing", "close-vetoed" }));
            });
            await AssertSessionRemovedAsync(launch.SessionId);
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(pid);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task CloseSession_compatibility_path_reports_close_and_force_intent()
    {
        var launch = await LaunchTestAppAsync();
        try
        {
            var close = await CloseSessionAsync(launch.SessionId);

            Assert.That(close, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(close!.SessionRemoved, Is.True);
                Assert.That(close.CloseRequested, Is.True);
                Assert.That(close.CloseRequestDispatched, Is.True);
                Assert.That(close.ForceTerminationRequested, Is.True);
                Assert.That(close.ForceTerminationAttempted, Is.False);
                Assert.That(close.ProcessExited, Is.True);
            });
        }
        finally
        {
            KillProcessIfRunning(launch.Pid);
        }
    }

    [Test]
    public async Task CloseSession_without_force_preserves_legacy_closed_while_reporting_vetoed_exit()
    {
        var markerPath = CreateLifecycleMarkerPath();
        LaunchAppResponse? launch = null;
        var pid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath, vetoClose: true);
            pid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");

            var close = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["force"] = false,
                ["timeoutMs"] = 250
            });
            await WaitForLifecycleMarkerAsync(markerPath, "close-vetoed");

            Assert.Multiple(() =>
            {
                Assert.That(close.SessionRemoved, Is.True);
                Assert.That(close.Closed, Is.True);
                Assert.That(close.ProcessExited, Is.False);
                Assert.That(close.CloseRequested, Is.True);
                Assert.That(close.CloseRequestDispatched, Is.True);
                Assert.That(close.ForceTerminationRequested, Is.False);
                Assert.That(close.ForceTerminationAttempted, Is.False);
                Assert.That(IsProcessAlive(launch.Pid), Is.True);
                Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started", "closing", "close-vetoed" }));
            });
            await AssertSessionRemovedAsync(launch.SessionId);
        }
        finally
        {
            KillProcessIfRunning(pid);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task CloseApp_reports_intent_without_dispatch_for_an_already_exited_process()
    {
        var markerPath = CreateLifecycleMarkerPath();
        LaunchAppResponse? launch = null;
        string? activeSessionId = null;
        var pid = 0;
        try
        {
            launch = await LaunchLifecycleProbeAsync(markerPath);
            activeSessionId = launch.SessionId;
            pid = launch.Pid;
            await WaitForLifecycleMarkerAsync(markerPath, "started");
            KillProcessIfRunning(launch.Pid);
            Assert.That(IsProcessAlive(launch.Pid), Is.False);

            var close = await CloseApplicationAsync(launch.SessionId);
            activeSessionId = null;

            Assert.Multiple(() =>
            {
                Assert.That(close.Closed, Is.True);
                Assert.That(close.SessionRemoved, Is.True);
                Assert.That(close.CloseRequested, Is.True);
                Assert.That(close.CloseRequestDispatched, Is.False);
                Assert.That(close.ForceTerminationRequested, Is.False);
                Assert.That(close.ForceTerminationAttempted, Is.False);
                Assert.That(close.ProcessExited, Is.True);
                Assert.That(close.ProcessAlreadyExited, Is.True);
                Assert.That(ReadLifecycleMarkers(markerPath), Is.EqualTo(new[] { "started" }));
            });
            await AssertSessionRemovedAsync(launch.SessionId);
        }
        finally
        {
            await EndSessionBestEffortAsync(activeSessionId);
            KillProcessIfRunning(pid);
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task ListSessions_marks_externally_exited_session_unavailable_snapshot()
    {
        var launch = await LaunchTestAppAsync();
        try
        {
            KillProcessIfRunning(launch.Pid);
            Assert.That(IsProcessAlive(launch.Pid), Is.False);

            var sessions = await _mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
            var session = sessions.Sessions.Single(s => s.SessionId == launch.SessionId);
            var close = await CloseSessionAsync(launch.SessionId);

            Assert.That(session.BackendCapabilities, Does.Not.Contain("uia"));
            Assert.That(session.BackendCapabilities, Does.Not.Contain("wpf"));
            Assert.That(session.BackendCapabilityStates.Single(s => s.Backend == "uia").State, Is.EqualTo("unavailable"));
            Assert.That(session.BackendCapabilityStates.Single(s => s.Backend == "wpf").State, Is.EqualTo("unavailable"));
            Assert.That(session.ActiveWindowHandle, Is.Zero);
            Assert.That(session.ActiveWindowTitle, Is.Empty);
            Assert.That(close, Is.Not.Null);

            await Verifier.Verify(new
            {
                Launch = launch with { SessionId = "<session>", Pid = -1 },
                Session = session with
                {
                    SessionId = "<session>",
                    Pid = -1,
                    ActiveWindowHandle = 0,
                    CreatedAtUtc = "<time>"
                },
                Close = close
            });
        }
        finally
        {
            KillProcessIfRunning(launch.Pid);
            _ = await CloseSessionAsync(launch.SessionId);
        }
    }

    [Test]
    public async Task CloseSession_unknown_session_reports_error_snapshot()
    {
        InvalidOperationException? ex = null;
        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = "missing-session",
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.That(ex, Is.Not.Null);
        await Verifier.Verify(ex!.Message.Split("--- server stderr", StringSplitOptions.None)[0].TrimEnd());
    }

    [Test]
    public async Task ListSessions_reports_wpf_backend_immediately_after_launch_snapshot()
    {
        var launch = await LaunchTestAppAsync();
        try
        {
            var sessions = await _mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
            var session = sessions.Sessions.Single(s => s.SessionId == launch.SessionId);

            Assert.That(session.BackendCapabilities, Does.Contain("uia"));
            Assert.That(session.BackendCapabilities, Does.Contain("wpf"));
            Assert.That(session.BackendCapabilityStates.Single(s => s.Backend == "wpf").State, Is.EqualTo("ready"));

            await Verifier.Verify(new
            {
                Launch = launch with { SessionId = "<session>", Pid = -1 },
                Session = session with
                {
                    SessionId = "<session>",
                    Pid = -1,
                    ActiveWindowHandle = 0,
                    CreatedAtUtc = "<time>"
                }
            });
        }
        finally
        {
            await CloseSessionAsync(launch.SessionId);
        }
    }

    [Test]
    public async Task Attach_by_process_name_accepts_dotted_name_with_or_without_exe_suffix()
    {
        var withoutExeSuffix = await AttachByProcessNameAsync(includeExeSuffix: false);
        var withExeSuffix = await AttachByProcessNameAsync(includeExeSuffix: true);

        Assert.Multiple(() =>
        {
            Assert.That(withoutExeSuffix.Pid, Is.GreaterThan(0));
            Assert.That(withExeSuffix.Pid, Is.GreaterThan(0));
            Assert.That(withoutExeSuffix.ProcessName, Is.EqualTo(withExeSuffix.ProcessName));
        });
    }

    private async Task<LaunchAppResponse> LaunchTestAppAsync()
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        return await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });
    }

    private async Task<LaunchAppResponse> LaunchLifecycleProbeAsync(
        string markerPath,
        bool vetoClose = false,
        string? childPidPath = null)
    {
        var exePath = TestAppPaths.FindLifecycleProbeTestAppExecutable();
        var args = new List<string> { "--marker-path", markerPath };
        if (vetoClose)
        {
            args.Add("--veto-close");
        }

        if (!string.IsNullOrWhiteSpace(childPidPath))
        {
            args.Add("--child-pid-path");
            args.Add(childPidPath);
        }

        return await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = Path.GetDirectoryName(exePath)!,
            ["args"] = args.ToArray()
        });
    }

    private static Process StartLifecycleProbe(string markerPath, bool vetoClose = false)
    {
        var exePath = TestAppPaths.FindLifecycleProbeTestAppExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--marker-path");
        startInfo.ArgumentList.Add(markerPath);
        if (vetoClose)
        {
            startInfo.ArgumentList.Add("--veto-close");
        }

        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Failed to start lifecycle probe process.");
    }

    private async Task AssertLifecycleProbeUsableAsync(string sessionId)
    {
        var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId
        });
        Assert.That(
            windows.Windows.Select(window => window.Title),
            Does.Contain("WPF Tools MCP LifecycleProbe TestApp"));
    }

    private async Task AssertSessionRemovedAsync(string sessionId)
    {
        var sessions = await _mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
        Assert.That(sessions.Sessions, Has.None.Matches<SessionInfo>(session => session.SessionId == sessionId));
    }

    private async Task EndSessionBestEffortAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            _ = await TerminateApplicationAsync(sessionId);
        }
        catch
        {
        }
    }

    private static string CreateLifecycleMarkerPath() =>
        Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-lifecycle-{Guid.NewGuid():N}.log");

    private static string CreateLifecycleChildPidPath() =>
        Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-lifecycle-child-{Guid.NewGuid():N}.pid");

    private static IReadOnlyList<string> ReadLifecycleMarkers(string markerPath)
    {
        try
        {
            return File.Exists(markerPath)
                ? File.ReadAllLines(markerPath).Where(marker => marker.Length > 0).ToArray()
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task WaitForLifecycleMarkerAsync(string markerPath, string expectedMarker)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (ReadLifecycleMarkers(markerPath).Contains(expectedMarker, StringComparer.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            $"Timed out waiting for lifecycle marker '{expectedMarker}'. " +
            $"Observed: [{string.Join(", ", ReadLifecycleMarkers(markerPath))}]");
    }

    private static async Task<int> WaitForChildPidAsync(string childPidPath)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                if (File.Exists(childPidPath) &&
                    int.TryParse(File.ReadAllText(childPidPath), out var childPid) &&
                    IsProcessAlive(childPid))
                {
                    return childPid;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for lifecycle child PID at '{childPidPath}'.");
        return 0;
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private async Task<string> CaptureAttachFailureToCurrentProcessAsync()
    {
        InvalidOperationException? ex = null;
        try
        {
            _ = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId
            });
            Assert.Fail("Expected attach_to_app to fail when targeting the test runner process.");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        return ex!.Message.Split("--- server stderr", StringSplitOptions.None)[0].TrimEnd();
    }

    private async Task<CloseAppResponse?> CloseSessionAsync(string sessionId)
    {
        try
        {
            return await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
            return null;
        }
    }

    private Task<CloseAppResponse> CloseApplicationAsync(string sessionId, int timeoutMs = 2000) =>
        _mcp.CallToolAsync<CloseAppResponse>("close_app", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["timeoutMs"] = timeoutMs
        });

    private Task<CloseAppResponse> TerminateApplicationAsync(string sessionId) =>
        _mcp.CallToolAsync<CloseAppResponse>("terminate_app", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["timeoutMs"] = 2000
        });

    private async Task<AttachToAppResponse> AttachByProcessNameAsync(bool includeExeSuffix)
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;
        var expectedProcessName = Path.GetFileNameWithoutExtension(exePath);

        KillProcessesByName(expectedProcessName);

        Process? process = null;
        AttachToAppResponse? attached = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            });

            if (process is null)
            {
                throw new InvalidOperationException("Failed to start test app process.");
            }

            _ = process.WaitForInputIdle(10_000);

            var requestedName = includeExeSuffix
                ? $"{process.ProcessName}.exe"
                : process.ProcessName;

            attached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["processName"] = requestedName
            });

            var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId
            });
            Assert.That(windows.Windows, Is.Not.Empty);

            return attached;
        }
        finally
        {
            try
            {
                if (attached is not null)
                {
                    _ = await CloseSessionAsync(attached.SessionId);
                }
            }
            catch
            {
            }

            if (process is not null)
            {
                KillProcessIfRunning(process.Id);
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }

            KillProcessesByName(expectedProcessName);
        }
    }

    private static bool IsProcessAlive(int pid)
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

    private static void KillProcessIfRunning(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(2000);
        }
        catch
        {
        }
    }

    private static void KillProcessesByName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
