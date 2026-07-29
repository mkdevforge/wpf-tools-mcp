using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class DiagnosticSnapshotUiaIntegrationTests
{
    [Test]
    public async Task Snapshot_fails_sections_instead_of_retargeting_a_shifted_uia_handle()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe);

        var appPath = TestAppPaths.FindDynamicContentTestAppExecutable();
        var launch = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = appPath,
            ["workingDirectory"] = Path.GetDirectoryName(appPath)
        });

        try
        {
            var target = await ResolveUiaAsync(mcp, launch.SessionId, "Dynamic_Status");
            var insert = await ResolveUiaAsync(mcp, launch.SessionId, "Dynamic_InsertSiblingBeforeStatus");
            _ = await mcp.CallToolAsync<ClickElementResponse>("click_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["elementId"] = insert.Element.ElementId
            });
            var replacement = await ResolveUiaAsync(mcp, launch.SessionId, "Dynamic_InsertedSibling");
            var shiftedTarget = await ResolveUiaAsync(mcp, launch.SessionId, "Dynamic_Status");
            Assert.Multiple(() =>
            {
                Assert.That(
                    replacement.Element.XPath == target.Element.XPath ||
                    replacement.Element.XPath.StartsWith(target.Element.XPath + "[", StringComparison.Ordinal),
                    Is.True);
                Assert.That(shiftedTarget.Element.XPath, Is.Not.EqualTo(target.Element.XPath));
            });

            var snapshot = await mcp.CallToolAsync<CaptureDiagnosticSnapshotResponse>(
                "capture_diagnostic_snapshot",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["elementId"] = target.Element.ElementId,
                    ["sections"] = new[] { "UiaProperties", "VisualTree" }
                });
            var sectionSummary = string.Join(
                "; ",
                snapshot.Sections.Select(section =>
                    $"{section.Section}:{section.Status}:{section.Code}:{section.Message}:{section.Data}"));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Target.Element.ElementId, Is.EqualTo(target.Element.ElementId));
                Assert.That(snapshot.Sections, Has.Count.EqualTo(2));
                Assert.That(
                    snapshot.Sections.All(section => section.Status == DiagnosticSectionStatus.Failed),
                    Is.True,
                    sectionSummary);
                Assert.That(snapshot.Sections.All(section => section.Data is null), Is.True, sectionSummary);
                Assert.That(snapshot.Sections.All(section => section.Message!.Contains(
                    "identity_changed",
                    StringComparison.Ordinal)), Is.True);
            });
        }
        finally
        {
            try
            {
                _ = await mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["force"] = true,
                    ["timeoutMs"] = 2000
                });
            }
            catch
            {
            }
        }
    }

    private static Task<ResolveElementResponse> ResolveUiaAsync(
        McpTestContext mcp,
        string sessionId,
        string automationId) =>
        mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["backend"] = "uia",
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = automationId
            },
            ["visibleOnly"] = false,
            ["includeOffViewport"] = true
        });
}
