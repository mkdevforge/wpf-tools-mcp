using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WpfAgentErrorClassifierTests
{
    [Test]
    public void Stale_or_not_found_uses_typed_codes_without_message_prefixes()
    {
        Assert.That(
            WpfAgentErrorClassifier.IsStaleOrNotFound(
                new AgentCallException("Element no longer resolves.", AgentErrorCodes.WpfHandleStale, null)),
            Is.True);

        Assert.That(
            WpfAgentErrorClassifier.IsStaleOrNotFound(
                new AgentCallException("Locator did not match any elements.", AgentErrorCodes.WpfResolveNotFound, null)),
            Is.True);
    }

    [Test]
    public void Resolve_helpers_use_typed_codes_without_message_prefixes()
    {
        var notFound = new AgentCallException("Locator did not match any elements.", AgentErrorCodes.WpfResolveNotFound, null);
        var ambiguous = new AgentCallException("Locator is ambiguous.", AgentErrorCodes.WpfResolveAmbiguous, null);

        Assert.That(WpfAgentErrorClassifier.IsResolveNotFound(notFound), Is.True);
        Assert.That(WpfAgentErrorClassifier.IsResolveAmbiguous(ambiguous), Is.True);
        Assert.That(WpfAgentErrorClassifier.CleanLegacyPrefix(ambiguous, "wpf_resolve:ambiguous:"), Is.EqualTo("Locator is ambiguous."));
    }

    [Test]
    public void Legacy_prefix_fallback_remains_narrow_for_unmigrated_errors()
    {
        Assert.That(
            WpfAgentErrorClassifier.IsResolveNotFound(
                new InvalidOperationException("wpf_resolve:not_found: Locator did not match any elements.")),
            Is.True);

        Assert.That(
            WpfAgentErrorClassifier.IsLegacyTimeoutElementNotFound(
                new InvalidOperationException("timeout: element not found before deadline.")),
            Is.True);
    }
}
