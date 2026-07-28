using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DirectSearchProjectionTests
{
    private static readonly Rect Viewport = new(100, 100, 800, 600);

    [Test]
    public void ResolveUiaIsOffscreen_marks_geometry_outside_the_window_viewport()
    {
        var result = AutomationController.ResolveUiaIsOffscreen(
            providerIsOffscreen: false,
            bounds: new Rect(150, 900, 200, 30),
            viewportBounds: Viewport);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ResolveUiaIsOffscreen_marks_empty_bounds_offscreen()
    {
        var result = AutomationController.ResolveUiaIsOffscreen(
            providerIsOffscreen: false,
            bounds: new Rect(150, 150, 0, 0),
            viewportBounds: Viewport);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ResolveUiaIsOffscreen_preserves_provider_state_when_geometry_does_not_disprove_it()
    {
        var bounds = new Rect(150, 150, 200, 30);

        Assert.Multiple(() =>
        {
            Assert.That(
                AutomationController.ResolveUiaIsOffscreen(false, bounds, Viewport),
                Is.False);
            Assert.That(
                AutomationController.ResolveUiaIsOffscreen(true, bounds, Viewport),
                Is.True);
            Assert.That(
                AutomationController.ResolveUiaIsOffscreen(null, bounds, Viewport),
                Is.Null);
            Assert.That(
                AutomationController.ResolveUiaIsOffscreen(false, bounds, viewportBounds: null),
                Is.False);
        });
    }
}
