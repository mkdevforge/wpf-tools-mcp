using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DeeplyNestedSnapshots
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

    private async Task LaunchDeeplyNestedAppAsync()
    {
        var exePath = TestAppPaths.FindDeeplyNestedTestAppExecutable();
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
    public async Task XPath_roundtrip_for_deep_target_snapshot()
    {
        await LaunchDeeplyNestedAppAsync();
        try
        {
            var target = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Nested_TargetButton"
                }
            });

            var xpath = target.Element.XPath;

            var resolved = await _mcp.CallToolAsync<GetElementPropertiesResponse>("get_element_properties", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["xpath"] = xpath
                }
            });

            await Verifier.Verify(new
            {
                Target = target.Element with { Bounds = target.Element.Bounds with { X = 0, Y = 0 } },
                XPath = xpath,
                SegmentCount = xpath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length,
                Resolved = resolved.Element with { Bounds = resolved.Element.Bounds with { X = 0, Y = 0 } }
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
