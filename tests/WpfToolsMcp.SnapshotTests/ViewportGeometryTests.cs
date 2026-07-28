using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ViewportGeometryTests
{
    [TestCase(100d, 96u, 100)]
    [TestCase(100d, 120u, 125)]
    [TestCase(99d, 144u, 149)]
    [TestCase(63.75d, 192u, 128)]
    public void DipsToPhysicalPixels_uses_target_dpi_and_away_from_zero_rounding(
        double dips,
        uint dpi,
        int expected)
    {
        var actual = ViewportGeometryCalculator.DipsToPhysicalPixels(dips, dpi);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(96, 96u, 96d)]
    [TestCase(125, 120u, 100d)]
    [TestCase(149, 144u, 99.33333333333333d)]
    [TestCase(256, 192u, 128d)]
    public void PhysicalPixelsToDips_preserves_fractional_logical_size(
        int pixels,
        uint dpi,
        double expected)
    {
        var actual = ViewportGeometryCalculator.PhysicalPixelsToDips(pixels, dpi);

        Assert.That(actual, Is.EqualTo(expected).Within(0.000_001d));
    }

    [TestCase(500d, 96u, 192u, 500, 1000)]
    [TestCase(500d, 120u, 192u, 625, 1000)]
    [TestCase(500d, 192u, 192u, 1000, 1000)]
    public void Wpf_dips_are_scaled_through_window_and_monitor_dpi(
        double dips,
        uint windowDpi,
        uint monitorDpi,
        int expectedTargetLogicalPixels,
        int expectedMonitorPhysicalPixels)
    {
        var targetLogicalPixels = ViewportGeometryCalculator.DipsToPhysicalPixels(dips, windowDpi);
        var monitorPhysicalPixels = ViewportGeometryCalculator.ScalePixelsBetweenDpi(
            targetLogicalPixels,
            windowDpi,
            monitorDpi);

        Assert.Multiple(() =>
        {
            Assert.That(targetLogicalPixels, Is.EqualTo(expectedTargetLogicalPixels));
            Assert.That(monitorPhysicalPixels, Is.EqualTo(expectedMonitorPhysicalPixels));
        });
    }

    [Test]
    public void Dpi_conversion_rejects_invalid_dimensions_and_dpi()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ViewportGeometryCalculator.DipsToPhysicalPixels(0, 96));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ViewportGeometryCalculator.DipsToPhysicalPixels(double.NaN, 96));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ViewportGeometryCalculator.DipsToPhysicalPixels(100, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ViewportGeometryCalculator.PhysicalPixelsToDips(-1, 96));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ViewportGeometryCalculator.PhysicalPixelsToDips(100, 0));
        });
    }

    [Test]
    public void ExpandClientToOuter_adds_asymmetric_non_client_frame()
    {
        var frame = new ViewportFrameInsets(Left: 8, Top: 31, Right: 8, Bottom: 8);

        var actual = ViewportGeometryCalculator.ExpandClientToOuter(640, 480, frame);

        Assert.That(actual, Is.EqualTo(new ViewportSize(656, 519)));
    }

    [TestCase(656, 519, 640, 480, 638, 477, 658, 522)]
    [TestCase(656, 519, 640, 480, 642, 481, 654, 518)]
    [TestCase(656, 519, 640, 480, 640, 480, 656, 519)]
    public void CorrectOuterSize_applies_the_measured_client_delta(
        int outerWidth,
        int outerHeight,
        int targetClientWidth,
        int targetClientHeight,
        int actualClientWidth,
        int actualClientHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var actual = ViewportGeometryCalculator.CorrectOuterSize(
            outerWidth,
            outerHeight,
            targetClientWidth,
            targetClientHeight,
            actualClientWidth,
            actualClientHeight);

        Assert.That(actual, Is.EqualTo(new ViewportSize(expectedWidth, expectedHeight)));
    }

    [Test]
    public void ClampOuterPosition_preserves_negative_monitor_coordinates()
    {
        var workArea = new Rect(-1920, -200, 4480, 1440);
        var desired = new Rect(-2200, -500, 900, 700);

        var actual = ViewportGeometryCalculator.ClampOuterPosition(desired, workArea);

        Assert.That(actual, Is.EqualTo(new Rect(-1920, -200, 900, 700)));
    }

    [Test]
    public void ClampOuterPosition_anchors_an_oversized_window_to_the_work_area_origin()
    {
        var workArea = new Rect(-1280, 0, 3200, 1080);
        var desired = new Rect(500, 700, 5000, 2000);

        var actual = ViewportGeometryCalculator.ClampOuterPosition(desired, workArea);

        Assert.That(actual, Is.EqualTo(new Rect(-1280, 0, 5000, 2000)));
    }

    [Test]
    public void ClampOuterPosition_leaves_an_in_bounds_window_unchanged()
    {
        var workArea = new Rect(-1280, 0, 3200, 1080);
        var desired = new Rect(-640, 120, 800, 600);

        var actual = ViewportGeometryCalculator.ClampOuterPosition(desired, workArea);

        Assert.That(actual, Is.EqualTo(desired));
    }

    [Test]
    public void ClampClientSizeToWorkArea_accounts_for_non_client_frame()
    {
        var target = new PixelDimensions(1920, 1080);
        var frame = new ViewportFrameInsets(8, 31, 8, 8);
        var workArea = new Rect(0, 0, 1920, 1040);

        var actual = ViewportGeometryCalculator.ClampClientSizeToWorkArea(
            target,
            frame,
            workArea,
            out var wasClamped);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(new PixelDimensions(1904, 1001)));
            Assert.That(wasClamped, Is.True);
        });
    }

    [Test]
    public void OuterSizeExceedsWorkArea_detects_a_known_minimum_conflict()
    {
        var workArea = new Rect(-1280, 0, 1280, 720);

        Assert.Multiple(() =>
        {
            Assert.That(
                ViewportGeometryCalculator.OuterSizeExceedsWorkArea(
                    new Rect(-1280, 0, 1400, 700),
                    workArea),
                Is.True);
            Assert.That(
                ViewportGeometryCalculator.OuterSizeExceedsWorkArea(
                    new Rect(-1280, 0, 1200, 700),
                    workArea),
                Is.False);
        });
    }
}
