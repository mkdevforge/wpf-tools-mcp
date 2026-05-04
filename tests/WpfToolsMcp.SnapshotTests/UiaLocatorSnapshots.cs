using System.Threading;
using NUnit.Framework;
using VerifyNUnit;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class UiaLocatorSnapshots
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

    [Test]
    public async Task GetUiaLocators_automation_id_control_snapshot()
    {
        await LaunchPrimaryTestAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                }
            });

            await Verifier.Verify(Scrub(result));
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaLocators_name_only_text_snapshot()
    {
        await LaunchPrimaryTestAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["name"] = "TextBox:"
                }
            });

            await Verifier.Verify(Scrub(result));
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaLocators_templated_wpf_button_snapshot()
    {
        await LaunchCustomControlsAppAsync();
        try
        {
            var button = await FindSingleWpfElementAsync("Custom_TemplatedButton");

            var result = await _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = button.ElementId
            });

            await Verifier.Verify(Scrub(result));
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaTree_primary_window_snapshot()
    {
        await LaunchPrimaryTestAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<GetUiaTreeResponse>("get_uia_tree", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["depth"] = 2,
                ["maxNodes"] = 20,
                ["visibleOnly"] = true,
                ["includeOffViewport"] = true
            });

            await Verifier.Verify(result);
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    private async Task LaunchPrimaryTestAppAsync()
    {
        var exePath = TestAppPaths.FindTestAppExecutable();
        await LaunchAppAsync(exePath);
    }

    private async Task LaunchCustomControlsAppAsync()
    {
        var exePath = TestAppPaths.FindCustomControlsTestAppExecutable();
        await LaunchAppAsync(exePath);
    }

    private async Task LaunchAppAsync(string exePath)
    {
        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = Path.GetDirectoryName(exePath)!
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

    private async Task<ElementRef> FindSingleWpfElementAsync(string automationId)
    {
        var matches = await _mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "wpf",
            ["query"] = new Dictionary<string, object?>
            {
                ["automationIdEquals"] = automationId
            },
            ["maxResults"] = 3,
            ["returnFields"] = "standard"
        });

        Assert.That(matches.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
        Assert.That(matches.ReturnedMatches, Is.EqualTo(1));
        Assert.That(matches.Matches[0].ElementId, Does.StartWith("wpf_"));
        return matches.Matches[0];
    }

    private static GetUiaLocatorsResponse Scrub(GetUiaLocatorsResponse response) =>
        response with
        {
            Wpf = response.Wpf is null
                ? null
                : response.Wpf with
                {
                    ElementId = response.Wpf.ElementId is null ? null : "<element>",
                    ClassName = string.IsNullOrWhiteSpace(response.Wpf.ClassName) ? null : "<class>"
                },
            Uia = response.Uia with
            {
                Bounds = new Rect(0, 0, 0, 0),
                ClassName = string.IsNullOrWhiteSpace(response.Uia.ClassName) ? null : response.Uia.ClassName
            },
            UiaMapping = ScrubUiaMapping(response.UiaMapping)
        };

    private static UiaMappingDiagnostics? ScrubUiaMapping(UiaMappingDiagnostics? mapping) =>
        mapping is null
            ? null
            : mapping with
            {
                Candidates = mapping.Candidates
                    .Select(candidate => candidate with
                    {
                        Bounds = new Rect(0, 0, 0, 0)
                    })
                    .ToArray()
            };
}
