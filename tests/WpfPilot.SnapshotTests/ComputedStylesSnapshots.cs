using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ComputedStylesSnapshots
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
        }
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
    public async Task GetComputedProperties_selected_properties_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetComputedPropertiesResponse>("get_computed_properties", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Button"
            },
            ["propertyNames"] = new[] { "Width", "HorizontalAlignment", "Margin" },
            ["includeSources"] = true
        });

        var stable = new
        {
            Element = ScrubElementRef(result.Element),
            Properties = result.Properties.Select(p => new
            {
                p.Name,
                p.OwnerType,
                p.Value,
                p.ValueSource,
                p.BindingKind,
                p.Path
            }).ToArray(),
            result.Truncated,
            result.MissingPropertyNames
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task GetStyleChain_button_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetStyleChainResponse>("get_style_chain", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Button"
            },
            ["includeThemeStyle"] = true,
            ["includeResourceKeys"] = false,
            ["maxBasedOnDepth"] = 5
        });

        var stable = new
        {
            Element = ScrubElementRef(result.Element),
            Styles = result.Styles.Select(s => new
            {
                s.Kind,
                s.TargetType,
                BasedOnCount = s.BasedOnChainTargetTypes.Count,
                s.StylePropertyValueSource,
                s.SettersCount,
                s.TriggersCount
            }).ToArray()
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task GetTemplateInfo_slider_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetTemplateInfoResponse>("get_template_info", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Slider"
            },
            ["includeResourceKeys"] = false
        });

        var stable = new
        {
            Element = ScrubElementRef(result.Element),
            Template = new
            {
                result.Template.Kind,
                result.Template.TemplateType,
                result.Template.TargetType,
                result.Template.TriggersCount,
                TemplateParts = result.Template.TemplateParts?.Select(p => new
                {
                    p.Name,
                    p.ExpectedType,
                    p.Found,
                    p.ActualType
                }).ToArray()
            }
        };

        await Verifier.Verify(stable);
    }

    [Test]
    public async Task GetTemplateInfo_slider_with_part_refs_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetTemplateInfoResponse>("get_template_info", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Slider"
            },
            ["includeResourceKeys"] = false,
            ["includePartElementRefs"] = true
        });

        var stable = new
        {
            Element = ScrubElementRef(result.Element),
            Template = new
            {
                result.Template.Kind,
                result.Template.TemplateType,
                result.Template.TargetType,
                result.Template.TriggersCount,
                TemplateParts = result.Template.TemplateParts?.Select(p => new
                {
                    p.Name,
                    p.ExpectedType,
                    p.Found,
                    p.ActualType,
                    HasXPath = !string.IsNullOrWhiteSpace(p.XPath),
                    HasBounds = p.Bounds is { Width: > 0, Height: > 0 }
                }).ToArray()
            },
            result.Warnings
        };

        await Verifier.Verify(stable);
    }

    private static ElementRef ScrubElementRef(ElementRef element) =>
        element with
        {
            Bounds = element.Bounds is { } bounds ? bounds with { X = 0, Y = 0 } : null
        };

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
