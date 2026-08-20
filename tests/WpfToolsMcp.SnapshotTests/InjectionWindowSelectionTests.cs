using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class InjectionWindowSelectionTests
{
    [Test]
    public void Explicit_injection_window_handle_selects_the_requested_window()
    {
        const long requestedHandle = 42;
        var mainWindowRequested = false;

        var selected = AutomationController.SelectInitialInjectionWindow(
            requestedHandle,
            handle => $"handle:{handle}",
            () =>
            {
                mainWindowRequested = true;
                return "main";
            });

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.EqualTo("handle:42"));
            Assert.That(mainWindowRequested, Is.False);
        });
    }

    [Test]
    public void Missing_injection_window_handle_preserves_the_main_window_fallback()
    {
        var explicitWindowRequested = false;

        var selected = AutomationController.SelectInitialInjectionWindow(
            initialWindowHandle: null,
            handle =>
            {
                explicitWindowRequested = true;
                return $"handle:{handle}";
            },
            () => "main");

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.EqualTo("main"));
            Assert.That(explicitWindowRequested, Is.False);
        });
    }
}
