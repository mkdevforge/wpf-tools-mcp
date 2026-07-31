using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.UIA3;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ScreenshotSequenceIntegrationTests
{
    [Test]
    public async Task Observation_probe_external_delayed_transition_changes_the_sequence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "core",
            cancellationToken: cts.Token);
        var outputParent = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-sequence-integration-{Guid.NewGuid():N}");
        var markerPath = Path.Combine(outputParent, "observation.log");
        Directory.CreateDirectory(outputParent);

        var sessionId = "";
        var pid = 0;
        try
        {
            var executablePath = TestAppPaths.FindObservationProbeTestAppExecutable();
            var launch = await mcp.CallToolAsync<LaunchAppResponse>(
                "launch_app",
                new Dictionary<string, object?>
                {
                    ["exePath"] = executablePath,
                    ["workingDirectory"] = Path.GetDirectoryName(executablePath)!,
                    ["args"] = new[] { "--marker-path", markerPath }
                },
                cts.Token);
            sessionId = launch.SessionId;
            pid = launch.Pid;

            using var automation = new UIA3Automation();
            var application = FlaUI.Core.Application.Attach(pid);
            var window = application.GetMainWindow(automation)
                ?? throw new InvalidOperationException("ObservationProbe main window was not available.");
            var runOrdered = window.FindFirstDescendant(
                    condition => condition.ByAutomationId("Observation_RunOrdered"))
                ?? throw new InvalidOperationException("ObservationProbe ordered-transition button was not available.");
            var invoke = runOrdered.Patterns.Invoke.PatternOrDefault
                ?? throw new InvalidOperationException("ObservationProbe ordered-transition button did not support InvokePattern.");

            var captureTask = mcp.CallToolAsync<TakeScreenshotSequenceResponse>(
                "take_screenshot_sequence",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["frameCount"] = 16,
                    ["intervalMs"] = 100,
                    ["outputDirectory"] = outputParent
                },
                cts.Token);

            _ = await WaitForFirstFrameAsync(outputParent, cts.Token);
            invoke.Invoke();

            var response = await captureTask;
            await WaitForMarkerAsync(markerPath, "ordered-complete", cts.Token);
            var manifest = await ReadManifestAsync(response.ManifestPath, cts.Token);
            var distinctFrames = manifest.Frames
                .Select(frame => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(frame.Path))))
                .Distinct(StringComparer.Ordinal)
                .Count();

            Assert.Multiple(() =>
            {
                Assert.That(response.Complete, Is.True);
                Assert.That(response.CapturedFrameCount, Is.EqualTo(16));
                Assert.That(Path.GetDirectoryName(response.DirectoryPath), Is.EqualTo(Path.GetFullPath(outputParent)));
                Assert.That(Path.GetFileName(response.DirectoryPath), Does.Match("^sequence-[0-9a-f]{32}$"));
                Assert.That(manifest.CaptureContext.Viewport, Is.Not.Null);
                Assert.That(manifest.Frames, Has.Count.EqualTo(16));
                Assert.That(manifest.Frames.All(frame => File.Exists(frame.Path)), Is.True);
                Assert.That(distinctFrames, Is.GreaterThan(1));
            });
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    _ = await mcp.CallToolAsync<CloseAppResponse>(
                        "terminate_app",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = sessionId,
                            ["timeoutMs"] = 3_000
                        },
                        cleanupCts.Token);
                }
                catch
                {
                }
            }

            await KillProcessBestEffortAsync(pid);
            try
            {
                Directory.Delete(outputParent, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<string> WaitForFirstFrameAsync(
        string outputParent,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = Directory
                .EnumerateFiles(outputParent, "frame-0000.png", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (frame is not null)
            {
                return frame;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForMarkerAsync(
        string markerPath,
        string marker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(markerPath) &&
                (await File.ReadAllLinesAsync(markerPath, cancellationToken)).Contains(marker, StringComparer.Ordinal))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task<ScreenshotSequenceManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ScreenshotSequenceManifest>(
                   stream,
                   new JsonSerializerOptions(JsonSerializerDefaults.Web),
                   cancellationToken)
               ?? throw new InvalidOperationException("Manifest did not deserialize.");
    }

    private static async Task KillProcessBestEffortAsync(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token);
            }
        }
        catch
        {
        }
    }
}
