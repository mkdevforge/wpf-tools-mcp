using System.Threading;
using ImageMagick;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class InspectionSnapshots
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
    public async Task GetVisualTree_main_window_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "uia",
            ["depth"] = 4,
            ["maxNodes"] = 200
        });

        await Verifier.Verify(result);
    }

    [Test]
    public async Task GetElementProperties_textbox_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_TextBox"
            }
        });

        var stable = new
        {
            Element = result.Element with { Bounds = result.Element.Bounds with { X = 0, Y = 0 } },
            PropertyKeys = result.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            PatternKeys = result.Patterns.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            ValuePattern = result.Patterns.TryGetValue("Value", out var valuePattern) ? valuePattern?.ToJsonString() : null
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task TakeScreenshot_element_verified_png()
    {
        var screenshot = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Button"
            },
            ["captureMode"] = "auto",
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

    [Test]
    public async Task LocatorStrategies_resolve_snapshot()
    {
        async Task<ElementSummary> ResolveAsync(Dictionary<string, object?> locator) =>
            (await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = locator
            })).Element;

        var xpathTarget = await ResolveAsync(new Dictionary<string, object?> { ["automationId"] = "XPathDemo_Right_Button1" });

        var resolved = new[]
        {
            new { Strategy = "automationId", Element = await ResolveAsync(new Dictionary<string, object?> { ["automationId"] = "Basic_TextBox" }) },
            new { Strategy = "automationIdContains", Element = await ResolveAsync(new Dictionary<string, object?> { ["automationIdContains"] = "TextBox" }) },
            new { Strategy = "name", Element = await ResolveAsync(new Dictionary<string, object?> { ["name"] = "Right Panel" }) },
            new { Strategy = "nameContains", Element = await ResolveAsync(new Dictionary<string, object?> { ["nameContains"] = "Basic Controls" }) },
            new { Strategy = "className", Element = await ResolveAsync(new Dictionary<string, object?> { ["className"] = "Slider" }) },
            new { Strategy = "classNameContains", Element = await ResolveAsync(new Dictionary<string, object?> { ["classNameContains"] = "Slid" }) },
            new { Strategy = "typeEquals", Element = await ResolveAsync(new Dictionary<string, object?> { ["typeEquals"] = "TextBox" }) },
            new { Strategy = "xpath", Element = await ResolveAsync(new Dictionary<string, object?> { ["xpath"] = xpathTarget.XPath }) },
            new { Strategy = "index-disambiguation", Element = await ResolveAsync(new Dictionary<string, object?> { ["name"] = "Alpha", ["index"] = 0 }) },
            new { Strategy = "strict-false", Element = await ResolveAsync(new Dictionary<string, object?> { ["name"] = "Alpha", ["strict"] = false }) },
        };

        var stable = resolved
            .Select(r => new
            {
                r.Strategy,
                Element = r.Element with { Bounds = r.Element.Bounds with { X = 0, Y = 0 } }
            })
            .ToArray();

        await Verifier.Verify(stable);
    }
}
