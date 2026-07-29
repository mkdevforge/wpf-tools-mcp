using System.Text.Json;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ScreenshotCorrelationGeometryTests
{
    [Test]
    public void Image_region_maps_to_a_capture_on_a_negative_screen_origin()
    {
        var mapped = ScreenshotCorrelationGeometry.MapImageRegionToScreen(
            new Rect(320, 180, 160, 90),
            imageWidth: 1280,
            imageHeight: 720,
            capturedBounds: new Rect(-1920, -200, 1280, 720));

        Assert.That(mapped, Is.EqualTo(new Rect(-1600, -20, 160, 90)));
    }

    [Test]
    public void Image_region_uses_the_capture_ratio_once_for_a_synthetic_144_dpi_capture()
    {
        var dpi = new ViewportDpi(
            WindowDpiX: 144,
            WindowDpiY: 144,
            WindowScaleX: 1.5,
            WindowScaleY: 1.5,
            MonitorDpiX: 144,
            MonitorDpiY: 144,
            MonitorScaleX: 1.5,
            MonitorScaleY: 1.5,
            Awareness: DpiAwareness.PerMonitorAware);
        var capturedBounds = new Rect(
            -300,
            75,
            checked((int)(1000 * dpi.WindowScaleX)),
            checked((int)(600 * dpi.WindowScaleY)));

        // Correlation coordinates are image pixels. The image-to-capture ratio applies once;
        // applying the reported DPI scale a second time would move and enlarge this region.
        var mapped = ScreenshotCorrelationGeometry.MapImageRegionToScreen(
            new Rect(100, 80, 200, 120),
            imageWidth: 1000,
            imageHeight: 600,
            capturedBounds: capturedBounds);

        Assert.That(mapped, Is.EqualTo(new Rect(-150, 195, 300, 180)));
    }

    [Test]
    public void One_image_pixel_keeps_one_canonical_screen_point_when_scaling_expands_it()
    {
        var mapped = ScreenshotCorrelationGeometry.MapImageRegionToScreen(
            new Rect(100, 80, 1, 1),
            imageWidth: 1000,
            imageHeight: 600,
            capturedBounds: new Rect(-300, 75, 1500, 900));
        var point = ScreenshotCorrelationGeometry.GetCanonicalScreenPoint(mapped);

        Assert.Multiple(() =>
        {
            Assert.That(mapped, Is.EqualTo(new Rect(-150, 195, 2, 2)));
            Assert.That(point, Is.EqualTo(new ScreenshotCorrelationPoint(-149, 196)));
        });
    }

    [Test]
    public void Fractional_image_mapping_expands_outward_to_preserve_the_whole_query()
    {
        var mapped = ScreenshotCorrelationGeometry.MapImageRegionToScreen(
            new Rect(1, 1, 1, 1),
            imageWidth: 3,
            imageHeight: 3,
            capturedBounds: new Rect(10, 20, 10, 10));

        Assert.That(mapped, Is.EqualTo(new Rect(13, 23, 4, 4)));
    }

    [Test]
    public void Screen_region_mapping_intersects_capture_and_returns_image_pixels()
    {
        var mapped = ScreenshotCorrelationGeometry.MapScreenRegionToImage(
            new Rect(-550, -50, 300, 300),
            imageWidth: 1000,
            imageHeight: 500,
            capturedBounds: new Rect(-500, 0, 1500, 750));

        Assert.That(mapped, Is.EqualTo(new Rect(0, 0, 167, 167)));
    }

    [Test]
    public void Image_point_selected_from_ancestor_clipped_region_round_trips_inside_the_rendered_area()
    {
        var capturedBounds = new Rect(100, 200, 150, 90);
        var targetBounds = new Rect(190, 220, 90, 40);
        var ancestorClip = new Rect(170, 200, 45, 80);
        var nominalCapturedTarget = ScreenshotCorrelationGeometry.Intersect(targetBounds, capturedBounds)!;
        var nominalCenter = new ScreenshotCorrelationPoint(
            nominalCapturedTarget.X + nominalCapturedTarget.Width / 2,
            nominalCapturedTarget.Y + nominalCapturedTarget.Height / 2);
        var renderedRegion = ScreenshotCorrelationGeometry.Intersect(nominalCapturedTarget, ancestorClip)!;

        var imageRegion = ScreenshotCorrelationGeometry.MapScreenRegionToImage(
            renderedRegion,
            imageWidth: 100,
            imageHeight: 60,
            capturedBounds: capturedBounds)!;
        var imagePoint = new Rect(
            imageRegion.X + imageRegion.Width / 2,
            imageRegion.Y + imageRegion.Height / 2,
            1,
            1);
        var mappedPointRegion = ScreenshotCorrelationGeometry.MapImageRegionToScreen(
            imagePoint,
            imageWidth: 100,
            imageHeight: 60,
            capturedBounds: capturedBounds);
        var mappedPoint = ScreenshotCorrelationGeometry.GetCanonicalScreenPoint(mappedPointRegion);

        Assert.Multiple(() =>
        {
            Assert.That(
                ScreenshotCorrelationGeometry.ContainsPoint(ancestorClip, nominalCenter.X, nominalCenter.Y),
                Is.False,
                "Centering only the target/client intersection reproduces the clipped-pixel bug.");
            Assert.That(
                ScreenshotCorrelationGeometry.ContainsPoint(renderedRegion, mappedPoint.X, mappedPoint.Y),
                Is.True);
            Assert.That(
                ScreenshotCorrelationGeometry.ContainsPoint(targetBounds, mappedPoint.X, mappedPoint.Y),
                Is.True);
            Assert.That(
                ScreenshotCorrelationGeometry.ContainsPoint(capturedBounds, mappedPoint.X, mappedPoint.Y),
                Is.True);
            Assert.That(
                ScreenshotCorrelationGeometry.ContainsPoint(ancestorClip, mappedPoint.X, mappedPoint.Y),
                Is.True);
        });
    }

    [Test]
    public void Screen_region_outside_capture_has_no_image_mapping()
    {
        var mapped = ScreenshotCorrelationGeometry.MapScreenRegionToImage(
            new Rect(-900, -700, 20, 20),
            imageWidth: 640,
            imageHeight: 480,
            capturedBounds: new Rect(-500, -300, 640, 480));

        Assert.That(mapped, Is.Null);
    }

    [TestCase(-1, 0, 1, 1)]
    [TestCase(0, -1, 1, 1)]
    [TestCase(0, 0, 0, 1)]
    [TestCase(0, 0, 1, 0)]
    [TestCase(639, 0, 2, 1)]
    [TestCase(0, 479, 1, 2)]
    public void Image_region_mapping_rejects_invalid_or_out_of_image_bounds(
        int x,
        int y,
        int width,
        int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenshotCorrelationGeometry.MapImageRegionToScreen(
                new Rect(x, y, width, height),
                imageWidth: 640,
                imageHeight: 480,
                capturedBounds: new Rect(-200, 50, 640, 480)));
    }

    [Test]
    public void Image_mapping_rejects_invalid_image_or_capture_dimensions()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ScreenshotCorrelationGeometry.MapImageRegionToScreen(
                    new Rect(0, 0, 1, 1),
                    imageWidth: 0,
                    imageHeight: 480,
                    capturedBounds: new Rect(0, 0, 640, 480)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ScreenshotCorrelationGeometry.MapImageRegionToScreen(
                    new Rect(0, 0, 1, 1),
                    imageWidth: 640,
                    imageHeight: 480,
                    capturedBounds: new Rect(0, 0, 0, 480)));
        });
    }

    [Test]
    public void Correlation_limits_are_clamped_to_strict_upper_caps()
    {
        var normalized = AutomationController.NormalizeScreenshotCorrelationOptions(
            new ScreenshotCorrelationOptions(
                X: 0,
                Y: 0,
                MaxCandidates: int.MaxValue,
                MaxNodes: int.MaxValue,
                IncludeAncestors: true,
                MaxAncestors: int.MaxValue));

        Assert.Multiple(() =>
        {
            Assert.That(normalized.MaxCandidates, Is.EqualTo(25));
            Assert.That(normalized.MaxNodes, Is.EqualTo(200_000));
            Assert.That(normalized.MaxAncestors, Is.EqualTo(20));
        });
    }

    [Test]
    public void Correlation_limits_are_clamped_to_safe_lower_bounds()
    {
        var normalized = AutomationController.NormalizeScreenshotCorrelationOptions(
            new ScreenshotCorrelationOptions(
                X: 0,
                Y: 0,
                MaxCandidates: int.MinValue,
                MaxNodes: int.MinValue,
                IncludeAncestors: true,
                MaxAncestors: int.MinValue));

        Assert.Multiple(() =>
        {
            Assert.That(normalized.MaxCandidates, Is.EqualTo(1));
            Assert.That(normalized.MaxNodes, Is.EqualTo(1));
            Assert.That(normalized.MaxAncestors, Is.Zero);
        });
    }

    [Test]
    public void Rectangle_overlap_detection_is_strict_and_scales_beyond_return_limits()
    {
        var disjoint = Enumerable.Range(0, 10_000)
            .Select(index => new Rect(index * 2, 0, 1, 10))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ScreenshotCorrelationOverlap.HasAnyOverlap(disjoint), Is.False);
            Assert.That(
                ScreenshotCorrelationOverlap.HasAnyOverlap(
                    [new Rect(0, 0, 10, 10), new Rect(10, 0, 10, 10)]),
                Is.False,
                "Edge contact is not positive-area overlap.");
            Assert.That(
                ScreenshotCorrelationOverlap.HasAnyOverlap(
                    disjoint.Append(new Rect(5, 2, 4, 3))),
                Is.True);
        });
    }

    [Test]
    public void Obscuration_classification_treats_a_distinct_owned_overlay_root_as_an_obscurer()
    {
        var result = AutomationController.ClassifyScreenshotObscurationSamples(
            targetRootWindowHandle: 100,
            sampledRootWindowHandles: [100, 200, 200, 100, 100]);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(ScreenshotObscurationState.PotentiallyObscured));
            Assert.That(result.SampledPoints, Is.EqualTo(5));
            Assert.That(result.ObscuredPoints, Is.EqualTo(2));
            Assert.That(result.ObscuringWindowHandles, Is.EqualTo(new long[] { 200 }));
        });
    }

    [Test]
    public void Screenshot_request_omits_absent_correlation_options()
    {
        var json = JsonSerializer.Serialize(new TakeScreenshotRequest());

        Assert.That(json, Does.Not.Contain("\"Correlation\""));
    }

    [Test]
    public void Screenshot_correlation_options_round_trip_all_bounded_controls()
    {
        var options = new ScreenshotCorrelationOptions(
            X: 31,
            Y: 47,
            Width: 120,
            Height: 80,
            Backend: ScreenshotCorrelationBackend.Both,
            MaxCandidates: 25,
            MaxNodes: 200_000,
            IncludeAncestors: true,
            MaxAncestors: 20,
            Annotate: false);

        var json = JsonSerializer.Serialize(options);
        var roundTrip = JsonSerializer.Deserialize<ScreenshotCorrelationOptions>(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Backend\":\"Both\""));
            Assert.That(roundTrip, Is.EqualTo(options));
        });
    }

    [Test]
    public void Wpf_agent_request_preserves_point_semantics_when_the_mapped_region_is_larger_than_one_pixel()
    {
        var request = new CorrelateWpfScreenshotRegionRequest(
            ScreenRegionPhysicalPixels: new Rect(-150, 195, 2, 2),
            WindowHandle: 42,
            ScreenPointPhysicalPixels: new ScreenshotCorrelationPoint(-149, 196));

        var json = JsonSerializer.Serialize(request);
        var roundTrip = JsonSerializer.Deserialize<CorrelateWpfScreenshotRegionRequest>(json);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip, Is.EqualTo(request));
            Assert.That(roundTrip!.ScreenPointPhysicalPixels, Is.EqualTo(new ScreenshotCorrelationPoint(-149, 196)));
        });
    }

    [Test]
    public void Correlation_candidate_omits_optional_context_when_not_requested()
    {
        var candidate = new ScreenshotCorrelationCandidate(
            Index: 1,
            Backend: InspectionBackend.Uia,
            Element: new ElementRef(
                Type: "Button",
                AutomationId: "Correlation_FrontElement",
                Name: "Front overlap target",
                XPath: "/Window[1]/Button[2]",
                ClassName: "Button",
                Bounds: new Rect(100, 200, 190, 96)),
            MatchKind: ScreenshotCorrelationMatchKind.BoundsIntersection,
            IntersectionPhysicalPixels: new Rect(140, 230, 20, 20));

        var json = JsonSerializer.Serialize(candidate);
        var roundTrip = JsonSerializer.Deserialize<ScreenshotCorrelationCandidate>(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("\"Ancestors\""));
            Assert.That(json, Does.Not.Contain("\"Annotation\""));
            Assert.That(roundTrip, Is.EqualTo(candidate));
        });
    }
}
