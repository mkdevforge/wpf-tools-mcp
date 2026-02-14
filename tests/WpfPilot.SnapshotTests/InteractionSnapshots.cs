using System.Threading;
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
}
