using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DataGridSnapshots
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

    private async Task LaunchDataGridAppAsync()
    {
        var exePath = TestAppPaths.FindDataGridTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        _ = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });
    }

    private async Task CloseAppAsync()
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
                    ["locator"] = locator
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Locator did not match any element", StringComparison.Ordinal))
            {
                await Task.Delay(delayMs);
            }
        }

        Assert.Fail("Element did not appear within timeout.");
        throw new AssertionException("Unreachable.");
    }

    [Test]
    public async Task DataGrid_editing_name_cell_updates_selected_status_snapshot()
    {
        await LaunchDataGridAppAsync();
        try
        {
            var selectCell = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_Row_0_NameCell"
                },
                ["clickMode"] = "mouseAlways"
            });

            var statusBefore = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_SelectedStatus"
                }
            });

            var enterEdit = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "DataGrid_Row_0_NameCell"
                },
                ["clickType"] = "double",
                ["clickMode"] = "mouseAlways"
            });

            _ = await WaitForElementAsync(new Dictionary<string, object?>
            {
                ["automationId"] = "DataGrid_NameEditor"
            });

            var typed = await _mcp.CallToolAsync<TypeTextResponse>("type_text", new Dictionary<string, object?>
            {
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
                StatusBefore = statusBefore.Element.Name,
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

