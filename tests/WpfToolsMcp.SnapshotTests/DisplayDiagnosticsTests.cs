using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DisplayDiagnosticsTests
{
    [Test]
    public void ClampBoundsToVirtualScreen_preserves_negative_virtual_coordinates()
    {
        var virtualScreen = new Rect(-1920, -200, 4480, 1440);
        var desired = new Rect(-2200, -500, 900, 700);

        var clamped = DisplayDiagnostics.ClampBoundsToVirtualScreen(desired, virtualScreen, out var wasClamped);

        Assert.That(wasClamped, Is.True);
        Assert.That(clamped, Is.EqualTo(new Rect(-1920, -200, 900, 700)));
    }

    [Test]
    public void ClampBoundsToVirtualScreen_caps_size_before_position()
    {
        var virtualScreen = new Rect(-1280, 0, 3200, 1080);
        var desired = new Rect(-5000, 2000, 5000, 2000);

        var clamped = DisplayDiagnostics.ClampBoundsToVirtualScreen(desired, virtualScreen, out var wasClamped);

        Assert.That(wasClamped, Is.True);
        Assert.That(clamped, Is.EqualTo(new Rect(-1280, 0, 3200, 1080)));
    }
}
