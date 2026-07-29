using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ScreenshotCorrelationCapabilityTests
{
    [Test]
    public void Current_agent_capabilities_advertise_screenshot_correlation()
    {
        Assert.That(
            AgentProtocolCapabilities.Current,
            Does.Contain(AgentProtocolCapabilities.CorrelateScreenshotRegion));
    }

    [Test]
    public void Missing_correlation_capability_fails_before_the_agent_call()
    {
        var callInvoked = false;

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await AutomationController.CallCorrelateScreenshotWhenSupportedAsync(
                new AgentCapabilitiesResponse(ProtocolVersion: 1, Capabilities: []),
                () =>
                {
                    callInvoked = true;
                    return Task.FromResult("unexpected");
                }));

        Assert.Multiple(() =>
        {
            Assert.That(callInvoked, Is.False);
            Assert.That(exception!.Message, Does.Contain("Restart the target application"));
            Assert.That(exception.Message, Does.Contain("start a new MCP session"));
        });
    }

    [Test]
    public async Task Advertised_correlation_capability_allows_the_agent_call()
    {
        var capabilities = new AgentCapabilitiesResponse(
            ProtocolVersion: 1,
            Capabilities: [AgentProtocolCapabilities.CorrelateScreenshotRegion]);

        var result = await AutomationController.CallCorrelateScreenshotWhenSupportedAsync(
            capabilities,
            () => Task.FromResult("sent"));

        Assert.That(result, Is.EqualTo("sent"));
    }
}
