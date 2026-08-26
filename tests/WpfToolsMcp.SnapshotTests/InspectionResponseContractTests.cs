using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class InspectionResponseContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Screenshot_backend_metadata_is_omitted_when_no_element_backend_was_used()
    {
        var response = AutomationController.WithScreenshotRoutingMetadata(
            CreateScreenshotResponse(),
            hasElementTarget: false,
            backendUsed: InspectionBackend.Wpf,
            fallback: new BackendFallbackInfo(
                FromBackend: "uia",
                ToBackend: "wpf",
                Attempted: true,
                Available: true,
                Used: true));

        var json = JsonSerializer.SerializeToNode(response, JsonOptions)!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json.ContainsKey("windowHandleUsed"), Is.True);
            Assert.That(json.ContainsKey("backendUsed"), Is.False);
            Assert.That(json.ContainsKey("fallback"), Is.False);
        });
    }

    [Test]
    public void Screenshot_backend_metadata_reports_the_backend_and_actual_fallback()
    {
        var response = AutomationController.WithScreenshotRoutingMetadata(
            CreateScreenshotResponse(),
            hasElementTarget: true,
            backendUsed: InspectionBackend.Uia,
            fallback: new BackendFallbackInfo(
                FromBackend: "wpf",
                ToBackend: "uia",
                Attempted: true,
                Available: true,
                Used: true));

        var json = JsonSerializer.SerializeToNode(response, JsonOptions)!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(json["backendUsed"]!.GetValue<string>(), Is.EqualTo("uia").IgnoreCase);
            Assert.That(json["fallback"]!["fromBackend"]!.GetValue<string>(), Is.EqualTo("wpf"));
            Assert.That(json["fallback"]!["toBackend"]!.GetValue<string>(), Is.EqualTo("uia"));
            Assert.That(json["fallback"]!["used"]!.GetValue<bool>(), Is.True);
        });
    }

    [Test]
    public void Screenshot_auto_routing_records_unavailable_wpf_backend_before_uia_capture()
    {
        var failure = new FailureInfo(
            Code: "injection_failed",
            Stage: "injection",
            Detail: "The WPF backend could not be initialized.");

        var unavailable = AutomationController.SelectAutoScreenshotLocatorBackend(
            wpfBackendAvailable: false,
            wpfAttempted: true,
            failure);
        var available = AutomationController.SelectAutoScreenshotLocatorBackend(
            wpfBackendAvailable: true,
            wpfAttempted: false,
            failure: null);

        Assert.Multiple(() =>
        {
            Assert.That(unavailable.BackendUsed, Is.EqualTo(InspectionBackend.Uia));
            Assert.That(unavailable.Fallback, Is.Not.Null);
            Assert.That(unavailable.Fallback!.FromBackend, Is.EqualTo("wpf"));
            Assert.That(unavailable.Fallback.ToBackend, Is.EqualTo("uia"));
            Assert.That(unavailable.Fallback.Attempted, Is.True);
            Assert.That(unavailable.Fallback.Used, Is.True);
            Assert.That(unavailable.Fallback.Failure, Is.SameAs(failure));
            Assert.That(available.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
            Assert.That(available.Fallback, Is.Null);
        });
    }

    [Test]
    public void Inspection_metadata_requires_an_agent_that_advertises_the_wire_shape()
    {
        var previousAgent = new AgentCapabilitiesResponse(
            AgentProtocolCapabilities.CurrentProtocolVersion,
            []);
        var currentAgent = new AgentCapabilitiesResponse(
            AgentProtocolCapabilities.CurrentProtocolVersion,
            [AgentProtocolCapabilities.InspectionResponseMetadata]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AutomationController.EnsureInspectionResponseMetadataCapability(previousAgent));

        Assert.Multiple(() =>
        {
            Assert.That(
                AgentProtocolCapabilities.Current,
                Does.Contain(AgentProtocolCapabilities.InspectionResponseMetadata));
            Assert.That(exception!.Message, Does.StartWith("agent_capability_unavailable:"));
            Assert.DoesNotThrow(
                () => AutomationController.EnsureInspectionResponseMetadataCapability(currentAgent));
        });
    }

    [Test]
    public void Bounded_inspection_features_require_explicit_agent_capabilities()
    {
        var previousAgent = new AgentCapabilitiesResponse(
            AgentProtocolCapabilities.CurrentProtocolVersion,
            [AgentProtocolCapabilities.InspectionResponseMetadata]);
        var currentAgent = new AgentCapabilitiesResponse(
            AgentProtocolCapabilities.CurrentProtocolVersion,
            AgentProtocolCapabilities.Current);

        var batchException = Assert.Throws<InvalidOperationException>(
            () => AutomationController.EnsureComputedPropertiesBatchCapability(previousAgent));
        var pathException = Assert.Throws<InvalidOperationException>(
            () => AutomationController.EnsureDataContextPropertyPathsCapability(previousAgent));

        Assert.Multiple(() =>
        {
            Assert.That(batchException!.Message, Does.StartWith("agent_capability_unavailable:"));
            Assert.That(pathException!.Message, Does.StartWith("agent_capability_unavailable:"));
            Assert.DoesNotThrow(
                () => AutomationController.EnsureComputedPropertiesBatchCapability(currentAgent));
            Assert.DoesNotThrow(
                () => AutomationController.EnsureDataContextPropertyPathsCapability(currentAgent));
        });
    }

    private static TakeScreenshotResponse CreateScreenshotResponse() =>
        new(
            Path: "capture.png",
            Width: 10,
            Height: 10,
            Format: "png",
            CapturedBounds: new Rect(0, 0, 10, 10),
            RequestedBounds: null,
            WasClipped: false,
            WindowHandleUsed: 123,
            CaptureModeUsed: ScreenshotCaptureMode.Screen);
}
