using System.Diagnostics;
using System.Threading;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class SessionManagerLifecycleTests
{
    [Test]
    public void Backend_capability_projection_reports_unavailable_process_state_for_both_backends()
    {
        var capabilities = SessionManager.ProjectBackendCapabilityStates(
            ProcessInstanceState.Unavailable,
            controllerAttached: true);

        Assert.That(capabilities, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            foreach (var capability in capabilities)
            {
                Assert.That(capability.State, Is.EqualTo("unavailable"));
                Assert.That(capability.Failure?.Code, Is.EqualTo("process_state_unavailable"));
                Assert.That(capability.Failure?.Stage, Is.EqualTo("target_shutdown"));
                Assert.That(capability.Failure?.Retryable, Is.True);
                Assert.That(capability.Failure?.RecoveryActions, Is.EqualTo(new[] { "retry" }));
            }
        });
    }

    [Test]
    public void PreCanceled_list_sessions_preserves_cancellation()
    {
        using var sessions = new SessionManager();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            _ = await sessions.ListSessionsAsync(cancellation.Token));
    }

    [Test]
    public async Task Detach_retires_session_before_releasing_resources()
    {
        using var sessions = new SessionManager();
        var markerPath = CreateMarkerPath();
        LaunchAppResponse? launch = null;
        Task<DetachSessionResponse>? detachTask = null;
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var retirementCancellation = new CancellationTokenSource();
        var sessionActive = false;

        try
        {
            launch = await LaunchLifecycleProbeAsync(sessions, markerPath);
            sessionActive = true;

            detachTask = sessions.DetachSessionAsync(
                launch.SessionId,
                async () =>
                {
                    releaseEntered.SetResult();
                    await releaseGate.Task;
                },
                retirementCancellation.Token);
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            retirementCancellation.Cancel();

            var getControllerException = Assert.Throws<InvalidOperationException>(() =>
                sessions.GetController(launch.SessionId));
            var registerCalled = false;
            var registerException = Assert.Throws<InvalidOperationException>(() =>
                sessions.RegisterSessionResource(
                    launch.SessionId,
                    () =>
                    {
                        registerCalled = true;
                        return new object();
                    }));
            var listed = await sessions.ListSessionsAsync();

            Assert.Multiple(() =>
            {
                Assert.That(getControllerException!.Message, Does.Contain("is ending"));
                Assert.That(registerException!.Message, Does.Contain("is ending"));
                Assert.That(registerCalled, Is.False);
                Assert.That(listed.Sessions, Has.None.Matches<SessionInfo>(session => session.SessionId == launch.SessionId));
                Assert.That(detachTask.IsCompleted, Is.False);
                Assert.That(IsProcessAlive(launch.Pid), Is.True);
            });

            releaseGate.SetResult();
            var detached = await detachTask.WaitAsync(TimeSpan.FromSeconds(2));
            sessionActive = false;

            Assert.Multiple(() =>
            {
                Assert.That(detached.SessionRemoved, Is.True);
                Assert.That(detached.ProcessWasRunning, Is.True);
                Assert.That(detached.ProcessWasRunningObserved, Is.True);
                Assert.That(detached.ProcessStillRunning, Is.True);
                Assert.That(detached.ProcessStillRunningObserved, Is.True);
                Assert.That(IsProcessAlive(launch.Pid), Is.True);
            });
        }
        finally
        {
            releaseGate.TrySetResult();
            if (detachTask is not null)
            {
                try
                {
                    _ = await detachTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }

            if (sessionActive && launch is not null)
            {
                await TerminateSessionBestEffortAsync(sessions, launch.SessionId);
            }

            if (launch is not null)
            {
                KillProcessIfRunning(launch.Pid);
            }

            DeleteFileBestEffort(markerPath);
        }
    }

    [Test]
    public async Task PreCanceled_detach_leaves_session_usable()
    {
        using var sessions = new SessionManager();
        var markerPath = CreateMarkerPath();
        LaunchAppResponse? launch = null;
        var sessionActive = false;

        try
        {
            launch = await LaunchLifecycleProbeAsync(sessions, markerPath);
            sessionActive = true;
            var releaseCalled = false;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                _ = await sessions.DetachSessionAsync(
                    launch.SessionId,
                    () =>
                    {
                        releaseCalled = true;
                        return Task.CompletedTask;
                    },
                    cancellation.Token));

            var (controller, _) = sessions.GetController(launch.SessionId);
            var registered = sessions.RegisterSessionResource(launch.SessionId, () => "registered");
            var windows = await controller.RunExclusiveAsync(
                () => controller.ListWindowsAsync(CancellationToken.None),
                CancellationToken.None);
            var listed = await sessions.ListSessionsAsync();

            Assert.Multiple(() =>
            {
                Assert.That(releaseCalled, Is.False);
                Assert.That(registered, Is.EqualTo("registered"));
                Assert.That(windows.Windows, Is.Not.Empty);
                Assert.That(listed.Sessions, Has.Some.Matches<SessionInfo>(session => session.SessionId == launch.SessionId));
                Assert.That(IsProcessAlive(launch.Pid), Is.True);
            });
        }
        finally
        {
            if (sessionActive && launch is not null)
            {
                await TerminateSessionBestEffortAsync(sessions, launch.SessionId);
                sessionActive = false;
            }

            if (launch is not null)
            {
                KillProcessIfRunning(launch.Pid);
            }

            DeleteFileBestEffort(markerPath);
        }
    }

    private static Task<LaunchAppResponse> LaunchLifecycleProbeAsync(SessionManager sessions, string markerPath)
    {
        var exePath = TestAppPaths.FindLifecycleProbeTestAppExecutable();
        return sessions.LaunchAppAsync(
            new LaunchAppRequest(
                ExePath: exePath,
                Args: ["--marker-path", markerPath],
                WorkingDirectory: Path.GetDirectoryName(exePath)),
            CancellationToken.None);
    }

    private static async Task TerminateSessionBestEffortAsync(SessionManager sessions, string sessionId)
    {
        try
        {
            _ = await sessions.TerminateApplicationAsync(sessionId, 2000, CancellationToken.None);
        }
        catch
        {
        }
    }

    private static string CreateMarkerPath() =>
        Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-session-manager-{Guid.NewGuid():N}.log");

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
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(2000);
            }
        }
        catch
        {
        }
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
}
