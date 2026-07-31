using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Protocol;
using NUnit.Framework;
using VerifyNUnit;
using WpfToolsMcp.Automation;
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
            var uiaCapability = session.BackendCapabilityStates.Single(s => s.Backend == "uia");
            var wpfCapability = session.BackendCapabilityStates.Single(s => s.Backend == "wpf");

            Assert.Multiple(() =>
            {
                Assert.That(session.BackendCapabilities, Does.Not.Contain("uia"));
                Assert.That(session.BackendCapabilities, Does.Not.Contain("wpf"));
                Assert.That(uiaCapability.State, Is.EqualTo("unavailable"));
                Assert.That(wpfCapability.State, Is.EqualTo("unavailable"));
                Assert.That(uiaCapability.Failure?.Code, Is.EqualTo("target_exited"));
                Assert.That(wpfCapability.Failure?.Code, Is.EqualTo("target_exited"));
                Assert.That(wpfCapability.Failure?.Stage, Is.EqualTo("target_shutdown"));
                Assert.That(uiaCapability.Failure?.Stage, Is.EqualTo("target_shutdown"));
                Assert.That(uiaCapability.Failure?.Retryable, Is.False);
                Assert.That(
                    uiaCapability.Failure?.RecoveryActions,
                    Is.EqualTo(new[] { "restart_target", "reattach" }));
                Assert.That(session.ActiveWindowHandle, Is.Zero);
                Assert.That(session.ActiveWindowTitle, Is.Empty);
                Assert.That(close, Is.Not.Null);
            });

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
    public async Task ListSessions_reports_wpf_backend_as_not_initialized_without_injecting_snapshot()
    {
        var launch = await LaunchTestAppAsync();
        try
        {
            var sessions = await _mcp.CallToolAsync<ListSessionsResponse>("list_sessions");
            var session = sessions.Sessions.Single(s => s.SessionId == launch.SessionId);
            var uiaCapability = session.BackendCapabilityStates.Single(s => s.Backend == "uia");
            var wpfCapability = session.BackendCapabilityStates.Single(s => s.Backend == "wpf");

            Assert.Multiple(() =>
            {
                Assert.That(session.BackendCapabilities, Is.EqualTo(new[] { "uia" }));
                Assert.That(uiaCapability.State, Is.EqualTo("ready"));
                Assert.That(uiaCapability.Failure, Is.Null);
                Assert.That(wpfCapability.State, Is.EqualTo("not_initialized"));
                Assert.That(wpfCapability.Failure, Is.Null);
            });

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

    [Test]
    public async Task Attach_by_ambiguous_process_name_returns_structured_candidates()
    {
        var lifecycleProbeExe = CreateUniqueLifecycleProbeExecutable();
        var processName = Path.GetFileNameWithoutExtension(lifecycleProbeExe);
        var firstMarkerPath = CreateLifecycleMarkerPath();
        var secondMarkerPath = CreateLifecycleMarkerPath();
        Process? first = null;
        Process? second = null;
        try
        {
            first = StartLifecycleProbe(firstMarkerPath, executablePath: lifecycleProbeExe);
            second = StartLifecycleProbe(secondMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(firstMarkerPath, "started");
            await WaitForLifecycleMarkerAsync(secondMarkerPath, "started");

            var result = await _mcp.CallToolResultAsync("attach_to_app", new Dictionary<string, object?>
            {
                ["processName"] = processName
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsError, Is.True);
                Assert.That(result.StructuredContent, Is.Not.Null);
                Assert.That(
                    result.Content.OfType<TextContentBlock>().Single().Text,
                    Does.Contain("ambiguous_process"));
            });

            var ambiguity = JsonSerializer.Deserialize<ToolErrorResponse>(
                result.StructuredContent!.Value.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Error;

            Assert.That(ambiguity, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(ambiguity!.Code, Is.EqualTo("ambiguous_process"));
                Assert.That(ambiguity.Context!.DiscoveredCandidates, Is.EqualTo(2));
                Assert.That(ambiguity.Context.ReturnedCandidates, Is.EqualTo(2));
                Assert.That(ambiguity.Context.Truncated, Is.False);
                Assert.That(ambiguity.Context.Candidates!.Select(candidate => candidate.Pid),
                    Is.EquivalentTo(new[] { first.Id, second.Id }));
                Assert.That(ambiguity.Context.Candidates!.Select(candidate => candidate.Index), Is.EqualTo(new[] { 0, 1 }));
                Assert.That(ambiguity.Context.Candidates!.All(candidate => candidate.ProcessInstanceId?.Length > 0), Is.True);
                Assert.That(ambiguity.Context.Candidates!.All(candidate => candidate.WindowHandle > 0), Is.True);
                Assert.That(ambiguity.Context.Candidates!.All(candidate => candidate.ProcessName == processName), Is.True);
                Assert.That(
                    ambiguity.Context.Candidates!.All(candidate =>
                        string.Equals(
                            candidate.ExecutablePath,
                            Path.GetFullPath(lifecycleProbeExe),
                            StringComparison.OrdinalIgnoreCase)),
                    Is.True);
                Assert.That(
                    ambiguity.Context.Candidates!.All(candidate =>
                        candidate.ExecutablePathUnavailableReason is null),
                    Is.True);
                Assert.That(ambiguity.Cause?.Message, Does.Contain(processName));
            });

            var staleCandidate = ambiguity!.Context!.Candidates!.Single(candidate => candidate.Pid == first.Id);
            var liveCandidate = ambiguity.Context.Candidates!.Single(candidate => candidate.Pid == second.Id);
            KillProcessAndRequireExit(first.Id);

            var staleSelection = await CaptureToolFailureAsync(() =>
                _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
                {
                    ["processInstanceId"] = staleCandidate.ProcessInstanceId
                }));
            Assert.That(staleSelection.Message, Does.Contain("stale_process_candidate"));

            var selected = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["processInstanceId"] = liveCandidate.ProcessInstanceId
            });
            Assert.That(selected.Pid, Is.EqualTo(second.Id));
            _ = await CloseSessionAsync(selected.SessionId);
        }
        finally
        {
            KillProcessIfRunning(first?.Id ?? 0);
            KillProcessIfRunning(second?.Id ?? 0);
            first?.Dispose();
            second?.Dispose();
            DeleteFileBestEffort(firstMarkerPath);
            DeleteFileBestEffort(secondMarkerPath);
            DeleteFileBestEffort(lifecycleProbeExe);
        }
    }

    [Test]
    public async Task Launch_reuse_existing_is_deterministic_and_reports_ambiguous_candidates()
    {
        var lifecycleProbeExe = CreateUniqueLifecycleProbeExecutable();
        var firstMarkerPath = CreateLifecycleMarkerPath();
        var secondMarkerPath = CreateLifecycleMarkerPath();
        var launcherMarkerPath = CreateLifecycleMarkerPath();
        Process? first = null;
        Process? second = null;
        string? reusedSessionId = null;
        try
        {
            first = StartLifecycleProbe(firstMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(firstMarkerPath, "started");

            var launchArguments = new[]
            {
                "--marker-path",
                launcherMarkerPath,
                "--exit-immediately"
            };
            var reused = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = lifecycleProbeExe,
                ["workingDirectory"] = Path.GetDirectoryName(lifecycleProbeExe)!,
                ["args"] = launchArguments,
                ["waitForMainWindowMs"] = 1000,
                ["reuseExistingInstance"] = true
            });
            reusedSessionId = reused.SessionId;
            Assert.That(reused.Pid, Is.EqualTo(first.Id));

            _ = await _mcp.CallToolAsync<DetachSessionResponse>("detach_session", new Dictionary<string, object?>
            {
                ["sessionId"] = reusedSessionId
            });
            reusedSessionId = null;

            second = StartLifecycleProbe(secondMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(secondMarkerPath, "started");
            var ambiguousResult = await _mcp.CallToolResultAsync("launch_app", new Dictionary<string, object?>
            {
                ["exePath"] = lifecycleProbeExe,
                ["workingDirectory"] = Path.GetDirectoryName(lifecycleProbeExe)!,
                ["args"] = launchArguments,
                ["waitForMainWindowMs"] = 1000,
                ["reuseExistingInstance"] = true
            });
            var ambiguity = JsonSerializer.Deserialize<ToolErrorResponse>(
                ambiguousResult.StructuredContent!.Value.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Error;

            Assert.Multiple(() =>
            {
                Assert.That(ambiguousResult.IsError, Is.True);
                Assert.That(ambiguity, Is.Not.Null);
                Assert.That(ambiguity!.Code, Is.EqualTo("ambiguous_process"));
                Assert.That(ambiguity.Context!.Candidates!.Select(candidate => candidate.Pid),
                    Is.EquivalentTo(new[] { first.Id, second.Id }));
            });
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(reusedSessionId))
            {
                try
                {
                    _ = await _mcp.CallToolAsync<DetachSessionResponse>("detach_session", new Dictionary<string, object?>
                    {
                        ["sessionId"] = reusedSessionId
                    });
                }
                catch
                {
                }
            }

            KillProcessIfRunning(first?.Id ?? 0);
            KillProcessIfRunning(second?.Id ?? 0);
            first?.Dispose();
            second?.Dispose();
            DeleteFileBestEffort(firstMarkerPath);
            DeleteFileBestEffort(secondMarkerPath);
            DeleteFileBestEffort(launcherMarkerPath);
            DeleteFileBestEffort(lifecycleProbeExe);
        }
    }

    [Test]
    public async Task Reattach_replaces_exited_target_and_invalidates_previous_identities()
    {
        var lifecycleProbeExe = CreateUniqueLifecycleProbeExecutable();
        var firstMarkerPath = CreateLifecycleMarkerPath();
        var replacementMarkerPath = CreateLifecycleMarkerPath();
        Process? first = null;
        Process? replacement = null;
        AttachToAppResponse? attached = null;
        string? successorSessionId = null;
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");
        try
        {
            first = StartLifecycleProbe(firstMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(firstMarkerPath, "started");
            attached = await mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = first.Id,
                ["interactionPolicy"] = new InteractionPolicy(
                    AllowForegroundActivation: false,
                    AllowPhysicalInput: false)
            });

            var oldElement = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId,
                ["backend"] = InspectionBackend.Uia,
                ["locator"] = new ElementLocator(AutomationId: "LifecycleProbe_Status")
            });
            var oldWindow = (await mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId
            })).Windows.Single(window => window.Title == "WPF Tools MCP LifecycleProbe TestApp");

            KillProcessAndRequireExit(first.Id);
            replacement = StartLifecycleProbe(replacementMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(replacementMarkerPath, "started");

            var reattached = await mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId
            });
            successorSessionId = reattached.SessionId;
            var replacementWindows = await mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = successorSessionId
            });

            Assert.Multiple(() =>
            {
                Assert.That(reattached.SessionId, Is.Not.EqualTo(attached.SessionId));
                Assert.That(reattached.Pid, Is.EqualTo(replacement.Id));
                Assert.That(reattached.ProcessInstanceId, Is.Not.Null.And.Not.Empty);
                Assert.That(reattached.InteractionPolicy, Is.EqualTo(attached.InteractionPolicy));
                Assert.That(reattached.Recovery, Is.Not.Null);
                Assert.That(reattached.Recovery!.PreviousSessionId, Is.EqualTo(attached.SessionId));
                Assert.That(reattached.Recovery.SuccessorSessionId, Is.EqualTo(reattached.SessionId));
                Assert.That(reattached.Recovery!.PreviousPid, Is.EqualTo(first.Id));
                Assert.That(reattached.Recovery.WindowHandlesInvalidated, Is.True);
                Assert.That(reattached.Recovery.ElementIdsInvalidated, Is.True);
                Assert.That(reattached.Recovery.SubscriptionsCleared, Is.True);
                Assert.That(reattached.ActiveWindow, Is.Not.Null);
                Assert.That(reattached.ActiveWindow!.Handle, Is.Not.Zero);
                Assert.That(reattached.ActiveWindow.Title, Is.EqualTo("WPF Tools MCP LifecycleProbe TestApp"));
                Assert.That(replacementWindows.ProcessId, Is.EqualTo(replacement.Id));
                Assert.That(replacementWindows.Windows.Select(window => window.Handle),
                    Does.Contain(reattached.ActiveWindow.Handle));
            });

            var activeWindowHandle = reattached.ActiveWindow!.Handle;
            var staleElement = await CaptureToolFailureAsync(() =>
                mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
                {
                    ["sessionId"] = successorSessionId,
                    ["elementId"] = oldElement.Element.ElementId
                }));
            InvalidOperationException? staleWindow = null;
            InvalidOperationException? staleWindowOpenWait = null;
            InvalidOperationException? staleWindowClosedWait = null;
            InvalidOperationException? staleOwnerWindowWait = null;
            GetUiaTreeResponse? reassignedWindow = null;
            if (oldWindow.Handle == activeWindowHandle)
            {
                reassignedWindow = await mcp.CallToolAsync<GetUiaTreeResponse>("get_uia_tree", new Dictionary<string, object?>
                {
                    ["sessionId"] = successorSessionId,
                    ["windowHandle"] = oldWindow.Handle
                });
            }
            else
            {
                staleWindow = await CaptureToolFailureAsync(() =>
                    mcp.CallToolAsync<GetUiaTreeResponse>("get_uia_tree", new Dictionary<string, object?>
                    {
                        ["sessionId"] = successorSessionId,
                        ["windowHandle"] = oldWindow.Handle
                    }));
                staleWindowOpenWait = await CaptureToolFailureAsync(() =>
                    mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
                    {
                        ["sessionId"] = successorSessionId,
                        ["condition"] = new Dictionary<string, object?>
                        {
                            ["kind"] = WaitConditionKind.WindowOpen.ToString(),
                            ["window"] = new Dictionary<string, object?> { ["handle"] = oldWindow.Handle }
                        },
                        ["timeoutMs"] = 0
                    }));
                staleWindowClosedWait = await CaptureToolFailureAsync(() =>
                    mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
                    {
                        ["sessionId"] = successorSessionId,
                        ["condition"] = new Dictionary<string, object?>
                        {
                            ["kind"] = WaitConditionKind.WindowClosed.ToString(),
                            ["window"] = new Dictionary<string, object?> { ["handle"] = oldWindow.Handle }
                        },
                        ["timeoutMs"] = 0
                    }));
                staleOwnerWindowWait = await CaptureToolFailureAsync(() =>
                    mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
                    {
                        ["sessionId"] = successorSessionId,
                        ["condition"] = new Dictionary<string, object?>
                        {
                            ["kind"] = WaitConditionKind.WindowOpen.ToString(),
                            ["window"] = new Dictionary<string, object?>
                            {
                                ["title"] = "Window that never opens",
                                ["ownerHandle"] = oldWindow.Handle
                            }
                        },
                        ["timeoutMs"] = 0
                    }));
            }
            var staleSession = await CaptureToolFailureAsync(() =>
                mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
                {
                    ["sessionId"] = attached.SessionId
                }));

            Assert.Multiple(() =>
            {
                Assert.That(staleElement.Message, Does.Contain("stale_element"));
                Assert.That(
                    staleWindow?.Message,
                    oldWindow.Handle == activeWindowHandle
                        ? Is.Null
                        : Does.Contain("stale_window"));
                Assert.That(
                    reassignedWindow?.ReturnedNodes,
                    oldWindow.Handle == activeWindowHandle
                        ? Is.GreaterThan(0)
                        : Is.Null);
                Assert.That(
                    staleWindowOpenWait?.Message,
                    oldWindow.Handle == activeWindowHandle
                        ? Is.Null
                        : Does.Contain("stale_window"));
                Assert.That(
                    staleWindowClosedWait?.Message,
                    oldWindow.Handle == activeWindowHandle
                        ? Is.Null
                        : Does.Contain("stale_window"));
                Assert.That(
                    staleOwnerWindowWait?.Message,
                    oldWindow.Handle == activeWindowHandle
                        ? Is.Null
                        : Does.Contain("stale_window"));
                Assert.That(staleSession.Message, Does.Contain("stale_session"));
                Assert.That(staleSession.Message, Does.Contain("process_replaced"));
                Assert.That(staleSession.Message, Does.Contain(successorSessionId));
            });

            var replacementElement = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = successorSessionId,
                ["backend"] = InspectionBackend.Uia,
                ["locator"] = new ElementLocator(AutomationId: "LifecycleProbe_Status")
            });
            Assert.That(replacementElement.Element.ElementId, Is.Not.EqualTo(oldElement.Element.ElementId));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(successorSessionId))
            {
                try
                {
                    _ = await mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
                    {
                        ["sessionId"] = successorSessionId,
                        ["force"] = true,
                        ["timeoutMs"] = 2000
                    });
                }
                catch
                {
                }
            }

            KillProcessIfRunning(first?.Id ?? 0);
            KillProcessIfRunning(replacement?.Id ?? 0);
            first?.Dispose();
            replacement?.Dispose();
            DeleteFileBestEffort(firstMarkerPath);
            DeleteFileBestEffort(replacementMarkerPath);
            DeleteFileBestEffort(lifecycleProbeExe);
        }
    }

    [Test]
    public async Task Reattach_by_ambiguous_process_name_does_not_change_the_session_target()
    {
        var lifecycleProbeExe = CreateUniqueLifecycleProbeExecutable();
        var processName = Path.GetFileNameWithoutExtension(lifecycleProbeExe);
        var firstMarkerPath = CreateLifecycleMarkerPath();
        var replacementOneMarkerPath = CreateLifecycleMarkerPath();
        var replacementTwoMarkerPath = CreateLifecycleMarkerPath();
        Process? first = null;
        Process? replacementOne = null;
        Process? replacementTwo = null;
        AttachToAppResponse? attached = null;
        try
        {
            first = StartLifecycleProbe(firstMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(firstMarkerPath, "started");
            attached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = first.Id
            });
            KillProcessAndRequireExit(first.Id);

            replacementOne = StartLifecycleProbe(replacementOneMarkerPath, executablePath: lifecycleProbeExe);
            replacementTwo = StartLifecycleProbe(replacementTwoMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(replacementOneMarkerPath, "started");
            await WaitForLifecycleMarkerAsync(replacementTwoMarkerPath, "started");

            var result = await _mcp.CallToolResultAsync("attach_to_app", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsError, Is.True);
                Assert.That(result.StructuredContent, Is.Not.Null);
            });

            var ambiguity = JsonSerializer.Deserialize<ToolErrorResponse>(
                result.StructuredContent!.Value.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Error;
            Assert.That(ambiguity, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(ambiguity!.Code, Is.EqualTo("ambiguous_process"));
                Assert.That(ambiguity.Context!.SessionId, Is.EqualTo(attached.SessionId));
                Assert.That(ambiguity.Context.Candidates!.Select(candidate => candidate.Pid),
                    Is.EquivalentTo(new[] { replacementOne.Id, replacementTwo.Id }));
            });

            var explicitlyReattached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId,
                ["pid"] = replacementOne.Id
            });
            Assert.That(explicitlyReattached.Pid, Is.EqualTo(replacementOne.Id));
            attached = explicitlyReattached;
        }
        finally
        {
            if (attached is not null)
            {
                _ = await CloseSessionAsync(attached.SessionId);
            }

            KillProcessIfRunning(first?.Id ?? 0);
            KillProcessIfRunning(replacementOne?.Id ?? 0);
            KillProcessIfRunning(replacementTwo?.Id ?? 0);
            first?.Dispose();
            replacementOne?.Dispose();
            replacementTwo?.Dispose();
            DeleteFileBestEffort(firstMarkerPath);
            DeleteFileBestEffort(replacementOneMarkerPath);
            DeleteFileBestEffort(replacementTwoMarkerPath);
            DeleteFileBestEffort(lifecycleProbeExe);
        }
    }

    [Test]
    public async Task Reattach_rejects_a_still_running_target_without_changing_its_session()
    {
        var markerPath = CreateLifecycleMarkerPath();
        Process? process = null;
        AttachToAppResponse? attached = null;
        try
        {
            process = StartLifecycleProbe(markerPath);
            await WaitForLifecycleMarkerAsync(markerPath, "started");
            attached = await _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
            {
                ["pid"] = process.Id
            });

            var failure = await CaptureToolFailureAsync(() =>
                _mcp.CallToolAsync<AttachToAppResponse>("attach_to_app", new Dictionary<string, object?>
                {
                    ["sessionId"] = attached.SessionId
                }));
            var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
            {
                ["sessionId"] = attached.SessionId
            });

            Assert.Multiple(() =>
            {
                Assert.That(failure.Message, Does.Contain("target_process_still_running"));
                Assert.That(windows.ProcessId, Is.EqualTo(process.Id));
                Assert.That(windows.Windows, Is.Not.Empty);
            });
        }
        finally
        {
            if (attached is not null)
            {
                _ = await CloseSessionAsync(attached.SessionId);
            }

            KillProcessIfRunning(process?.Id ?? 0);
            process?.Dispose();
            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task Reattach_revalidates_the_successor_after_predecessor_calls_drain()
    {
        var lifecycleProbeExe = CreateUniqueLifecycleProbeExecutable();
        var firstMarkerPath = CreateLifecycleMarkerPath();
        var failedReplacementMarkerPath = CreateLifecycleMarkerPath();
        var finalReplacementMarkerPath = CreateLifecycleMarkerPath();
        Process? first = null;
        Process? failedReplacement = null;
        Process? finalReplacement = null;
        Task? heldPredecessorCall = null;
        Task<AttachToAppResponse>? replacementAttempt = null;
        var cleanupInvocations = 0;
        var releasePredecessorCall = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sessions = new SessionManager();
        try
        {
            first = StartLifecycleProbe(firstMarkerPath, executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(firstMarkerPath, "started");
            var attached = await sessions.AttachToAppAsync(
                new AttachToAppRequest(Pid: first.Id),
                CancellationToken.None);
            var (predecessorController, _) = sessions.GetController(attached.SessionId);
            KillProcessAndRequireExit(first.Id);

            failedReplacement = StartLifecycleProbe(
                failedReplacementMarkerPath,
                executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(failedReplacementMarkerPath, "started");

            var predecessorCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            heldPredecessorCall = predecessorController.RunExclusiveAsync(async () =>
            {
                predecessorCallStarted.TrySetResult(true);
                await releasePredecessorCall.Task;
            });
            await predecessorCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            replacementAttempt = sessions.AttachToAppAsync(
                new AttachToAppRequest(SessionId: attached.SessionId),
                () =>
                {
                    Interlocked.Increment(ref cleanupInvocations);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            var retirementWaitStarted = Stopwatch.GetTimestamp();
            while (!predecessorController.IsProcessReplacementRetirementStarted &&
                   Stopwatch.GetElapsedTime(retirementWaitStarted) < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10);
            }

            Assert.That(predecessorController.IsProcessReplacementRetirementStarted, Is.True);
            KillProcessAndRequireExit(failedReplacement.Id);
            releasePredecessorCall.TrySetResult(true);
            await heldPredecessorCall;

            InvalidOperationException? failure = null;
            try
            {
                _ = await replacementAttempt;
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            Assert.That(failure, Is.Not.Null);
            Assert.That(failure!.Message, Does.Contain("stale_process_candidate"));
            Assert.That(cleanupInvocations, Is.Zero);

            finalReplacement = StartLifecycleProbe(
                finalReplacementMarkerPath,
                executablePath: lifecycleProbeExe);
            await WaitForLifecycleMarkerAsync(finalReplacementMarkerPath, "started");
            var recovered = await sessions.AttachToAppAsync(
                new AttachToAppRequest(SessionId: attached.SessionId),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(recovered.SessionId, Is.Not.EqualTo(attached.SessionId));
                Assert.That(recovered.Pid, Is.EqualTo(finalReplacement.Id));
                Assert.That(recovered.Recovery?.PreviousSessionId, Is.EqualTo(attached.SessionId));
            });
        }
        finally
        {
            releasePredecessorCall.TrySetResult(true);
            if (heldPredecessorCall is not null)
            {
                try
                {
                    await heldPredecessorCall;
                }
                catch
                {
                }
            }

            if (replacementAttempt is not null)
            {
                try
                {
                    _ = await replacementAttempt;
                }
                catch
                {
                }
            }

            KillProcessIfRunning(first?.Id ?? 0);
            KillProcessIfRunning(failedReplacement?.Id ?? 0);
            KillProcessIfRunning(finalReplacement?.Id ?? 0);
            first?.Dispose();
            failedReplacement?.Dispose();
            finalReplacement?.Dispose();
            DeleteFileBestEffort(firstMarkerPath);
            DeleteFileBestEffort(failedReplacementMarkerPath);
            DeleteFileBestEffort(finalReplacementMarkerPath);
            DeleteFileBestEffort(lifecycleProbeExe);
        }
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

    private static Process StartLifecycleProbe(
        string markerPath,
        bool vetoClose = false,
        string? executablePath = null)
    {
        var exePath = executablePath ?? TestAppPaths.FindLifecycleProbeTestAppExecutable();
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

    private static string CreateUniqueLifecycleProbeExecutable()
    {
        var sourcePath = TestAppPaths.FindLifecycleProbeTestAppExecutable();
        var destinationPath = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            $"WpfLifecycleProbe_{Guid.NewGuid():N}.exe");
        File.Copy(sourcePath, destinationPath);
        return destinationPath;
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

        throw new AssertionException("Expected the tool call to fail.");
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

    private static void KillProcessAndRequireExit(int pid)
    {
        KillProcessIfRunning(pid);
        if (!SpinWait.SpinUntil(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(5)))
        {
            throw new AssertionException($"Process {pid} did not exit within the timeout.");
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
