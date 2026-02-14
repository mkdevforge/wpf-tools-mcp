using System.Threading;
using System.Text.Json.Nodes;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class InteractionSnapshots
{
    private McpTestContext _mcp = null!;

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

    private async Task LaunchTestAppAsync()
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        _ = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });
    }

    private async Task CloseTestAppAsync()
    {
        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_app", new Dictionary<string, object?>
            {
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }
    }

    [Test]
    public async Task FocusWindow_main_window_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<FocusWindowResponse>("focus_window");
            await Verifier.Verify(result with { Handle = 0 });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task ClickElement_basic_button_updates_status_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                },
                ["clickMode"] = "mouseAlways"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_ClickStatus"
                }
            });

            await Verifier.Verify(new
            {
                Click = click,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Invoke_basic_button_updates_status_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var invoke = await _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                }
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_ClickStatus"
                }
            });

            await Verifier.Verify(new
            {
                Invoke = invoke,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Invoke_on_non_invokable_element_returns_error_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            InvalidOperationException? ex = null;
            try
            {
                _ = await _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
                {
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = "Basic_Slider"
                    }
                });
                Assert.Fail("Expected invoke to fail for Basic_Slider.");
            }
            catch (InvalidOperationException caught)
            {
                ex = caught;
            }

            var message = ex!.Message.Split("--- server stderr", StringSplitOptions.None)[0].TrimEnd();
            await Verifier.Verify(message);
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task TypeText_textbox_updates_value_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var typed = await _mcp.CallToolAsync<TypeTextResponse>("type_text", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_TextBox"
                },
                ["text"] = "Hello from tests"
            });

            var textbox = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_TextBox"
                }
            });

            var valuePatternValue = GetPatternValue(textbox, "Value", "Value")?.GetValue<string>();

            await Verifier.Verify(new
            {
                Typed = typed,
                Value = valuePatternValue
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task SetValue_slider_updates_value_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var set = await _mcp.CallToolAsync<SetValueResponse>("set_value", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Slider"
                },
                ["value"] = 70
            });

            var slider = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Slider"
                }
            });

            var rangeValue = GetPatternValue(slider, "RangeValue", "Value")?.GetValue<double>();

            await Verifier.Verify(new
            {
                Set = set,
                Value = rangeValue
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task SelectItem_combobox_by_text_updates_value_snapshot()
    {
        await LaunchTestAppAsync();
        try
        {
            var selected = await _mcp.CallToolAsync<SelectItemResponse>("select_item", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_ComboBox"
                },
                ["text"] = "Two"
            });

            var comboBox = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_ComboBox"
                }
            });

            var valuePatternValue = GetPatternValue(comboBox, "Value", "Value")?.GetValue<string>();

            await Verifier.Verify(new
            {
                Selected = selected,
                Value = valuePatternValue
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    private static JsonNode? GetPatternValue(GetElementPropertiesResponse response, string patternName, string valueName)
    {
        if (!response.Patterns.TryGetValue(patternName, out var patternNode))
        {
            return null;
        }

        if (patternNode is not JsonObject obj)
        {
            return null;
        }

        if (obj["values"] is not JsonObject values)
        {
            return null;
        }

        return values[valueName];
    }
}
