using System.Reflection;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class InteractiveDesktopTests
{
    private static readonly InteractiveDesktopObservation Ready = new(
        SessionId: 1,
        WindowStationName: "WinSta0",
        WindowStationVisible: true,
        SessionConnectState: "active",
        SessionLocked: false,
        ServerDesktopName: "Default",
        InputDesktopAccess: InputDesktopAccess.Opened,
        InputDesktopName: "Default",
        ScreensaverRunning: false,
        UnverifiedChecks: []);

    [Test]
    public void Ready_session_is_available_and_reports_its_observations()
    {
        var status = InteractiveDesktop.Evaluate(Ready);

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.True);
            Assert.That(status.UnavailableReason, Is.Null);
            Assert.That(status.Detail, Is.Null);
            Assert.That(status.SessionId, Is.EqualTo(1));
            Assert.That(status.SessionConnectState, Is.EqualTo("active"));
            Assert.That(status.SessionLocked, Is.False);
            Assert.That(status.WindowStation, Is.EqualTo("WinSta0"));
            Assert.That(status.ServerDesktop, Is.EqualTo("Default"));
            Assert.That(status.InputDesktop, Is.EqualTo("Default"));
            Assert.That(status.ScreensaverRunning, Is.False);
            Assert.That(status.UnverifiedChecks, Is.Null);
        });
    }

    private static IEnumerable<TestCaseData> UnavailableCases()
    {
        yield return new TestCaseData(Ready with { SessionId = 0 }, "service_session")
            .SetName("Session 0 is a service session");
        yield return new TestCaseData(Ready with { WindowStationName = "Service-0x0-3e7$" }, "non_interactive_window_station")
            .SetName("Non-interactive window station");
        yield return new TestCaseData(Ready with { WindowStationVisible = false }, "non_interactive_window_station")
            .SetName("Invisible window station");
        yield return new TestCaseData(Ready with { SessionConnectState = "disconnected" }, "session_disconnected")
            .SetName("Disconnected session");
        yield return new TestCaseData(Ready with { SessionConnectState = "connected" }, "session_not_active")
            .SetName("Connected but not active session");
        yield return new TestCaseData(Ready with { SessionLocked = true }, "session_locked")
            .SetName("Locked session");
        yield return new TestCaseData(
                Ready with { InputDesktopAccess = InputDesktopAccess.AccessDenied, InputDesktopName = null },
                "secure_desktop_active")
            .SetName("Secure desktop owns input");
        yield return new TestCaseData(Ready with { InputDesktopName = "QaHidden" }, "input_desktop_mismatch")
            .SetName("Input goes to another desktop");
        yield return new TestCaseData(Ready with { ScreensaverRunning = true }, "screensaver_running")
            .SetName("Screen saver running");
    }

    [TestCaseSource(nameof(UnavailableCases))]
    public void Positive_evidence_makes_the_desktop_unavailable(
        object observation,
        string expectedReason)
    {
        // The observation type is internal, so NUnit passes it as object.
        var status = InteractiveDesktop.Evaluate((InteractiveDesktopObservation)observation);

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.False);
            Assert.That(status.UnavailableReason, Is.EqualTo(expectedReason));
            Assert.That(status.Detail, Is.Not.Empty);
        });
    }

    [Test]
    public void Disconnected_session_reports_disconnection_rather_than_its_lock()
    {
        var status = InteractiveDesktop.Evaluate(Ready with
        {
            SessionConnectState = "disconnected",
            SessionLocked = true,
            InputDesktopAccess = InputDesktopAccess.AccessDenied
        });

        Assert.That(status.UnavailableReason, Is.EqualTo("session_disconnected"));
    }

    [Test]
    public void Unreadable_checks_are_reported_without_blocking()
    {
        var status = InteractiveDesktop.Evaluate(new InteractiveDesktopObservation(
            SessionId: 1,
            WindowStationName: null,
            WindowStationVisible: null,
            SessionConnectState: null,
            SessionLocked: null,
            ServerDesktopName: null,
            InputDesktopAccess: InputDesktopAccess.Unknown,
            InputDesktopName: null,
            ScreensaverRunning: null,
            UnverifiedChecks: ["window_station", "session_connect_state", "session_lock", "input_desktop"]));

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.True);
            Assert.That(status.UnverifiedChecks, Is.EqualTo(new[]
            {
                "window_station",
                "session_connect_state",
                "session_lock",
                "input_desktop"
            }));
        });
    }

    [Test]
    public void Unavailable_status_becomes_an_actionable_failure_with_reason_and_recovery()
    {
        var status = InteractiveDesktop.Evaluate(Ready with { SessionLocked = true });

        var exception = InteractiveDesktop.CreateUnavailableException("type_text", status);

        Assert.Multiple(() =>
        {
            Assert.That(exception.Failure.Code, Is.EqualTo("interactive_desktop_unavailable"));
            Assert.That(exception.Failure.Stage, Is.EqualTo("interactive_desktop"));
            Assert.That(exception.Failure.Detail, Does.StartWith("operation=type_text reason=session_locked."));
            Assert.That(exception.Failure.Retryable, Is.True);
            Assert.That(exception.Failure.RecoveryActions, Is.EqualTo(new[] { "unlock_session" }));
        });
    }

    [Test]
    public void Service_session_failure_is_not_retryable()
    {
        var status = InteractiveDesktop.Evaluate(Ready with { SessionId = 0 });

        var exception = InteractiveDesktop.CreateUnavailableException("screen_capture", status);

        Assert.Multiple(() =>
        {
            Assert.That(exception.Failure.Retryable, Is.False);
            Assert.That(exception.Failure.RecoveryActions, Is.EqualTo(new[] { "run_in_interactive_session" }));
        });
    }

    [Test]
    public void Available_status_cannot_become_a_failure()
    {
        var status = InteractiveDesktop.Evaluate(Ready);

        Assert.Throws<ArgumentException>(() => InteractiveDesktop.CreateUnavailableException("type_text", status));
    }

    [Test]
    public void Tool_error_envelope_carries_the_desktop_failure_unchanged()
    {
        var status = InteractiveDesktop.Evaluate(Ready with { SessionConnectState = "disconnected" });

        var error = MapException(InteractiveDesktop.CreateUnavailableException("drag", status));

        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("interactive_desktop_unavailable"));
            Assert.That(error.Stage, Is.EqualTo("interactive_desktop"));
            Assert.That(error.Detail, Does.StartWith("operation=drag reason=session_disconnected."));
            Assert.That(error.Retryable, Is.True);
            Assert.That(error.RecoveryActions, Is.EqualTo(new[] { "reconnect_session" }));
        });
    }

    [Test]
    public void Test_host_session_is_observed_as_available()
    {
        // The UI test suite needs an interactive desktop; this pins the live probe against it.
        var status = InteractiveDesktop.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.True, status.Detail);
            Assert.That(status.SessionId, Is.GreaterThan(0));
            Assert.That(status.WindowStation, Is.EqualTo("WinSta0").IgnoreCase);
            Assert.That(status.SessionConnectState, Is.EqualTo("active"));
            Assert.That(status.SessionLocked, Is.False);
            Assert.That(status.InputDesktop, Is.EqualTo(status.ServerDesktop).IgnoreCase);
            Assert.That(status.UnverifiedChecks, Is.Null);
        });
    }

    private static ToolErrorInfo MapException(Exception exception)
    {
        var serverAssembly = Assembly.LoadFrom(Path.ChangeExtension(
            McpServerPaths.FindMcpServerExecutable(),
            ".dll"));
        var filter = serverAssembly.GetType(
            "WpfToolsMcp.McpServer.Tools.McpToolErrorFilter",
            throwOnError: true)!;
        var method = filter.GetMethod("MapException", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ToolErrorInfo)method.Invoke(null, [exception, null])!;
    }
}
