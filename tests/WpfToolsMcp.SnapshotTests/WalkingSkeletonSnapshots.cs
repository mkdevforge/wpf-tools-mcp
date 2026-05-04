using System.Threading;
using ImageMagick;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class WalkingSkeletonSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);

        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task ListWindows_snapshot()
    {
        var result = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId
        });

        var stable = result with
        {
            ProcessId = -1,
            Windows = result.Windows
                .Select(w => w with { Handle = 0, Bounds = w.Bounds with { X = 0, Y = 0 } })
                .ToArray()
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task TakeScreenshot_main_window_verified_png()
    {
        var screenshot = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["captureMode"] = "auto"
        });
        Assert.That(screenshot.Width, Is.GreaterThan(0));
        Assert.That(screenshot.Height, Is.GreaterThan(0));
        Assert.That(File.Exists(screenshot.Path), Is.True, $"Screenshot file was not created: {screenshot.Path}");
        var path = screenshot.Path;

        try
        {
            var settings = new VerifySettings();
            settings.ImageMagickComparer(threshold: 0.02, metric: ErrorMetric.Fuzz);
            await Verifier.VerifyFile(path, settings);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
