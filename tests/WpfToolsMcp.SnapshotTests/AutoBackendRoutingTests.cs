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

    [Test]
    public void Per_window_miss_does_not_poison_process_wide_agent_capability()
    {
        var perWindowMiss = new InvalidOperationException("wpf_window_not_found: HWND 42 is native.");
        var transportFailure = new InvalidOperationException("Agent connection closed.");

        Assert.Multiple(() =>
        {
            Assert.That(AutomationController.ShouldRecordAutoAgentFailure(perWindowMiss), Is.False);
            Assert.That(AutomationController.ShouldRecordAutoAgentFailure(transportFailure), Is.True);
        });
    }
}
