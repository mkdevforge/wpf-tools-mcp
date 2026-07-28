using System.Text.Json;
using System.Threading;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class DirectSearchTests
{
    private static readonly InspectionBackend[] DirectBackends =
    [
        InspectionBackend.Uia,
        InspectionBackend.Wpf
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private McpTestContext _mcp = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "diagnostics",
            cancellationToken: setupCts.Token);
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
    public async Task FindElements_reports_complete_bounded_and_stable_direct_results()
    {
        var launch = await LaunchAppAsync(TestAppPaths.FindMinimalTestAppExecutable());
        try
        {
            var completeByBackend = new Dictionary<InspectionBackend, FindElementsResponse>();

            foreach (var backend in DirectBackends)
            {
                var complete = await FindOkButtonsAsync(launch.SessionId, backend, maxResults: 2);
                completeByBackend.Add(backend, complete);

                Assert.Multiple(() =>
                {
                    Assert.That(complete.BackendUsed, Is.EqualTo(backend));
                    Assert.That(complete.ReturnedMatches, Is.EqualTo(2));
                    Assert.That(complete.DiscoveredMatches, Is.EqualTo(2));
                    Assert.That(complete.Matches, Has.Count.EqualTo(2));
                    Assert.That(complete.Truncated, Is.False);
                    Assert.That(complete.TruncatedReason, Is.Null);
                    Assert.That(complete.Matches.Select(match => match.XPath), Is.Unique);
                });

                foreach (var match in complete.Matches)
                {
                    AssertStandardMatch(match, backend, expectedOffscreen: false);

                    var reusable = await _mcp.CallToolAsync<GetPathToElementResponse>(
                        "get_path_to_element",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = launch.SessionId,
                            ["elementId"] = match.ElementId
                        });

                    Assert.Multiple(() =>
                    {
                        Assert.That(reusable.BackendUsed, Is.EqualTo(backend));
                        Assert.That(reusable.XPath, Is.EqualTo(match.XPath));
                    });
                }

                var repeated = await FindOkButtonsAsync(launch.SessionId, backend, maxResults: 2);
                Assert.That(
                    repeated.Matches.Select(ToStableIdentity),
                    Is.EqualTo(complete.Matches.Select(ToStableIdentity)),
                    $"{backend} should preserve direct-search order and identity for unchanged UI.");

                var bounded = await FindOkButtonsAsync(launch.SessionId, backend, maxResults: 1);
                Assert.Multiple(() =>
                {
                    Assert.That(bounded.BackendUsed, Is.EqualTo(backend));
                    Assert.That(bounded.ReturnedMatches, Is.EqualTo(1));
                    Assert.That(bounded.DiscoveredMatches, Is.EqualTo(2));
                    Assert.That(bounded.Matches, Has.Count.EqualTo(1));
                    Assert.That(bounded.Truncated, Is.True);
                    Assert.That(bounded.TruncatedReason, Is.EqualTo("maxResults"));
                    Assert.That(bounded.Matches[0].XPath, Is.EqualTo(complete.Matches[0].XPath));
                });

                var withoutIds = await FindOkButtonsAsync(
                    launch.SessionId,
                    backend,
                    maxResults: 2,
                    includeElementIds: false);
                Assert.That(
                    withoutIds.Matches,
                    Has.All.Matches<ElementRef>(match =>
                        match.ElementId is null &&
                        match.ElementIdUia is null &&
                        match.ElementIdWpf is null));
            }

            var automatic = await FindOkButtonsAsync(launch.SessionId, InspectionBackend.Auto, maxResults: 2);
            var automaticBackend = automatic.BackendUsed;
            Assert.That(automaticBackend, Is.AnyOf(InspectionBackend.Uia, InspectionBackend.Wpf));
            Assert.Multiple(() =>
            {
                Assert.That(automatic.ReturnedMatches, Is.EqualTo(2));
                Assert.That(automatic.DiscoveredMatches, Is.EqualTo(2));
                Assert.That(automatic.Truncated, Is.False);
                Assert.That(
                    automatic.Matches.Select(ToStableIdentity),
                    Is.EqualTo(completeByBackend[automaticBackend].Matches.Select(ToStableIdentity)));
            });
        }
        finally
        {
            await TerminateAppAsync(launch.SessionId);
        }
    }

    [Test]
    public async Task ResolveElement_returns_structured_retryable_ambiguity_for_direct_backends()
    {
        var launch = await LaunchAppAsync(TestAppPaths.FindMinimalTestAppExecutable());
        try
        {
            foreach (var backend in DirectBackends)
            {
                var result = await _mcp.CallToolResultAsync(
                    "resolve_element",
                    CreateResolveArguments(launch.SessionId, backend));

                var fallback = result.Content
                    .OfType<TextContentBlock>()
                    .Select(content => content.Text)
                    .FirstOrDefault();

                Assert.Multiple(() =>
                {
                    Assert.That(result.IsError, Is.True);
                    Assert.That(fallback, Does.Contain("ambiguous_element"));
                    Assert.That(fallback, Does.Contain("locator.index"));
                    Assert.That(result.StructuredContent, Is.Not.Null);
                });

                var ambiguity = JsonSerializer.Deserialize<ResolveElementAmbiguity>(
                    result.StructuredContent!.ToJsonString(),
                    JsonOptions);

                Assert.That(ambiguity, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(ambiguity!.Code, Is.EqualTo("ambiguous_element"));
                    Assert.That(ambiguity.BackendUsed, Is.EqualTo(backend));
                    Assert.That(ambiguity.ReturnedCandidates, Is.EqualTo(2));
                    Assert.That(ambiguity.DiscoveredCandidates, Is.EqualTo(2));
                    Assert.That(ambiguity.Truncated, Is.False);
                    Assert.That(ambiguity.TruncatedReason, Is.Null);
                    Assert.That(ambiguity.Candidates, Has.Count.EqualTo(2));
                    Assert.That(ambiguity.Candidates.Select(candidate => candidate.Index), Is.EqualTo(new[] { 0, 1 }));
                    Assert.That(ambiguity.Candidates.Select(candidate => candidate.Element.XPath), Is.Unique);
                });

                foreach (var candidate in ambiguity!.Candidates)
                {
                    AssertStandardMatch(candidate.Element, backend, expectedOffscreen: false);

                    var reusable = await _mcp.CallToolAsync<GetPathToElementResponse>(
                        "get_path_to_element",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = launch.SessionId,
                            ["elementId"] = candidate.Element.ElementId
                        });

                    Assert.Multiple(() =>
                    {
                        Assert.That(reusable.BackendUsed, Is.EqualTo(backend));
                        Assert.That(reusable.XPath, Is.EqualTo(candidate.Element.XPath));
                    });

                    var resolved = await _mcp.CallToolAsync<ResolveElementResponse>(
                        "resolve_element",
                        CreateResolveArguments(launch.SessionId, backend, candidate.Index));

                    Assert.Multiple(() =>
                    {
                        Assert.That(resolved.BackendUsed, Is.EqualTo(backend));
                        Assert.That(resolved.Element.XPath, Is.EqualTo(candidate.Element.XPath));
                        Assert.That(resolved.Element.ElementId, Does.StartWith(GetElementIdPrefix(backend)));
                    });
                }

                var cappedResult = await _mcp.CallToolResultAsync(
                    "resolve_element",
                    CreateResolveArguments(launch.SessionId, backend, listItems: true));

                Assert.Multiple(() =>
                {
                    Assert.That(cappedResult.IsError, Is.True);
                    Assert.That(cappedResult.StructuredContent, Is.Not.Null);
                });

                var capped = JsonSerializer.Deserialize<ResolveElementAmbiguity>(
                    cappedResult.StructuredContent!.ToJsonString(),
                    JsonOptions);

                Assert.That(capped, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(capped!.Code, Is.EqualTo("ambiguous_element"));
                    Assert.That(capped.BackendUsed, Is.EqualTo(backend));
                    Assert.That(capped.ReturnedCandidates, Is.EqualTo(5));
                    Assert.That(capped.DiscoveredCandidates, Is.GreaterThan(5));
                    Assert.That(capped.Truncated, Is.True);
                    Assert.That(capped.TruncatedReason, Is.EqualTo("maxCandidates"));
                    Assert.That(capped.Candidates, Has.Count.EqualTo(5));
                    Assert.That(capped.Candidates.Select(candidate => candidate.Index), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
                    Assert.That(capped.Candidates.Select(candidate => candidate.Element.XPath), Is.Unique);
                });

                var expectedListItemType = backend == InspectionBackend.Wpf ? "ListBoxItem" : "ListItem";
                foreach (var candidate in capped!.Candidates)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(candidate.Element.Type, Is.EqualTo(expectedListItemType));
                        Assert.That(candidate.Element.ClassName, Is.Not.Null.And.Not.Empty);
                        Assert.That(candidate.Element.Bounds, Is.Not.Null);
                        Assert.That(candidate.Element.IsVisible, Is.Not.Null);
                        Assert.That(candidate.Element.IsOffscreen, Is.Not.Null);
                        Assert.That(candidate.Element.ElementId, Does.StartWith(GetElementIdPrefix(backend)));
                    });

                    var reusable = await _mcp.CallToolAsync<GetPathToElementResponse>(
                        "get_path_to_element",
                        new Dictionary<string, object?>
                        {
                            ["sessionId"] = launch.SessionId,
                            ["elementId"] = candidate.Element.ElementId
                        });

                    Assert.Multiple(() =>
                    {
                        Assert.That(reusable.BackendUsed, Is.EqualTo(backend));
                        Assert.That(reusable.XPath, Is.EqualTo(candidate.Element.XPath));
                    });
                }
            }
        }
        finally
        {
            await TerminateAppAsync(launch.SessionId);
        }
    }

    [Test]
    public async Task FindElements_reports_offviewport_matches_and_scan_bounds_for_direct_backends()
    {
        var launch = await LaunchAppAsync(TestAppPaths.FindScrollTestAppExecutable());
        try
        {
            foreach (var backend in DirectBackends)
            {
                var target = await FindElementsAsync(
                    launch.SessionId,
                    backend,
                    new Dictionary<string, object?>
                    {
                        ["automationIdEquals"] = "Scroll_TargetButton"
                    },
                    maxResults: 2,
                    maxNodes: 1000,
                    returnFields: "standard");

                Assert.Multiple(() =>
                {
                    Assert.That(target.BackendUsed, Is.EqualTo(backend));
                    Assert.That(target.ReturnedMatches, Is.EqualTo(1));
                    Assert.That(target.DiscoveredMatches, Is.EqualTo(1));
                    Assert.That(target.Matches, Has.Count.EqualTo(1));
                    Assert.That(target.Truncated, Is.False);
                    Assert.That(target.Matches[0].AutomationId, Is.EqualTo("Scroll_TargetButton"));
                    Assert.That(target.Matches[0].IsOffscreen, Is.True);
                });

                var bounded = await FindElementsAsync(
                    launch.SessionId,
                    backend,
                    new Dictionary<string, object?>
                    {
                        ["typeEquals"] = "TextBlock"
                    },
                    visibleOnly: false,
                    maxResults: 100,
                    maxNodes: 30,
                    returnFields: "minimal");

                Assert.Multiple(() =>
                {
                    Assert.That(bounded.BackendUsed, Is.EqualTo(backend));
                    Assert.That(bounded.Truncated, Is.True);
                    Assert.That(bounded.TruncatedReason, Is.EqualTo("maxNodes"));
                    Assert.That(bounded.ScannedNodes, Is.EqualTo(30));
                    Assert.That(bounded.ReturnedMatches, Is.EqualTo(bounded.Matches.Count));
                    Assert.That(bounded.DiscoveredMatches, Is.EqualTo(bounded.ReturnedMatches));
                    Assert.That(bounded.DiscoveredMatches, Is.GreaterThan(0));
                });

                foreach (var match in bounded.Matches)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(match.ClassName, Is.Null);
                        Assert.That(match.Bounds, Is.Null);
                        Assert.That(match.IsVisible, Is.Null);
                        Assert.That(match.IsOffscreen, Is.Null);
                    });
                }
            }
        }
        finally
        {
            await TerminateAppAsync(launch.SessionId);
        }
    }

    private async Task<LaunchAppResponse> LaunchAppAsync(string exePath)
    {
        return await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = exePath,
            ["workingDirectory"] = Path.GetDirectoryName(exePath)!
        });
    }

    private async Task TerminateAppAsync(string sessionId)
    {
        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = await _mcp.CallToolAsync<CloseAppResponse>("terminate_app", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["timeoutMs"] = 3000
            }, cleanupCts.Token);
        }
        catch
        {
        }
    }

    private Task<FindElementsResponse> FindOkButtonsAsync(
        string sessionId,
        InspectionBackend backend,
        int maxResults,
        bool includeElementIds = true)
    {
        return FindElementsAsync(
            sessionId,
            backend,
            new Dictionary<string, object?>
            {
                ["nameEquals"] = "OK",
                ["typeEquals"] = "Button"
            },
            maxResults: maxResults,
            maxNodes: 500,
            returnFields: "standard",
            includeElementIds: includeElementIds);
    }

    private Task<FindElementsResponse> FindElementsAsync(
        string sessionId,
        InspectionBackend backend,
        IReadOnlyDictionary<string, object?> query,
        bool visibleOnly = true,
        int maxResults = 25,
        int maxNodes = 5000,
        string returnFields = "minimal",
        bool includeElementIds = true)
    {
        return _mcp.CallToolAsync<FindElementsResponse>("find_elements", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["backend"] = backend.ToString().ToLowerInvariant(),
            ["query"] = query,
            ["visibleOnly"] = visibleOnly,
            ["includeOffViewport"] = true,
            ["maxResults"] = maxResults,
            ["maxNodes"] = maxNodes,
            ["returnFields"] = returnFields,
            ["includeElementIds"] = includeElementIds
        });
    }

    private static Dictionary<string, object?> CreateResolveArguments(
        string sessionId,
        InspectionBackend backend,
        int? index = null,
        bool listItems = false)
    {
        var locator = new Dictionary<string, object?>
        {
            ["strict"] = true
        };

        if (listItems)
        {
            if (backend == InspectionBackend.Wpf)
            {
                locator["typeEquals"] = "ListBoxItem";
            }
            else
            {
                locator["controlTypeEquals"] = "ListItem";
            }
        }
        else
        {
            locator["name"] = "OK";
            locator["typeEquals"] = "Button";
        }

        if (index is not null)
        {
            locator["index"] = index.Value;
        }

        return new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["backend"] = backend.ToString().ToLowerInvariant(),
            ["locator"] = locator,
            ["timeoutMs"] = 0,
            ["visibleOnly"] = true,
            ["includeOffViewport"] = true
        };
    }

    private static void AssertStandardMatch(
        ElementRef match,
        InspectionBackend backend,
        bool expectedOffscreen)
    {
        Assert.Multiple(() =>
        {
            Assert.That(match.Type, Is.EqualTo("Button"));
            Assert.That(match.XPath, Is.Not.Null.And.Not.Empty);
            Assert.That(match.ClassName, Is.Not.Null.And.Not.Empty);
            Assert.That(match.Bounds, Is.Not.Null);
            Assert.That(match.Bounds!.Width, Is.GreaterThan(0));
            Assert.That(match.Bounds.Height, Is.GreaterThan(0));
            Assert.That(match.IsVisible, Is.True);
            Assert.That(match.IsOffscreen, Is.EqualTo(expectedOffscreen));
            Assert.That(match.ElementId, Does.StartWith(GetElementIdPrefix(backend)));
            Assert.That(match.ElementIdUia, Is.Null);
            Assert.That(match.ElementIdWpf, Is.Null);
        });
    }

    private static StableElementIdentity ToStableIdentity(ElementRef element) =>
        new(
            element.XPath,
            element.Type,
            element.AutomationId,
            element.Name,
            element.Bounds,
            element.IsVisible,
            element.IsOffscreen);

    private static string GetElementIdPrefix(InspectionBackend backend) => backend switch
    {
        InspectionBackend.Uia => "uia_",
        InspectionBackend.Wpf => "wpf_",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };

    private sealed record StableElementIdentity(
        string XPath,
        string Type,
        string? AutomationId,
        string? Name,
        Rect? Bounds,
        bool? IsVisible,
        bool? IsOffscreen);
}
