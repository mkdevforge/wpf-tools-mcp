using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WindowProjectionTests
{
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    public void ResolveWindowVisibility_prefers_supported_provider_state(
        bool nativeIsVisible,
        bool providerIsOffscreen,
        bool expectedIsVisible)
    {
        var result = AutomationController.ResolveWindowVisibility(
            nativeIsVisible,
            () => providerIsOffscreen);

        Assert.That(result, Is.EqualTo(expectedIsVisible));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ResolveWindowVisibility_falls_back_to_native_state_when_provider_throws(
        bool nativeIsVisible)
    {
        var result = AutomationController.ResolveWindowVisibility(
            nativeIsVisible,
            () => throw new InvalidOperationException("UIA property is unavailable."));

        Assert.That(result, Is.EqualTo(nativeIsVisible));
    }

    [Test]
    public void ResolveWindowVisibility_tolerates_unsupported_provider_state()
    {
        var result = AutomationController.ResolveWindowVisibility(
            nativeIsVisible: null,
            () => throw new InvalidOperationException("UIA property is unavailable."));

        Assert.That(result, Is.True);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void ResolveWindowEnabled_prefers_supported_provider_state(
        bool nativeIsEnabled,
        bool providerIsEnabled)
    {
        var result = AutomationController.ResolveWindowEnabled(
            nativeIsEnabled,
            () => providerIsEnabled);

        Assert.That(result, Is.EqualTo(providerIsEnabled));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ResolveWindowEnabled_falls_back_to_native_state_when_provider_throws(
        bool nativeIsEnabled)
    {
        var result = AutomationController.ResolveWindowEnabled(
            nativeIsEnabled,
            () => throw new InvalidOperationException("UIA property is unavailable."));

        Assert.That(result, Is.EqualTo(nativeIsEnabled));
    }

    [Test]
    public void ResolveWindowEnabled_tolerates_unsupported_provider_state()
    {
        var result = AutomationController.ResolveWindowEnabled(
            nativeIsEnabled: null,
            () => throw new InvalidOperationException("UIA property is unavailable."));

        Assert.That(result, Is.False);
    }
}
