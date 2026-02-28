using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class PickerHighlightSnapshots
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
    public async Task PickElementAtPoint_uia_snapshot()
    {
        var screenshot = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["captureMode"] = "auto"
        });

        var pick = await _mcp.CallToolAsync<PickElementAtPointResponse>("pick_element_at_point", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "uia",
            ["x"] = screenshot.CapturedBounds.X + screenshot.CapturedBounds.Width - 10,
            ["y"] = screenshot.CapturedBounds.Y + screenshot.CapturedBounds.Height - 10,
            ["includeAncestors"] = true,
            ["maxAncestors"] = 8
        });

        var stable = new
        {
            Pick = ScrubPickResponse(pick)
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task PickElementAtPoint_wpf_snapshot()
    {
        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
            return;
        }

        var screenshot = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["captureMode"] = "auto"
        });

        var pick = await _mcp.CallToolAsync<PickElementAtPointResponse>("pick_element_at_point", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "wpf",
            ["x"] = screenshot.CapturedBounds.X + screenshot.CapturedBounds.Width - 10,
            ["y"] = screenshot.CapturedBounds.Y + screenshot.CapturedBounds.Height - 10,
            ["includeAncestors"] = true,
            ["maxAncestors"] = 8
        });

        var stable = new
        {
            Pick = ScrubPickResponse(pick)
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task PickElementAtPoint_wpf_promotes_content_to_framework_element_snapshot()
    {
        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
            return;
        }

        var hyperlinkTextBlock = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_HyperlinkTextBlock"
            }
        });

        var bounds = hyperlinkTextBlock.Element.Bounds;
        var x = bounds.X + Math.Max(1, bounds.Width / 2);
        var y = bounds.Y + Math.Max(1, bounds.Height / 2);

        var pick = await _mcp.CallToolAsync<PickElementAtPointResponse>("pick_element_at_point", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "wpf",
            ["x"] = x,
            ["y"] = y,
            ["includeAncestors"] = true,
            ["maxAncestors"] = 8
        });

        Assert.That(pick.Element.Type, Is.EqualTo("TextBlock"), "WPF picker should promote content hits to a FrameworkElement.");

        var stable = new
        {
            HyperlinkTextBlock = hyperlinkTextBlock.Element with { Bounds = bounds with { X = 0, Y = 0 } },
            Pick = ScrubPickResponse(pick)
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task HighlightElement_by_locator_snapshot()
    {
        var result = await _mcp.CallToolAsync<HighlightElementResponse>("highlight_element", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "uia",
            ["durationMs"] = 250,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Button"
            }
        });

        var stable = result with { Bounds = result.Bounds with { X = 0, Y = 0 } };
        await Verifier.Verify(stable);
    }

    [Test]
    public async Task HighlightElement_uia_elementId_prefers_inproc_when_agent_available()
    {
        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
            return;
        }

        var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "uia",
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Button"
            }
        });

        Assert.That(resolved.Element.ElementId, Is.Not.Null.And.Not.Empty, "Expected a UIA elementId from resolve_element.");

        var result = await _mcp.CallToolAsync<HighlightElementResponse>("highlight_element", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["elementId"] = resolved.Element.ElementId,
            ["durationMs"] = 250,
            ["preferInProcHighlight"] = true
        });

        Assert.That(result.Highlighted, Is.True, result.Error ?? result.Reason ?? "Highlight failed.");
        Assert.That(result.MethodUsed, Is.EqualTo("wpf_agent_mapped"));
    }

    private static PickElementAtPointResponse ScrubPickResponse(PickElementAtPointResponse response)
    {
        var element = response.Element with
        {
            ElementId = "<element>",
            Bounds = response.Element.Bounds is { } bounds ? bounds with { X = 0, Y = 0 } : null
        };

        IReadOnlyList<ElementRef>? ancestors = null;
        if (response.Ancestors is { Count: > 0 })
        {
            ancestors = response.Ancestors
                .Select((a, i) => a with { ElementId = $"<ancestor:{i}>" })
                .ToArray();
        }

        return response with
        {
            WindowHandleUsed = 0,
            XScreen = 0,
            YScreen = 0,
            Element = element,
            Ancestors = ancestors
        };
    }

    private static bool ShouldSkipForMissingAssets(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }
}
