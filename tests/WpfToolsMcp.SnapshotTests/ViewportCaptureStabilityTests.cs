using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ViewportCaptureStabilityTests
{
    [Test]
    public async Task Stable_first_attempt_returns_capture_and_matching_viewport()
    {
        var viewport = CreateViewport(640, 480);
        var discarded = new List<int>();
        var events = new List<string>();
        var sampleNumber = 0;

        var result = await ViewportCaptureStabilityCoordinator.CaptureAsync(
            includeViewport: true,
            sampleViewport: () =>
            {
                events.Add($"sample-{++sampleNumber}");
                return viewport;
            },
            capture: () =>
            {
                events.Add("capture");
                return 1;
            },
            discardCapture: discarded.Add,
            waitForStableViewport: _ => Task.CompletedTask,
            cancellationToken: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Capture, Is.EqualTo(1));
            Assert.That(result.Viewport, Is.EqualTo(viewport));
            Assert.That(discarded, Is.Empty);
            Assert.That(events, Is.EqualTo(new[] { "sample-1", "capture", "sample-2" }));
        });
    }

    [Test]
    public async Task Changed_first_attempt_is_discarded_before_stable_retry()
    {
        var first = CreateViewport(640, 480);
        var second = CreateViewport(700, 500);
        var samples = new Queue<ViewportConditions>([first, second, second, second]);
        var captureNumber = 0;
        var discarded = new List<int>();
        var waits = 0;

        var result = await ViewportCaptureStabilityCoordinator.CaptureAsync(
            includeViewport: true,
            sampleViewport: () => samples.Dequeue(),
            capture: () => ++captureNumber,
            discardCapture: discarded.Add,
            waitForStableViewport: _ =>
            {
                waits++;
                return Task.CompletedTask;
            },
            cancellationToken: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Capture, Is.EqualTo(2));
            Assert.That(result.Viewport, Is.EqualTo(second));
            Assert.That(discarded, Is.EqualTo(new[] { 1 }));
            Assert.That(waits, Is.EqualTo(1));
        });
    }

    [Test]
    public void Persistent_changes_discard_every_capture_and_report_actionable_error()
    {
        var samples = new Queue<ViewportConditions>(
        [
            CreateViewport(600, 400),
            CreateViewport(610, 410),
            CreateViewport(620, 420),
            CreateViewport(630, 430),
            CreateViewport(640, 440),
            CreateViewport(650, 450)
        ]);
        var captureNumber = 0;
        var discarded = new List<int>();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ViewportCaptureStabilityCoordinator.CaptureAsync(
                includeViewport: true,
                sampleViewport: () => samples.Dequeue(),
                capture: () => ++captureNumber,
                discardCapture: discarded.Add,
                waitForStableViewport: _ => Task.CompletedTask,
                cancellationToken: CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.StartWith("screenshot_viewport_unstable:"));
            Assert.That(discarded, Is.EqualTo(new[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void Failed_post_capture_sample_discards_the_unlabeled_capture()
    {
        var viewport = CreateViewport(640, 480);
        var sampleCount = 0;
        var discarded = new List<int>();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ViewportCaptureStabilityCoordinator.CaptureAsync(
                includeViewport: true,
                sampleViewport: () => ++sampleCount == 1
                    ? viewport
                    : throw new InvalidOperationException("sample failed"),
                capture: () => 1,
                discardCapture: discarded.Add,
                waitForStableViewport: _ => Task.CompletedTask,
                cancellationToken: CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("sample failed"));
            Assert.That(discarded, Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public async Task Viewport_disabled_captures_once_without_sampling()
    {
        var sampleCount = 0;
        var captureCount = 0;

        var result = await ViewportCaptureStabilityCoordinator.CaptureAsync(
            includeViewport: false,
            sampleViewport: () =>
            {
                sampleCount++;
                return CreateViewport(1, 1);
            },
            capture: () => ++captureCount,
            discardCapture: _ => Assert.Fail("A viewport-disabled capture must not be discarded."),
            waitForStableViewport: _ => Task.CompletedTask,
            cancellationToken: CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Capture, Is.EqualTo(1));
            Assert.That(result.Viewport, Is.Null);
            Assert.That(sampleCount, Is.Zero);
            Assert.That(captureCount, Is.EqualTo(1));
        });
    }

    private static ViewportConditions CreateViewport(int width, int height) =>
        new(
            ClientBoundsPhysicalPixels: new Rect(100, 100, width, height),
            OuterBoundsPhysicalPixels: new Rect(92, 69, width + 16, height + 39),
            ClientSizePhysicalPixels: new ViewportSize(width, height),
            ClientSizeWpfDips: new ViewportSize(width, height),
            FramePhysicalPixels: new ViewportFrameInsets(8, 31, 8, 8),
            Dpi: new ViewportDpi(96, 96, 1, 1, 96, 96, 1, 1, DpiAwareness.PerMonitorAware),
            Monitor: new ViewportMonitor(
                "DISPLAY1",
                new Rect(0, 0, 1920, 1080),
                new Rect(0, 0, 1920, 1040),
                true),
            WindowState: WindowState.Normal);
}
