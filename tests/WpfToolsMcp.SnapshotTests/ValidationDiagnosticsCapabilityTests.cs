using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ValidationDiagnosticsCapabilityTests
{
    [Test]
    public void Current_agent_advertises_validation_diagnostics()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.GetValidationErrors));
    }

    [Test]
    public void Missing_capability_fails_before_the_agent_call()
    {
        var callInvoked = false;

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await AutomationController.CallGetValidationErrorsWhenSupportedAsync(
                new AgentCapabilitiesResponse(
                    AgentProtocolCapabilities.CurrentProtocolVersion,
                    Capabilities: []),
                () =>
                {
                    callInvoked = true;
                    return Task.FromResult("unexpected");
                }));

        Assert.Multiple(() =>
        {
            Assert.That(callInvoked, Is.False);
            Assert.That(
                exception!.Message,
                Is.EqualTo(
                    "agent_capability_unavailable: get_validation_errors requires the current WPF agent. " +
                    "Restart the target application, start a new MCP session, and attach again so the current agent can be injected."));
        });
    }

    [Test]
    public async Task Advertised_capability_allows_the_agent_call()
    {
        var capabilities = new AgentCapabilitiesResponse(
            AgentProtocolCapabilities.CurrentProtocolVersion,
            [AgentProtocolCapabilities.GetValidationErrors]);

        var result = await AutomationController.CallGetValidationErrorsWhenSupportedAsync(
            capabilities,
            () => Task.FromResult("sent"));

        Assert.That(result, Is.EqualTo("sent"));
    }
}
