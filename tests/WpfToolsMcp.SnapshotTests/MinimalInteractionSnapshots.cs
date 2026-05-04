using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class MinimalInteractionSnapshots
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

    private async Task LaunchMinimalAppAsync()
    {
        var exePath = TestAppPaths.FindMinimalTestAppExecutable();
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
    public async Task ClickElement_name_ambiguous_returns_error_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            InvalidOperationException? ex = null;
            try
            {
                _ = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["name"] = "OK"
                    }
                });
                Assert.Fail("Expected click_element to fail due to ambiguous name 'OK'.");
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
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task ClickElement_name_with_index_updates_click_count_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "OK",
                    ["index"] = 0
                }
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "Clicks: 1"
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
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task ClickElement_name_strict_false_picks_first_match_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "OK",
                    ["strict"] = false
                }
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["nameContains"] = "Clicks:"
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
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task WaitFor_visible_succeeds_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["nameContains"] = "Clicks:"
                },
                ["state"] = "visible",
                ["timeoutMs"] = 0,
            });

            var stable = result with
            {
                ElapsedMs = -1,
                Attempts = -1,
                LastObservation = result.LastObservation is null
                    ? null
                    : result.LastObservation with
                    {
                        Bounds = result.LastObservation.Bounds is null
                            ? null
                            : result.LastObservation.Bounds with { X = 0, Y = 0 }
                    }
            };

            await Verifier.Verify(stable);
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task WaitFor_name_contains_timeout_returns_response_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<WaitForResponse>("wait_for", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["nameContains"] = "Clicks:"
                },
                ["state"] = "name_contains",
                ["expectedText"] = "Clicks: 999",
                ["timeoutMs"] = 0,
                ["throwOnTimeout"] = false,
            });

            var stable = result with
            {
                ElapsedMs = -1,
                Attempts = -1,
                LastObservation = result.LastObservation is null
                    ? null
                    : result.LastObservation with
                    {
                        Bounds = result.LastObservation.Bounds is null
                            ? null
                            : result.LastObservation.Bounds with { X = 0, Y = 0 }
                    }
            };

            await Verifier.Verify(stable);
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task SelectItem_listbox_by_text_updates_status_snapshot()
    {
        await LaunchMinimalAppAsync();
        try
        {
            var selected = await _mcp.CallToolAsync<SelectItemResponse>("select_item", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["className"] = "ListBox"
                },
                ["text"] = "Item 10"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "Selected: Item 10"
                }
            });

            await Verifier.Verify(new
            {
                Selected = selected,
                Status = status.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
