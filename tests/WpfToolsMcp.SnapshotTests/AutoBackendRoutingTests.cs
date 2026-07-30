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
            "private target stack");

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
    public void Sanitized_agent_errors_retain_internal_auto_routing_semantics()
    {
        var xpathMiss = new AgentRemoteException(
            "wpf/get_visual_tree",
            "XPath segment not found for '/Window/Grid[2]'.",
            "private target stack");
        var locatorMiss = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:not_found: Locator did not match any elements.",
            "private target stack");
        var ambiguity = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:ambiguous: Locator matched multiple elements.",
            "private target stack");

        Assert.Multiple(() =>
        {
            Assert.That(AutomationController.IsWaitableWpfXPathNotFound(xpathMiss), Is.True);
            Assert.That(AutomationController.IsEligibleAutoScreenshotFallback(locatorMiss), Is.True);
            Assert.That(AutomationController.IsEligibleAutoScreenshotFallback(ambiguity), Is.False);
            Assert.That(xpathMiss.Message, Is.EqualTo("Agent call failed."));
            Assert.That(locatorMiss.Message, Is.EqualTo("Agent call failed."));
        });
    }

    [Test]
    public void Auto_resolve_falls_back_for_backend_health_failures_but_not_locator_semantics()
    {
        var requestFailure = new AgentRemoteException(
            "wpf/resolve_element",
            "The backend rejected this operation.",
            "private target stack");
        var locatorMiss = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:not_found: Locator did not match any elements.",
            "private target stack");
        var ambiguity = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:ambiguous: Locator matched multiple elements.",
            "private target stack");

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
    public void Auto_WPF_ambiguity_uses_a_structured_error_without_remote_diagnostics()
    {
        const long windowHandle = 42;
        const string privatePath = @"C:\Users\customer\private-workspace\MainWindow.xaml";
        var remoteFailure = new AgentRemoteException(
            "wpf/resolve_element",
            $"wpf_resolve:ambiguous: Locator is ambiguous (found 4). Source: {privatePath}",
            $"at CustomerApp.Resolve() in {privatePath}:line 87");

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
            Assert.That(exception.Message, Does.Not.Contain(privatePath));
            Assert.That(exception.Message, Does.Not.Contain("CustomerApp.Resolve"));
            Assert.That(exception.Message, Does.Contain("Retry with a stricter locator"));
        });
    }
}
