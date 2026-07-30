using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public sealed class UiaCoverageAnalyzerTests
{
    [Test]
    public void Automation_id_does_not_satisfy_an_interactive_elements_accessible_name()
    {
        var element = new ElementRef(
            Type: "Button",
            AutomationId: "Accessibility_AutomationIdOnly",
            Name: null,
            XPath: "/Window/Button");

        var finding = WpfVisualTreeInspector.CreateMissingAccessibleNameFinding(
            element,
            isInteractive: true,
            isLikelyInteractive: true,
            peerName: null,
            automationPropertiesName: null);

        Assert.That(finding, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(finding!.IssueCode, Is.EqualTo("missing_accessible_name"));
            Assert.That(finding.Severity, Is.EqualTo("warning"));
            Assert.That(finding.Element, Is.SameAs(element));
            Assert.That(finding.Element.AutomationId, Is.EqualTo("Accessibility_AutomationIdOnly"));
            Assert.That(finding.Details, Does.Contain("automation_id_present"));
            Assert.That(
                finding.Suggestions,
                Does.Contain(
                    "Keep AutomationProperties.AutomationId as a stable automation identifier; " +
                    "it does not provide an accessible name."));
        });
    }

    [TestCase(false, null, null)]
    [TestCase(true, "Save", null)]
    [TestCase(true, null, "Save changes")]
    [TestCase(true, "Save", "Save changes")]
    public void Accessible_name_finding_requires_an_interactive_element_with_no_effective_name(
        bool isInteractive,
        string? peerName,
        string? automationPropertiesName)
    {
        var element = new ElementRef(
            Type: "Button",
            AutomationId: "Accessibility_Named",
            Name: null,
            XPath: "/Window/Button");

        var finding = WpfVisualTreeInspector.CreateMissingAccessibleNameFinding(
            element,
            isInteractive,
            isLikelyInteractive: true,
            peerName,
            automationPropertiesName);

        Assert.That(finding, Is.Null);
    }

    [Test]
    public void Missing_name_without_automation_id_keeps_the_existing_info_diagnostic()
    {
        var element = new ElementRef(
            Type: "CustomControl",
            AutomationId: null,
            Name: null,
            XPath: "/Window/CustomControl");

        var finding = WpfVisualTreeInspector.CreateMissingAccessibleNameFinding(
            element,
            isInteractive: true,
            isLikelyInteractive: false,
            peerName: null,
            automationPropertiesName: null);

        Assert.That(finding, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(finding!.Severity, Is.EqualTo("info"));
            Assert.That(finding.Details, Does.Contain("automation_id_empty"));
        });
    }
}
