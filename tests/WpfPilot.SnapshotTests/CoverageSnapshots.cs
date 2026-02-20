using NUnit.Framework;
using VerifyNUnit;
using WpfPilot.Contracts;

namespace WpfPilot.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class CoverageSnapshots
{
    private McpTestContext _mcp = null!;
    private string _sessionId = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe);

        var exePath = TestAppPaths.FindBrokenAutomationTestAppExecutable();
        var workingDirectory = Path.GetDirectoryName(exePath)!;

        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = workingDirectory,
        });

        _sessionId = launch.SessionId;

        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId
            });
        }
        catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
        {
            Assert.Ignore(ex.Message);
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

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

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task UiaCoverageReport_broken_automation_peer_snapshot()
    {
        var result = await _mcp.CallToolAsync<GetUiaCoverageReportResponse>("uia_coverage_report", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["interactiveOnly"] = false,
            ["maxNodes"] = 3000,
            ["maxFindings"] = 50
        });

        Assert.That(result.Findings.Any(f => f.IssueCode == "no_automation_peer"), Is.True);

        var stable = new
        {
            result.Summary.Truncated,
            result.Summary.TruncatedReason,
            Findings = result.Findings
                .Select(f => new
                {
                    f.IssueCode,
                    f.Severity,
                    Element = new
                    {
                        f.Element.Type,
                        f.Element.AutomationId,
                        f.Element.Name
                    },
                    SuggestionsCount = f.Suggestions.Count
                })
                .OrderBy(f => f.IssueCode, StringComparer.Ordinal)
                .ThenBy(f => f.Element.Type, StringComparer.Ordinal)
                .ToArray()
        };

        await Verifier.Verify(stable);
    }

    private static bool ShouldSkipForMissingAssets(InvalidOperationException ex)
    {
        var message = ex.Message;
        return message.Contains("Phase 2 agent payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 Snoop payload directory not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Phase 2 agent assembly not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop injector launcher not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Snoop generic injector not found", StringComparison.OrdinalIgnoreCase);
    }
}
