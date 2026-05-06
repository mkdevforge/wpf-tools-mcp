using ModelContextProtocol;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Tools;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class McpToolErrorsDesignTests
{
    [Test]
    public void RunAsync_prefers_typed_invalid_request_code()
    {
        var ex = Assert.ThrowsAsync<McpException>(
            async () => await McpToolErrors.RunAsync<string>(
                () => throw new AgentCallException("Provide exactly one target.", AgentErrorCodes.InvalidRequest, null),
                "click_element"));

        Assert.That(ex?.Message, Does.Contain("tool=click_element: invalid_request: Provide exactly one target."));
    }

    [Test]
    public void RunAsync_prefers_typed_stale_agent_code()
    {
        var ex = Assert.ThrowsAsync<McpException>(
            async () => await McpToolErrors.RunAsync<string>(
                () => throw new AgentCallException("Element no longer resolves.", AgentErrorCodes.WpfHandleStale, null),
                "get_element_properties"));

        Assert.That(ex?.Message, Does.Contain("tool=get_element_properties: wpf_handle_stale: Element no longer resolves."));
    }

    [Test]
    public void RunAsync_retains_legacy_prefix_fallback_for_unmigrated_errors()
    {
        var ex = Assert.ThrowsAsync<McpException>(
            async () => await McpToolErrors.RunAsync<string>(
                () => throw new InvalidOperationException("timeout: element not found before deadline."),
                "wait_for"));

        Assert.That(ex?.Message, Does.Contain("tool=wait_for: timeout: element not found before deadline."));
    }

    [Test]
    public void ClickElement_rejects_missing_target_before_session_lookup()
    {
        using var sessions = new SessionManager();

        var ex = Assert.ThrowsAsync<McpException>(
            async () => await InteractionTools.ClickElement(sessions, "missing-session"));

        Assert.That(ex?.Message, Does.Contain("invalid_request: click_element requires exactly one of locator OR elementId."));
        Assert.That(ex?.Message, Does.Not.Contain("Unknown sessionId"));
    }

    [Test]
    public void ClickElement_rejects_mixed_target_before_session_lookup()
    {
        using var sessions = new SessionManager();

        var ex = Assert.ThrowsAsync<McpException>(
            async () => await InteractionTools.ClickElement(
                sessions,
                "missing-session",
                locator: new ElementLocator(AutomationId: "Basic_Button"),
                elementId: "uia_1"));

        Assert.That(ex?.Message, Does.Contain("invalid_request: click_element requires exactly one of locator OR elementId."));
        Assert.That(ex?.Message, Does.Not.Contain("Unknown sessionId"));
    }

    [Test]
    public void SetValue_rejects_missing_target_before_session_lookup()
    {
        using var sessions = new SessionManager();

        var ex = Assert.ThrowsAsync<McpException>(
            async () => await InteractionTools.SetValue(
                sessions,
                "missing-session",
                text: "hello"));

        Assert.That(ex?.Message, Does.Contain("invalid_request: set_value requires exactly one of locator OR elementId."));
        Assert.That(ex?.Message, Does.Not.Contain("Unknown sessionId"));
    }
}
