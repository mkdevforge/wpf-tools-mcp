using FlaUI.Core;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public sealed class AutoBackendRoutingTests
{
    [TestCase(FrameworkType.Wpf, "Wpf")]
    [TestCase(FrameworkType.Win32, "Uia")]
    [TestCase(FrameworkType.WinForms, "Uia")]
    [TestCase(FrameworkType.Xaml, "Uia")]
    [TestCase(FrameworkType.Qt, "Uia")]
    [TestCase(FrameworkType.None, "ProbeWpfThenUia")]
    [TestCase(FrameworkType.Unknown, "ProbeWpfThenUia")]
    public void ClassifyAutoBackendRoute_uses_framework_specific_policy(
        FrameworkType frameworkType,
        string expected)
    {
        Assert.That(AutomationController.ClassifyAutoBackendRoute(frameworkType).ToString(), Is.EqualTo(expected));
    }

    [TestCase("Wpf", true, InspectionBackend.Wpf)]
    [TestCase("Wpf", false, InspectionBackend.Uia)]
    [TestCase("Uia", true, InspectionBackend.Uia)]
    [TestCase("Uia", false, InspectionBackend.Uia)]
    [TestCase("ProbeWpfThenUia", true, InspectionBackend.Wpf)]
    [TestCase("ProbeWpfThenUia", false, InspectionBackend.Uia)]
    public void SelectAutoBackend_only_uses_WPF_when_the_route_allows_it(
        string route,
        bool wpfBackendAvailable,
        InspectionBackend expected)
    {
        var parsedRoute = Enum.Parse<AutomationController.AutoBackendRoute>(route);
        Assert.That(
            AutomationController.SelectAutoBackend(parsedRoute, wpfBackendAvailable),
            Is.EqualTo(expected));
    }

    [Test]
    public void IsPerWindowAutoWpfMiss_recognizes_agent_window_miss_through_wrapping()
    {
        var error = new InvalidOperationException(
            "outer",
            new InvalidOperationException("wpf_window_not_found: HWND 42 is not owned by a WPF HwndSource."));

        Assert.That(AutomationController.IsPerWindowAutoWpfMiss(error), Is.True);
    }

    [TestCase("wpf_resolve:not_found: Locator did not match any elements.")]
    [TestCase("Agent connection closed.")]
    [TestCase("")]
    public void IsPerWindowAutoWpfMiss_does_not_hide_other_failures(string message)
    {
        Assert.That(
            AutomationController.IsPerWindowAutoWpfMiss(new InvalidOperationException(message)),
            Is.False);
    }

    [TestCase("wpf_window_not_found: HWND 42 is native.")]
    [TestCase("wpf_resolve:not_found: Locator did not match any elements.")]
    [TestCase("wpf_resolve:ambiguous: Locator is ambiguous (found 3).")]
    public void Auto_WPF_scope_misses_are_distinct_from_backend_failures(string message)
    {
        var exception = new AgentRemoteException(
            "wpf/resolve_element",
            message,
            "target diagnostic stack");

        Assert.Multiple(() =>
        {
            Assert.That(AutomationController.IsAutoWpfScopeMiss(exception), Is.True);
            Assert.That(
                AutomationController.ShouldRecordAutoAgentFailure(
                    exception,
                    agentConnectionHealthy: false),
                Is.False);
        });
    }

    [Test]
    public void Per_window_miss_does_not_poison_process_wide_agent_capability()
    {
        var perWindowMiss = new InvalidOperationException("wpf_window_not_found: HWND 42 is native.");
        var transportFailure = new InvalidOperationException("Agent connection closed.");
        var requestFailure = new AgentRemoteException(
            "wpf/get_visual_tree",
            "The requested element was unavailable.",
            "target-side diagnostic details");

        Assert.Multiple(() =>
        {
            Assert.That(
                AutomationController.ShouldRecordAutoAgentFailure(
                    perWindowMiss,
                    agentConnectionHealthy: false),
                Is.False);
            Assert.That(
                AutomationController.ShouldRecordAutoAgentFailure(
                    transportFailure,
                    agentConnectionHealthy: false),
                Is.True);
            Assert.That(
                AutomationController.ShouldRecordAutoAgentFailure(
                    requestFailure,
                    agentConnectionHealthy: true),
                Is.False);
        });
    }

    [Test]
    public void Agent_errors_retain_diagnostics_and_auto_routing_semantics()
    {
        var xpathMiss = new AgentRemoteException(
            "wpf/get_visual_tree",
            "XPath segment not found for '/Window/Grid[2]'.",
            "target diagnostic stack");
        var locatorMiss = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:not_found: Locator did not match any elements.",
            "target diagnostic stack");
        var ambiguity = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:ambiguous: Locator matched multiple elements.",
            "target diagnostic stack");

        Assert.Multiple(() =>
        {
            Assert.That(AutomationController.IsWaitableWpfXPathNotFound(xpathMiss), Is.True);
            Assert.That(AutomationController.IsEligibleAutoScreenshotFallback(locatorMiss), Is.True);
            Assert.That(AutomationController.IsEligibleAutoScreenshotFallback(ambiguity), Is.False);
            Assert.That(xpathMiss.Message, Does.Contain("Grid[2]"));
            Assert.That(locatorMiss.Message, Does.StartWith("wpf_resolve:not_found"));
        });
    }

    [Test]
    public void Auto_WPF_fallback_metadata_retains_bounded_remote_causes()
    {
        var operationFailure = new AgentRemoteException(
            "wpf/get_visual_tree",
            new string('m', 1_200),
            new string('d', 5_000));
        var scopeFailure = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:not_found: Locator did not match any elements.",
            "target-side locator details");

        var operation = AutomationController.CreateAutoWpfFallbackFailure(operationFailure);
        var scope = AutomationController.CreateAutoWpfFallbackFailure(scopeFailure);

        Assert.Multiple(() =>
        {
            Assert.That(operation.Code, Is.EqualTo(FailureDiagnostics.Codes.BackendOperationFailed));
            Assert.That(operation.Cause!.Type, Is.EqualTo(typeof(AgentRemoteException).FullName));
            Assert.That(operation.Cause.Message, Has.Length.EqualTo(1_024));
            Assert.That(operation.Cause.Details, Has.Length.EqualTo(4_096));
            Assert.That(scope.Code, Is.EqualTo(FailureDiagnostics.Codes.BackendScopeUnavailable));
            Assert.That(scope.Cause!.Message, Does.StartWith("wpf_resolve:not_found:"));
            Assert.That(scope.Cause.Details, Is.EqualTo("target-side locator details"));
        });
    }

    [Test]
    public void Stored_auto_agent_failure_preserves_cause_in_backend_capability_state()
    {
        using var controller = new AutomationController();
        controller.SetAutoAgentFailure(
            new IOException("local pipe diagnostic"),
            FailureDiagnostics.Stages.PipeConnection);

        var capability = controller.GetWpfBackendCapabilityState();

        Assert.Multiple(() =>
        {
            Assert.That(capability.State, Is.EqualTo("unavailable"));
            Assert.That(capability.Failure!.Code, Is.EqualTo(FailureDiagnostics.Codes.AgentConnectionFailed));
            Assert.That(capability.Failure.Cause!.Type, Is.EqualTo(typeof(IOException).FullName));
            Assert.That(capability.Failure.Cause.Message, Is.EqualTo("local pipe diagnostic"));
        });
    }

    [Test]
    public void Internal_failure_message_tolerates_a_throwing_message_getter()
    {
        var throwingMessage = new ThrowingMessageException();

        Assert.Multiple(() =>
        {
            Assert.That(AutomationController.GetInternalFailureMessage(throwingMessage), Is.Empty);
            Assert.That(AutomationController.IsAutoWpfScopeMiss(throwingMessage), Is.False);
            Assert.That(throwingMessage.GetterCalls, Is.EqualTo(2));
        });
    }

    [Test]
    public void Auto_resolve_falls_back_for_backend_health_failures_but_not_locator_semantics()
    {
        var requestFailure = new AgentRemoteException(
            "wpf/resolve_element",
            "The backend rejected this operation.",
            "target diagnostic stack");
        var locatorMiss = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:not_found: Locator did not match any elements.",
            "target diagnostic stack");
        var ambiguity = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:ambiguous: Locator matched multiple elements.",
            "target diagnostic stack");

        Assert.Multiple(() =>
        {
            Assert.That(
                AutomationController.ShouldFallbackFromAutoWpfResolveFailure(
                    requestFailure,
                    agentConnectionHealthy: true),
                Is.True);
            Assert.That(
                AutomationController.ShouldFallbackFromAutoWpfResolveFailure(
                    new TimeoutException(),
                    agentConnectionHealthy: false),
                Is.True);
            Assert.That(
                AutomationController.ShouldFallbackFromAutoWpfResolveFailure(
                    new TimeoutException(),
                    agentConnectionHealthy: true),
                Is.True);
            Assert.That(
                AutomationController.ShouldFallbackFromAutoWpfResolveFailure(
                    locatorMiss,
                    agentConnectionHealthy: true),
                Is.False);
            Assert.That(
                AutomationController.ShouldFallbackFromAutoWpfResolveFailure(
                    ambiguity,
                    agentConnectionHealthy: true),
                Is.False);
        });
    }

    [Test]
    public void Auto_WPF_ambiguity_keeps_structured_metadata_and_remote_diagnostics()
    {
        const long windowHandle = 42;
        const string diagnosticPath = @"C:\Users\example\workspace\MainWindow.xaml";
        var remoteFailure = new AgentRemoteException(
            "wpf/resolve_element",
            $"wpf_resolve:ambiguous: Locator is ambiguous (found 4). Source: {diagnosticPath}",
            $"at CustomerApp.Resolve() in {diagnosticPath}:line 87");

        var exception = AutomationController.CreateLegacyWpfAmbiguityException(
            remoteFailure,
            windowHandle);

        Assert.Multiple(() =>
        {
            Assert.That(exception.Ambiguity.Code, Is.EqualTo("ambiguous_element"));
            Assert.That(exception.Ambiguity.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            Assert.That(exception.Ambiguity.WindowHandleUsed, Is.EqualTo(windowHandle));
            Assert.That(exception.Ambiguity.DiscoveredCandidates, Is.EqualTo(4));
            Assert.That(exception.Ambiguity.Candidates, Is.Empty);
            Assert.That(exception.Ambiguity.TruncatedReason, Is.EqualTo("legacyAgent"));
            Assert.That(exception.Message, Does.Not.Contain(diagnosticPath));
            Assert.That(exception.Message, Does.Not.Contain("CustomerApp.Resolve"));
            Assert.That(exception.Message, Does.Contain("Retry with a stricter locator"));
            Assert.That(exception.InnerException, Is.SameAs(remoteFailure));
            Assert.That(exception.InnerException!.Message, Does.Contain(diagnosticPath));
            Assert.That(((AgentRemoteException)exception.InnerException).RemoteDetails, Does.Contain("CustomerApp.Resolve"));
        });
    }

    private sealed class ThrowingMessageException : Exception
    {
        public int GetterCalls { get; private set; }

        public override string Message
        {
            get
            {
                GetterCalls++;
                throw new InvalidOperationException("application message getter failed");
            }
        }
    }
}
