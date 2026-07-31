using System.Text.Json;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ScreenshotSequenceTests
{
    private static readonly Rect PinnedBounds = new(10, 20, 320, 180);

    [Test]
    public async Task Sequence_records_serial_order_actual_timing_and_manifest_shape()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var time = new ManualTimeProvider();
            var delays = new List<TimeSpan>();

            var response = await ScreenshotSequenceCoordinator.CaptureAsync(
                "sequence-id",
                directory,
                ScreenshotCaptureArea.Client,
                ScreenshotClipMode.Intersect,
                frameCount: 3,
                intervalMs: 100,
                async (index, path, token) =>
                {
                    await File.WriteAllTextAsync(path, $"frame {index}", token);
                    time.Advance(TimeSpan.FromMilliseconds(7));
                    return Sample(wasClipped: index == 0);
                },
                timeProvider: time,
                delay: (duration, _) =>
                {
                    delays.Add(duration);
                    time.Advance(duration);
                    return Task.CompletedTask;
                });

            var manifest = await ReadManifestAsync(response.ManifestPath);
            Assert.Multiple(() =>
            {
                Assert.That(response.Complete, Is.True);
                Assert.That(response.CapturedFrameCount, Is.EqualTo(3));
                Assert.That(response.FirstFramePath, Does.EndWith("frame-0000.png"));
                Assert.That(response.LastFramePath, Does.EndWith("frame-0002.png"));
                Assert.That(response.WindowHandleUsed, Is.EqualTo(42));
                Assert.That(response.CapturedBounds, Is.EqualTo(PinnedBounds));
                Assert.That(response.CaptureModeUsed, Is.EqualTo(ScreenshotCaptureMode.Screen));
                Assert.That(response.WasClipped, Is.True);
                Assert.That(delays, Is.EqualTo(new[]
                {
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100)
                }));
                Assert.That(manifest.Version, Is.EqualTo(1));
                Assert.That(manifest.SequenceId, Is.EqualTo("sequence-id"));
                Assert.That(manifest.CaptureContext.WindowHandleUsed, Is.EqualTo(42));
                Assert.That(manifest.CaptureContext.CapturedBounds, Is.EqualTo(PinnedBounds));
                Assert.That(manifest.CaptureContext.CaptureModeUsed, Is.EqualTo(ScreenshotCaptureMode.Screen));
                Assert.That(manifest.CaptureContext.WasClipped, Is.True);
                Assert.That(manifest.Frames.Select(frame => frame.Index), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(manifest.Frames.Select(frame => frame.ElapsedMs), Is.EqualTo(new long[] { 0, 107, 214 }));
                Assert.That(manifest.Frames.Select(frame => frame.CaptureDurationMs), Is.EqualTo(new long[] { 7, 7, 7 }));
                Assert.That(manifest.Frames.Select(frame => frame.WasClipped), Is.EqualTo(new[] { true, false, false }));
                Assert.That(Directory.GetFiles(directory, ".manifest-*.tmp"), Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Later_capture_failure_returns_completed_evidence_and_bounded_failure()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var response = await ScreenshotSequenceCoordinator.CaptureAsync(
                "sequence-id",
                directory,
                ScreenshotCaptureArea.Client,
                ScreenshotClipMode.Intersect,
                frameCount: 3,
                intervalMs: 1,
                async (index, path, token) =>
                {
                    if (index == 1)
                    {
                        await File.WriteAllTextAsync(path, "partial frame", token);
                        throw new InvalidOperationException(new string('x', 3_000));
                    }

                    await File.WriteAllTextAsync(path, "frame", token);
                    return Sample();
                },
                delay: static (_, _) => Task.CompletedTask);

            var manifest = await ReadManifestAsync(response.ManifestPath);
            Assert.Multiple(() =>
            {
                Assert.That(response.Complete, Is.False);
                Assert.That(response.CapturedFrameCount, Is.EqualTo(1));
                Assert.That(response.FirstFramePath, Is.EqualTo(response.LastFramePath));
                Assert.That(response.Failure?.Code, Is.EqualTo("capture_failed"));
                Assert.That(response.Failure?.Detail, Has.Length.EqualTo(2_000));
                Assert.That(response.Failure?.ExceptionType, Is.EqualTo(typeof(InvalidOperationException).FullName));
                Assert.That(manifest.Complete, Is.False);
                Assert.That(manifest.CapturedFrameCount, Is.EqualTo(1));
                Assert.That(manifest.Frames, Has.Count.EqualTo(1));
                Assert.That(manifest.Failure, Is.EqualTo(response.Failure));
                Assert.That(File.Exists(Path.Combine(directory, "frame-0001.png")), Is.False);
                Assert.That(Directory.GetFiles(directory, ".frame-*.tmp"), Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void First_capture_failure_propagates_without_publishing_a_manifest()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ScreenshotSequenceCoordinator.CaptureAsync(
                    "sequence-id",
                    directory,
                    ScreenshotCaptureArea.Client,
                    ScreenshotClipMode.Intersect,
                    frameCount: 2,
                    intervalMs: 1,
                    (_, _, _) => throw new InvalidOperationException("first capture failed"),
                    delay: static (_, _) => Task.CompletedTask));

            Assert.That(File.Exists(Path.Combine(directory, "manifest.json")), Is.False);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Cancellation_after_first_frame_publishes_partial_manifest_then_propagates()
    {
        var directory = CreateTemporaryDirectory();
        using var cts = new CancellationTokenSource();
        try
        {
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await ScreenshotSequenceCoordinator.CaptureAsync(
                    "sequence-id",
                    directory,
                    ScreenshotCaptureArea.Client,
                    ScreenshotClipMode.Intersect,
                    frameCount: 3,
                    intervalMs: 1,
                    async (_, path, token) =>
                    {
                        await File.WriteAllTextAsync(path, "frame", token);
                        return Sample();
                    },
                    cts.Token,
                    delay: (_, token) =>
                    {
                        cts.Cancel();
                        return Task.FromCanceled(token);
                    }));

            var manifest = ReadManifestAsync(Path.Combine(directory, "manifest.json")).GetAwaiter().GetResult();
            Assert.Multiple(() =>
            {
                Assert.That(manifest.Complete, Is.False);
                Assert.That(manifest.CapturedFrameCount, Is.EqualTo(1));
                Assert.That(manifest.Failure?.Code, Is.EqualTo("cancelled"));
                Assert.That(manifest.Frames.Single().Index, Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Changed_capture_context_stops_without_claiming_the_frame()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var response = await ScreenshotSequenceCoordinator.CaptureAsync(
                "sequence-id",
                directory,
                ScreenshotCaptureArea.Client,
                ScreenshotClipMode.Intersect,
                frameCount: 2,
                intervalMs: 1,
                async (index, path, token) =>
                {
                    await File.WriteAllTextAsync(path, "frame", token);
                    return Sample(bounds: index == 0 ? PinnedBounds : new Rect(11, 20, 320, 180));
                },
                delay: static (_, _) => Task.CompletedTask);

            var manifest = await ReadManifestAsync(response.ManifestPath);
            Assert.Multiple(() =>
            {
                Assert.That(response.Complete, Is.False);
                Assert.That(response.CapturedFrameCount, Is.EqualTo(1));
                Assert.That(response.Failure?.Code, Is.EqualTo("capture_context_changed"));
                Assert.That(manifest.CaptureContext.CapturedBounds, Is.EqualTo(PinnedBounds));
                Assert.That(manifest.Frames, Has.Count.EqualTo(1));
                Assert.That(File.Exists(Path.Combine(directory, "frame-0001.png")), Is.False);
                Assert.That(Directory.GetFiles(directory, ".frame-*.tmp"), Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase(1, 100)]
    [TestCase(301, 100)]
    [TestCase(2, 0)]
    [TestCase(300, 101)]
    public void Schedule_validation_rejects_out_of_scope_requests(int frameCount, int intervalMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenshotSequenceCoordinator.ValidateSchedule(frameCount, intervalMs));
    }

    private static ScreenshotSequenceCaptureSample Sample(Rect? bounds = null, bool wasClipped = false) =>
        new(
            Width: 320,
            Height: 180,
            CapturedBounds: bounds ?? PinnedBounds,
            WasClipped: wasClipped,
            WindowHandleUsed: 42,
            CaptureModeUsed: ScreenshotCaptureMode.Screen,
            Viewport: null);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpf-tools-mcp-sequence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<ScreenshotSequenceManifest> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ScreenshotSequenceManifest>(
                   stream,
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException("Manifest did not deserialize.");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += (long)duration.TotalMilliseconds;
        }
    }
}
