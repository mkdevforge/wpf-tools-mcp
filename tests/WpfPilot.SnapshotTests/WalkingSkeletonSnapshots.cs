using System.Threading;
using ImageMagick;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Automation;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class WalkingSkeletonSnapshots
{
    private AutomationController _automation = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _automation = new AutomationController();

        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        await _automation.LaunchAsync(new LaunchAppRequest(exePath, WorkingDirectory: workingDirectory));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_automation is null)
        {
            return;
        }

        try
        {
            await _automation.CloseAsync(new CloseAppRequest(Force: true, TimeoutMs: 2000));
        }
        catch
        {
        }

        _automation.Dispose();
    }

    [Test]
    public async Task ListWindows_snapshot()
    {
        var result = await _automation.ListWindowsAsync();

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
        var screenshot = await _automation.TakeScreenshotAsync(new TakeScreenshotRequest());
        Assert.That(screenshot.Width, Is.GreaterThan(0));
        Assert.That(screenshot.Height, Is.GreaterThan(0));

        var bytes = Convert.FromBase64String(screenshot.PngBase64);
        var path = Path.Combine(Path.GetTempPath(), $"WpfPilot.TestApp.{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, bytes);

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
