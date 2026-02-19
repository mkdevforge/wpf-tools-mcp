using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ScrollSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        await _mcp.DisposeAsync();
    }

    private async Task LaunchScrollAppAsync()
    {
        var exePath = TestAppPaths.FindScrollTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;
    }

    private async Task CloseAppAsync()
    {
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
        finally
        {
            _sessionId = "";
        }
    }

    [Test]
    public async Task ScrollToElement_brings_target_button_into_view_snapshot()
    {
        await LaunchScrollAppAsync();
        try
        {
            var container = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_Viewer"
                }
            });

            var before = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_TargetButton"
                }
            });

            var containerBottom = container.Element.Bounds.Y + container.Element.Bounds.Height;
            Assert.That(
                before.Element.Bounds.Y,
                Is.GreaterThanOrEqualTo(containerBottom),
                "Expected Scroll_TargetButton to start below the Scroll_Viewer viewport.");

            var scroll = await _mcp.CallToolAsync<ScrollToElementResponse>("scroll_to_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_TargetButton"
                },
                ["containerLocator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_Viewer"
                }
            });

            var after = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_TargetButton"
                }
            });

            var afterBottom = after.Element.Bounds.Y + after.Element.Bounds.Height;
            Assert.That(scroll.Scrolled, Is.True);
            Assert.That(after.Element.Bounds.Y, Is.LessThan(containerBottom));
            Assert.That(afterBottom, Is.GreaterThan(container.Element.Bounds.Y));

            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_TargetButton"
                },
                ["clickMode"] = "mouseAlways"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_Status"
                }
            });

            await Verifier.Verify(new
            {
                ContainerBounds = container.Element.Bounds with { X = 0, Y = 0 },
                BeforeBounds = before.Element.Bounds with { X = 0, Y = 0 },
                Scroll = scroll,
                AfterBounds = after.Element.Bounds with { X = 0, Y = 0 },
                Click = click,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task ScrollToElement_handles_offscreen_element_with_empty_bounds_snapshot()
    {
        await LaunchScrollAppAsync();
        try
        {
            var container = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_Viewer"
                }
            });

            var before = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_BadBoundsTargetButton"
                }
            });

            Assert.That(before.Element.IsOffscreen, Is.True, "Expected Scroll_BadBoundsTargetButton to start offscreen.");
            Assert.That(before.Element.Bounds.Width, Is.EqualTo(0));
            Assert.That(before.Element.Bounds.Height, Is.EqualTo(0));

            var scroll = await _mcp.CallToolAsync<ScrollToElementResponse>("scroll_to_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_BadBoundsTargetButton"
                },
                ["containerLocator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_Viewer"
                }
            });

            var after = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_BadBoundsTargetButton"
                }
            });

            Assert.That(scroll.Scrolled, Is.True);
            Assert.That(after.Element.IsOffscreen, Is.False);
            Assert.That(after.Element.Bounds.Width, Is.GreaterThan(0));
            Assert.That(after.Element.Bounds.Height, Is.GreaterThan(0));

            var containerBottom = container.Element.Bounds.Y + container.Element.Bounds.Height;
            var afterBottom = after.Element.Bounds.Y + after.Element.Bounds.Height;
            Assert.That(after.Element.Bounds.Y, Is.LessThan(containerBottom));
            Assert.That(afterBottom, Is.GreaterThan(container.Element.Bounds.Y));

            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_BadBoundsTargetButton"
                },
                ["clickMode"] = "mouseAlways"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Scroll_Status"
                }
            });

            await Verifier.Verify(new
            {
                ContainerBounds = container.Element.Bounds with { X = 0, Y = 0 },
                BeforeIsOffscreen = before.Element.IsOffscreen,
                BeforeBounds = before.Element.Bounds with { X = 0, Y = 0 },
                Scroll = scroll,
                AfterIsOffscreen = after.Element.IsOffscreen,
                AfterBounds = after.Element.Bounds with { X = 0, Y = 0 },
                Click = click,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
