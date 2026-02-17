using System.Threading;
using NUnit.Framework;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ScreenshotCaptureDumps
{
    [Test]
    public async Task Dump_main_window_screen_vs_printwindow()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WPF_PILOT_DUMP_SCREENSHOTS"), "1", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Pass("Set WPF_PILOT_DUMP_SCREENSHOTS=1 to dump screenshots for manual comparison.");
        }

        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe);

        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        _ = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        try
        {
            var artifactsDir = Path.Combine(RepoRoot.Find(), "tests", "WpfPilot.SnapshotTests", "_Artifacts");
            Directory.CreateDirectory(artifactsDir);

            var screenPath = Path.Combine(artifactsDir, "MainWindow.screen.png");
            var printWindowPath = Path.Combine(artifactsDir, "MainWindow.printWindow.png");

            var screen = await mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["captureMode"] = "screen",
                ["outputPath"] = screenPath,
            });

            var printWindow = await mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["captureMode"] = "printWindow",
                ["outputPath"] = printWindowPath,
            });

            Assert.That(File.Exists(screen.Path), Is.True, $"Screen capture was not created: {screen.Path}");
            Assert.That(File.Exists(printWindow.Path), Is.True, $"PrintWindow capture was not created: {printWindow.Path}");

            TestContext.WriteLine($"Wrote: {screenPath} ({screen.Width}x{screen.Height})");
            TestContext.WriteLine($"Wrote: {printWindowPath} ({printWindow.Width}x{printWindow.Height})");
        }
        finally
        {
            try
            {
                _ = await mcp.CallToolAsync<CloseAppResponse>("close_app", new Dictionary<string, object?>
                {
                    ["force"] = true,
                    ["timeoutMs"] = 2000
                });
            }
            catch
            {
            }
        }
    }
}
