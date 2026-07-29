using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class WindowProjectionTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void ResolveWindowVisibility_prefers_native_window_state(bool nativeIsVisible)
    {
        var providerWasRead = false;

        var result = AutomationController.ResolveWindowVisibility(
            nativeIsVisible,
            () =>
            {
                providerWasRead = true;
                throw new InvalidOperationException("UIA property is unavailable.");
            });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(nativeIsVisible));
            Assert.That(providerWasRead, Is.False);
        });
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void ResolveWindowVisibility_falls_back_to_provider_state(
        bool providerIsOffscreen,
        bool expectedIsVisible)
    {
        var result = AutomationController.ResolveWindowVisibility(
            nativeIsVisible: null,
            () => providerIsOffscreen);

        Assert.That(result, Is.EqualTo(expectedIsVisible));
    }

    [Test]
    public void ResolveWindowVisibility_tolerates_unsupported_provider_state()
    {
        var result = AutomationController.ResolveWindowVisibility(
            nativeIsVisible: null,
            () => throw new InvalidOperationException("UIA property is unavailable."));

        Assert.That(result, Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ResolveWindowEnabled_prefers_native_window_state(bool nativeIsEnabled)
    {
        var providerWasRead = false;

        var result = AutomationController.ResolveWindowEnabled(
            nativeIsEnabled,
            () =>
            {
                providerWasRead = true;
                throw new InvalidOperationException("UIA property is unavailable.");
            });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(nativeIsEnabled));
            Assert.That(providerWasRead, Is.False);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ResolveWindowEnabled_falls_back_to_provider_state(bool providerIsEnabled)
    {
        var result = AutomationController.ResolveWindowEnabled(
            nativeIsEnabled: null,
            () => providerIsEnabled);

        Assert.That(result, Is.EqualTo(providerIsEnabled));
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
