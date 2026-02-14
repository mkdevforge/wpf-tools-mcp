using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using VerifyTests;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class BrokenAutomationSnapshots
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

    private async Task LaunchBrokenAppAsync()
    {
        var exePath = TestAppPaths.FindBrokenAutomationTestAppExecutable();
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

    [Test]
    public async Task NoPeerControl_not_visible_in_uia_tree()
    {
        await LaunchBrokenAppAsync();
        try
        {
            var tree = await _mcp.CallToolAsync<GetVisualTreeResponse>("get_visual_tree", new Dictionary<string, object?>
            {
                ["depth"] = 8
            });

            static IEnumerable<VisualTreeNode> Enumerate(VisualTreeNode node)
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
    public async Task ClickElement_no_peer_control_returns_error_snapshot()
    {
        await LaunchBrokenAppAsync();
        try
        {
            InvalidOperationException? ex = null;
            try
            {
                _ = await _mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
                {
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["className"] = "NoPeerControl"
                    }
                });
                Assert.Fail("Expected click_element to fail because NoPeerControl is not exposed via UIA.");
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
}

