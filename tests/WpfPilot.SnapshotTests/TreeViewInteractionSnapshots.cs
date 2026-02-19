using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class TreeViewInteractionSnapshots
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

    private async Task LaunchTreeAppAsync()
    {
        var exePath = TestAppPaths.FindTreeViewTestAppExecutable();
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
    public async Task SelectItem_treeview_by_text_updates_status_snapshot()
    {
        await LaunchTreeAppAsync();
        try
        {
            var selected = await _mcp.CallToolAsync<SelectItemResponse>("select_item", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Tree_TreeView"
                },
                ["text"] = "Node 2.1"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Tree_SelectedStatus"
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

    [Test]
    public async Task SelectItem_treeview_by_item_locator_updates_status_snapshot()
    {
        await LaunchTreeAppAsync();
        try
        {
            var selected = await _mcp.CallToolAsync<SelectItemResponse>("select_item", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Tree_TreeView"
                },
                ["itemLocator"] = new Dictionary<string, object?>
                {
                    ["name"] = "Node 3.2"
                }
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Tree_SelectedStatus"
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
