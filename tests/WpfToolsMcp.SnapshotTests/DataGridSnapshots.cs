using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DataGridSnapshots
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

    private async Task LaunchDataGridAppAsync()
    {
        var exePath = TestAppPaths.FindDataGridTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;

        _ = await WaitForElementAsync(new Dictionary<string, object?>
        {
            ["automationId"] = "DataGrid_PeopleGrid"
        }, attempts: 60, delayMs: 100);
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

    private async Task<GetElementPropertiesResponse> WaitForElementAsync(
        Dictionary<string, object?> locator,
        int attempts = 20,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                return await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = locator
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Locator did not match any element", StringComparison.Ordinal))
            {
                await Task.Delay(delayMs);
            }
        }

        throw new TimeoutException("Element did not appear within timeout.");
    }

    private async Task<string?> WaitForStatusContainsAsync(
        string needle,
        int attempts = 30,
        int delayMs = 75)
    {
        for (var i = 0; i < attempts; i++)
        {
            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_SelectedStatus"
                }
            });

            if (status.Element.Name?.Contains(needle, StringComparison.Ordinal) == true)
            {
                return status.Element.Name;
            }

            await Task.Delay(delayMs);
        }

        return null;
    }

    [Test]
    public async Task DataGrid_editing_name_cell_updates_selected_status_snapshot()
    {
        await LaunchDataGridAppAsync();
        try
        {
            var nameCellLocator = new Dictionary<string, object?>
            {
                ["automationId"] = "DataGrid_Row_0_NameCell"
            };

            var selectCell = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = nameCellLocator,
                ["clickMode"] = "mouseAlways"
            });

            var statusBefore = await WaitForStatusContainsAsync("Alice") ??
                               (await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_SelectedStatus"
                }
            })).Element.Name;

            ClickElementResponse enterEdit = null!;
            for (var i = 0; i < 3; i++)
            {
                _ = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = nameCellLocator,
                    ["clickMode"] = "mouseAlways"
                });

                enterEdit = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = nameCellLocator,
                    ["clickType"] = "double",
                    ["clickMode"] = "mouseAlways"
                });

                try
                {
                    _ = await WaitForElementAsync(new Dictionary<string, object?>
                    {
                        ["automationId"] = "DataGrid_NameEditor"
                    }, attempts: 40, delayMs: 100);

                    break;
                }
                catch (TimeoutException) when (i < 2)
                {
                    await Task.Delay(100);
                }
            }

            var typed = await _mcp.CallToolAsync<TypeTextResponse>("type_text", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_NameEditor"
                },
                ["text"] = "Alicia"
            });

            GetElementPropertiesResponse statusAfter = null!;
            for (var i = 0; i < 20; i++)
            {
                statusAfter = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = "DataGrid_SelectedStatus"
                    }
                });

                if (statusAfter.Element.Name?.Contains("Alicia", StringComparison.Ordinal) == true)
                {
                    break;
                }

                await Task.Delay(75);
            }

            await Verifier.Verify(new
            {
                SelectCell = selectCell,
                StatusBefore = statusBefore,
                EnterEdit = enterEdit,
                Typed = typed,
                StatusAfter = statusAfter.Element.Name
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
