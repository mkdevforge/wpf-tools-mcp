using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DynamicContentSnapshots
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

    private async Task LaunchDynamicContentAppAsync()
    {
        var exePath = TestAppPaths.FindDynamicContentTestAppExecutable();
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

    private async Task<GetElementPropertiesResponse> WaitForElementAsync(
        Dictionary<string, object?> locator,
        int attempts = 25,
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

        Assert.Fail("Element did not appear within timeout.");
        throw new AssertionException("Unreachable.");
    }

    [Test]
    public async Task Dynamic_button_can_be_added_and_clicked_snapshot()
    {
        await LaunchDynamicContentAppAsync();
        try
        {
            var add = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_AddButton"
                }
            });

            _ = await WaitForElementAsync(new Dictionary<string, object?>
            {
                ["automationId"] = "Dynamic_NewButton"
            });

            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_NewButton"
                },
                ["clickMode"] = "mouseAlways"
            });

            var status = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Dynamic_Status"
                }
            });

            await Verifier.Verify(new
            {
                Add = add,
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
