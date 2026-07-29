using System.Text.Json;
using NUnit.Framework;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ObserveStateContractTests
{
    [Test]
    public void Current_agent_capabilities_advertise_observe_state()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.ObserveState));
    }

    [Test]
    public void Existing_positional_request_construction_defaults_to_visible_elements()
    {
        var request = new ObserveStateStartRequest(
            null,
            null,
            null,
            null,
            null,
            5_000,
            30_000,
            256,
            512,
            false);

        Assert.That(request.VisibleOnly, Is.True);
    }

    [Test]
    public void Visible_only_can_be_disabled_over_the_agent_contract()
    {
        var request = new ObserveStateStartRequest(VisibleOnly: false);

        var json = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<ObserveStateStartRequest>(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"VisibleOnly\":false"));
            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.VisibleOnly, Is.False);
        });
    }

    [Test]
    public void Typed_waits_require_the_observe_state_capability()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AutomationController.EnsureObserveStateCapability(
                new AgentCapabilitiesResponse(
                    ProtocolVersion: 0,
                    Capabilities: [])));

        Assert.That(
            exception!.Message,
            Is.EqualTo(
                "agent_capability_unavailable: typed WPF waits require the current WPF agent. " +
                "Restart the target application, start a new MCP session, and attach again so the current agent can be injected."));
    }

    [Test]
    public void Typed_waits_accept_an_agent_that_advertises_observe_state()
    {
        var capabilities = new AgentCapabilitiesResponse(
            ProtocolVersion: AgentProtocolCapabilities.CurrentProtocolVersion,
            Capabilities: [AgentProtocolCapabilities.ObserveState]);

        Assert.DoesNotThrow(() => AutomationController.EnsureObserveStateCapability(capabilities));
    }
}
