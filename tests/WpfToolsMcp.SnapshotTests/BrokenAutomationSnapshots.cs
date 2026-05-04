using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class BrokenAutomationSnapshots
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

    private async Task LaunchBrokenAppAsync()
    {
        var exePath = TestAppPaths.FindBrokenAutomationTestAppExecutable();
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
    public async Task NoPeerControl_not_visible_in_uia_tree()
    {
        await LaunchBrokenAppAsync();
        try
        {
            var tree = await _mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "uia",
                ["depth"] = 8,
                ["fields"] = new[] { "className" }
            });

            static IEnumerable<TreeNode> Enumerate(TreeNode node)
            {
                yield return node;
                foreach (var child in node.Children)
                {
                    foreach (var sub in Enumerate(child))
                    {
                        yield return sub;
                    }
                }
            }

            var any = Enumerate(tree.Root).Any(n => string.Equals(n.ClassName, "NoPeerControl", StringComparison.Ordinal));
            Assert.That(any, Is.False, "Expected NoPeerControl to be absent from the UIA tree.");
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task ClickElement_no_peer_control_uses_wpf_locator_snapshot()
    {
        await LaunchBrokenAppAsync();
        try
        {
            var click = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["classNameContains"] = "NoPeerControl"
                },
                ["timeoutMs"] = 250,
                ["pollIntervalMs"] = 50,
            });

            await Verifier.Verify(click);
        }
        finally
        {
            await CloseAppAsync();
        }
    }
}
