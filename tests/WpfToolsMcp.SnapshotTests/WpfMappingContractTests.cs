using System.Text.Json;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WpfMappingContractTests
{
    [Test]
    public void Reverse_mapping_extension_preserves_the_five_value_response_shape()
    {
        var response = new GetUiaLocatorsResponse(null, null, null, null, null)
        {
            WpfMapping = AutomationController.CreateNonWpfWindowMappingDiagnostics("window_framework_not_wpf")
        };

        var (wpf, uia, suggestions, flaUi, uiaMapping) = response;

        Assert.Multiple(() =>
        {
            Assert.That(wpf, Is.Null);
            Assert.That(uia, Is.Null);
            Assert.That(suggestions, Is.Null);
            Assert.That(flaUi, Is.Null);
            Assert.That(uiaMapping, Is.Null);
            Assert.That(response.WpfMapping, Is.Not.Null);
        });
    }

    [Test]
    public void Current_agent_advertises_uia_to_wpf_mapping()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.MapUiaToWpf));
    }

    [Test]
    public void Known_non_wpf_window_is_a_complete_unmapped_result()
    {
        var mapping = AutomationController.CreateNonWpfWindowMappingDiagnostics("window_framework_not_wpf");

        Assert.Multiple(() =>
        {
            Assert.That(mapping.Available, Is.True);
            Assert.That(mapping.Method, Is.EqualTo("frameworkClassification"));
            Assert.That(mapping.Status, Is.EqualTo(ElementMappingStatus.Unmapped));
            Assert.That(mapping.ScanComplete, Is.True);
            Assert.That(mapping.Failure, Is.Null);
            Assert.That(mapping.Evidence, Does.Contain("window_framework_not_wpf"));
        });
    }

    [Test]
    public void Unavailable_wpf_mapping_keeps_structured_failure_separate_from_uia_output()
    {
        var failure = new FailureInfo("agent_capability_unavailable", "protocol", "Update the WPF agent.");
        var response = new GetUiaLocatorsResponse(
            Wpf: null,
            Uia: new UiaLocatorIdentity(
                "Button",
                "SaveButton",
                "Save",
                "Button",
                "/Window/Button",
                new Rect(1, 2, 3, 4),
                IsEnabled: true,
                IsOffscreen: false),
            LocatorSuggestions: null,
            FlaUi: null,
            UiaMapping: null)
        {
            WpfMapping = AutomationController.CreateUnavailableWpfMappingDiagnostics(failure)
        };

        var json = JsonSerializer.Serialize(response);

        Assert.Multiple(() =>
        {
            Assert.That(response.Uia, Is.Not.Null);
            Assert.That(response.WpfMapping!.Available, Is.False);
            Assert.That(response.WpfMapping.Status, Is.Null);
            Assert.That(response.WpfMapping.Failure, Is.SameAs(failure));
            Assert.That(json, Does.Contain("\"WpfMapping\""));
            Assert.That(json, Does.Contain("\"Available\":false"));
            Assert.That(json, Does.Contain("\"Uia\""));
        });
    }
}
