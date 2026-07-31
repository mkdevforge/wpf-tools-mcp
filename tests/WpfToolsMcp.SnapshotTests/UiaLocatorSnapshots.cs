using System.Threading;
using FlaUI.UIA3;
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
    private int _pid;

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

            Assert.Multiple(() =>
            {
                Assert.That(result.Uia?.ElementId, Does.StartWith("uia_"));
                Assert.That(result.Wpf?.ElementId, Does.StartWith("wpf_"));
                Assert.That(result.WpfMapping?.Available, Is.True);
                Assert.That(result.WpfMapping?.Status, Is.EqualTo(ElementMappingStatus.Exact));
                Assert.That(result.WpfMapping?.SelectedElementId, Is.EqualTo(result.Wpf?.ElementId));
                Assert.That(result.WpfMapping?.Candidates.Count, Is.LessThanOrEqualTo(10));
            });

            var roundTrip = await _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["elementId"] = result.Wpf!.ElementId
            });
            Assert.Multiple(() =>
            {
                Assert.That(roundTrip.UiaMapping?.Status, Is.EqualTo(ElementMappingStatus.Exact));
                Assert.That(roundTrip.Uia?.AutomationId, Is.EqualTo(result.Uia?.AutomationId));
                Assert.That(roundTrip.Uia?.ElementId, Does.StartWith("uia_"));
            });

            AssertFlaUiXPathResolves(result);
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

            Assert.Multiple(() =>
            {
                Assert.That(result.Uia?.ElementId, Does.StartWith("uia_"));
                Assert.That(result.WpfMapping?.Available, Is.True);
                Assert.That(result.WpfMapping?.Status, Is.EqualTo(ElementMappingStatus.Heuristic));
                Assert.That(result.Wpf?.ElementId, Does.StartWith("wpf_"));
            });

            AssertFlaUiXPathResolves(result);
            await Verifier.Verify(Scrub(result));
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaLocators_templated_wpf_button_returns_explained_reusable_mapping()
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

            Assert.That(result.Wpf, Is.Not.Null);
            Assert.That(result.Uia, Is.Not.Null);
            Assert.That(result.UiaMapping, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(result.Wpf!.ElementId, Does.StartWith("wpf_"));
                Assert.That(result.Wpf.Bounds, Is.Not.Null);
                Assert.That(result.Uia!.ElementId, Does.StartWith("uia_"));
                Assert.That(result.UiaMapping!.Status, Is.EqualTo(ElementMappingStatus.Exact));
                Assert.That(result.UiaMapping.Method, Is.EqualTo("scoredWindowScan"));
                Assert.That(result.UiaMapping.ScanComplete, Is.True);
                Assert.That(result.UiaMapping.SelectedElementId, Is.EqualTo(result.Uia.ElementId));
                Assert.That(result.UiaMapping.SelectedXPath, Is.EqualTo(result.Uia.UiaXPath));
                Assert.That(result.UiaMapping.Score, Is.GreaterThan(0));
                Assert.That(result.UiaMapping.Evidence, Does.Contain("runtime_identity_verified"));
                Assert.That(result.UiaMapping.Candidates[0].Reusable, Is.True);
                Assert.That(result.UiaMapping.Candidates[0].ElementId, Does.StartWith("uia_"));
            });
            AssertFlaUiXPathResolves(result);
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaLocators_explicit_wpf_locator_maps_builtin_control()
    {
        await LaunchPrimaryTestAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "wpf",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.Wpf?.ElementId, Does.StartWith("wpf_"));
                Assert.That(result.Wpf?.Bounds, Is.Not.Null);
                Assert.That(result.Uia?.ElementId, Does.StartWith("uia_"));
                Assert.That(result.UiaMapping?.Status, Is.EqualTo(ElementMappingStatus.Exact));
                Assert.That(result.UiaMapping?.ScanComplete, Is.True);
                Assert.That(result.UiaMapping?.Truncated, Is.False);
            });
            AssertFlaUiXPathResolves(result);
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaLocators_incomplete_wpf_scan_never_selects_a_candidate()
    {
        await LaunchPrimaryTestAppAsync();
        try
        {
            var result = await _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["backend"] = "wpf",
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Basic_Button"
                },
                ["maxNodes"] = 1
            });

            Assert.Multiple(() =>
            {
                Assert.That(result.Uia, Is.Null);
                Assert.That(result.LocatorSuggestions, Is.Null);
                Assert.That(result.FlaUi, Is.Null);
                Assert.That(result.UiaMapping?.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
                Assert.That(result.UiaMapping?.ScanComplete, Is.False);
                Assert.That(result.UiaMapping?.ScannedNodes, Is.EqualTo(1));
                Assert.That(result.UiaMapping?.SelectedXPath, Is.Null);
                Assert.That(result.UiaMapping?.SelectedElementId, Is.Null);
                Assert.That(result.UiaMapping?.TruncatedReason, Is.EqualTo("maxNodes"));
            });
        }
        finally
        {
            await CloseAppAsync();
        }
    }

    [Test]
    public async Task GetUiaLocators_path_work_shares_the_mapping_node_budget()
    {
        await LaunchPrimaryTestAppAsync();
        try
        {
            var baseline = await GetBuiltInWpfButtonMappingAsync();
            Assert.That(baseline.UiaMapping?.Status, Is.EqualTo(ElementMappingStatus.Exact));
            var boundedMaxNodes = baseline.UiaMapping!.ScannedNodes!.Value - 1;
            Assert.That(boundedMaxNodes, Is.GreaterThan(1));

            var bounded = await GetBuiltInWpfButtonMappingAsync(boundedMaxNodes);

            Assert.Multiple(() =>
            {
                Assert.That(bounded.Uia, Is.Null);
                Assert.That(bounded.UiaMapping?.Status, Is.EqualTo(ElementMappingStatus.Ambiguous));
                Assert.That(bounded.UiaMapping?.ScanComplete, Is.False);
                Assert.That(bounded.UiaMapping?.ScannedNodes, Is.EqualTo(boundedMaxNodes));
                Assert.That(bounded.UiaMapping?.SelectedElementId, Is.Null);
                Assert.That(bounded.UiaMapping?.TruncatedReason, Is.EqualTo("maxNodes"));
            });
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

            await Verifier.Verify(result with { WindowHandleUsed = 0 });
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

    private Task<GetUiaLocatorsResponse> GetBuiltInWpfButtonMappingAsync(int? maxNodes = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = "wpf",
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = "Basic_Button"
            }
        };
        if (maxNodes is int boundedMaxNodes)
        {
            arguments["maxNodes"] = boundedMaxNodes;
        }

        return _mcp.CallToolAsync<GetUiaLocatorsResponse>("get_uia_locators", arguments);
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
        _pid = launch.Pid;
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
            _pid = 0;
        }
    }

    private void AssertFlaUiXPathResolves(GetUiaLocatorsResponse result)
    {
        Assert.That(_pid, Is.GreaterThan(0));
        Assert.That(result.Uia, Is.Not.Null);
        Assert.That(result.LocatorSuggestions, Is.Not.Null);
        var uia = result.Uia!;
        var suggestions = result.LocatorSuggestions!;
        Assert.That(suggestions.ByFlaUiXPath, Is.Not.Null.And.Not.Empty);

        using var automation = new UIA3Automation();
        var app = FlaUI.Core.Application.Attach(_pid);
        var window = app.GetMainWindow(automation);
        Assert.That(window, Is.Not.Null);

        var found = window!.FindFirstByXPath(suggestions.ByFlaUiXPath!);
        Assert.That(found, Is.Not.Null);
        var resolved = found!;
        Assert.That(resolved.ControlType.ToString(), Is.EqualTo(uia.ControlType));
        if (!string.IsNullOrWhiteSpace(uia.AutomationId))
        {
            Assert.That(resolved.Properties.AutomationId.ValueOrDefault, Is.EqualTo(uia.AutomationId));
        }

        if (!string.IsNullOrWhiteSpace(uia.Name))
        {
            Assert.That(resolved.Properties.Name.ValueOrDefault, Is.EqualTo(uia.Name));
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
            WindowHandleUsed = 0,
            Wpf = response.Uia?.AutomationId is null || response.Wpf is null
                ? null
                : response.Wpf with
                {
                    ElementId = response.Wpf.ElementId is null ? null : "<element>",
                    ClassName = string.IsNullOrWhiteSpace(response.Wpf.ClassName) ? null : "<class>"
                },
            Uia = response.Uia is null
                ? null
                : response.Uia with
                {
                    ElementId = null,
                    Bounds = new Rect(0, 0, 0, 0),
                    ClassName = string.IsNullOrWhiteSpace(response.Uia.ClassName) ? null : response.Uia.ClassName
                },
            UiaMapping = ScrubUiaMapping(response.UiaMapping),
            WpfMapping = null
        };

    private static UiaMappingDiagnostics? ScrubUiaMapping(UiaMappingDiagnostics? mapping) =>
        mapping is null
            ? null
            : mapping with
            {
                SelectedElementId = mapping.SelectedElementId is null ? null : "<element>",
                Candidates = mapping.Candidates
                    .Select(candidate => candidate with
                    {
                        ElementId = candidate.ElementId is null ? null : "<element>",
                        Bounds = new Rect(0, 0, 0, 0)
                    })
                    .ToArray()
            };
}
