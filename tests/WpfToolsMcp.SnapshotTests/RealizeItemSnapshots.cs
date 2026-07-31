using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class RealizeItemSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is not null)
        {
            await _mcp.DisposeAsync();
        }
    }

    [Test]
    public async Task Provider_order_index_distinguishes_already_realized_and_virtualized_items()
    {
        await LaunchTestAppAsync();
        try
        {
            var first = await RealizeByIndexAsync(0);
            var middle = await RealizeByIndexAsync(150, maxProviderCalls: 200);

            Assert.Multiple(() =>
            {
                Assert.That(first.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodAlreadyRealized));
                Assert.That(first.RealizeInvoked, Is.False);
                Assert.That(first.PostconditionVerified, Is.True);
                Assert.That(first.FindItemByPropertyCalls, Is.EqualTo(1));
                Assert.That(first.Reusable, Is.True);
                Assert.That(first.Element?.ElementId, Does.StartWith("uia_"));

                Assert.That(middle.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodVirtualizedItemRealize));
                Assert.That(middle.RealizeInvoked, Is.True);
                Assert.That(middle.PostconditionVerified, Is.True);
                Assert.That(middle.FindItemByPropertyCalls, Is.EqualTo(151));
                Assert.That(middle.StopReason, Is.EqualTo(RealizeItemOutcomes.StopCompleted));
                Assert.That(middle.ViewportMayHaveChanged, Is.True);
                Assert.That(middle.DataOrContainerLoadingMayHaveOccurred, Is.True);
                Assert.That(middle.Reusable, Is.True);
                Assert.That(middle.Element?.Name, Is.EqualTo("Virtual item 150"));
                Assert.That(middle.Element?.ElementId, Does.StartWith("uia_"));
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Provider_observed_duplicate_exact_name_is_ambiguous_without_mutation()
    {
        await LaunchTestAppAsync();
        try
        {
            var response = await _mcp.CallToolAsync<RealizeItemResponse>(
                "realize_item",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["containerLocator"] = ContainerLocator(),
                    ["name"] = "Duplicate target",
                    ["maxProviderCalls"] = 10,
                    ["advisoryElapsedLimitMs"] = 10_000
                });

            Assert.Multiple(() =>
            {
                Assert.That(response.RequestedIdentity.Name, Is.EqualTo("Duplicate target"));
                Assert.That(response.StopReason, Is.EqualTo(RealizeItemOutcomes.StopAmbiguous));
                Assert.That(response.MethodUsed, Is.EqualTo(RealizeItemOutcomes.MethodNone));
                Assert.That(response.FindItemByPropertyCalls, Is.EqualTo(2));
                Assert.That(response.RealizeInvoked, Is.False);
                Assert.That(response.PostconditionVerified, Is.False);
                Assert.That(response.Reusable, Is.False);
                Assert.That(response.Element, Is.Null);
            });
        }
        finally
        {
            await CloseTestAppAsync();
        }
    }

    [Test]
    public async Task Fresh_handle_composes_with_existing_tools_and_recycled_identity_fails_closed()
    {
        await LaunchTestAppAsync();
        var screenshotPath = Path.Combine(Path.GetTempPath(), $"wpf-realized-item-{Guid.NewGuid():N}.png");
        try
        {
            var realized = await RealizeByIndexAsync(150, maxProviderCalls: 200);
            var elementId = realized.Element?.ElementId;
            Assert.That(realized.Reusable, Is.True);
            Assert.That(elementId, Does.StartWith("uia_"));

            var inspected = await _mcp.CallToolAsync<GetElementPropertiesResponse>(
                "get_element_properties",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["elementId"] = elementId,
                    ["backend"] = InspectionBackend.Uia
                });
            var selected = await _mcp.CallToolAsync<SelectItemResponse>(
                "select_item",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["locator"] = ContainerLocator(),
                    ["itemElementId"] = elementId,
                    ["timeoutMs"] = 2_000
                });
            var scrolled = await _mcp.CallToolAsync<ScrollToElementResponse>(
                "scroll_to_element",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["elementId"] = elementId,
                    ["containerLocator"] = ContainerLocator(),
                    ["timeoutMs"] = 2_000
                });
            var screenshot = await _mcp.CallToolAsync<TakeScreenshotResponse>(
                "take_screenshot",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["elementId"] = elementId,
                    ["backend"] = InspectionBackend.Uia,
                    ["outputPath"] = screenshotPath
                });

            _ = await RealizeByIndexAsync(250, maxProviderCalls: 300);
            var staleResult = await _mcp.CallToolResultAsync(
                "scroll_to_element",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["elementId"] = elementId,
                    ["containerLocator"] = ContainerLocator(),
                    ["timeoutMs"] = 500
                });
            var staleCode = staleResult.StructuredContent is { } content
                ? content.GetProperty("error").GetProperty("code").GetString()
                : null;

            Assert.Multiple(() =>
            {
                Assert.That(inspected.Element.Name, Is.EqualTo("Virtual item 150"));
                Assert.That(selected.Selected, Is.True);
                Assert.That(scrolled.MethodUsed, Is.Not.Empty);
                Assert.That(screenshot.Width, Is.GreaterThan(0));
                Assert.That(screenshot.Height, Is.GreaterThan(0));
                Assert.That(File.Exists(screenshotPath), Is.True);
                Assert.That(staleResult.IsError, Is.True);
                Assert.That(staleCode, Is.EqualTo("stale_element"));
            });
        }
        finally
        {
            TryDelete(screenshotPath);
            await CloseTestAppAsync();
        }
    }

    private async Task<RealizeItemResponse> RealizeByIndexAsync(int index, int? maxProviderCalls = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["containerLocator"] = ContainerLocator(),
            ["index"] = index,
            ["advisoryElapsedLimitMs"] = 20_000
        };
        if (maxProviderCalls is int calls)
        {
            arguments["maxProviderCalls"] = calls;
        }

        return await _mcp.CallToolAsync<RealizeItemResponse>("realize_item", arguments);
    }

    private static Dictionary<string, object?> ContainerLocator() =>
        new() { ["automationId"] = "VirtualizedItems_List" };

    private async Task LaunchTestAppAsync()
    {
        var exePath = TestAppPaths.FindVirtualizedItemsTestAppExecutable();
        var launch = await _mcp.CallToolAsync<LaunchAppResponse>(
            "launch_app",
            new Dictionary<string, object?>
            {
                ["exePath"] = exePath,
                ["workingDirectory"] = Path.GetDirectoryName(exePath)!
            });
        _sessionId = launch.SessionId;
    }

    private async Task CloseTestAppAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>(
                "close_session",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["force"] = true,
                    ["timeoutMs"] = 2_000
                });
        }
        catch
        {
        }
        finally
        {
            _sessionId = string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
