using System.Diagnostics;
using System.Threading;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ScreenshotCorrelationTests
{
    private readonly List<string> _artifactPaths = [];
    private McpTestContext _mcp = null!;
    private string _sessionId = string.Empty;
    private TakeScreenshotResponse _clientCapture = null!;
    private WindowInfo _mainWindow = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var appExe = TestAppPaths.FindScreenshotCorrelationProbeTestAppExecutable();
        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = appExe,
            ["workingDirectory"] = Path.GetDirectoryName(appExe)
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

        _clientCapture = await TakeScreenshotAsync(
            correlation: null,
            outputPath: CreateArtifactPath("client-baseline"));
        var windows = await _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId
        });
        _mainWindow = windows.Windows.Single(window => window.Handle == _clientCapture.WindowHandleUsed);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_sessionId))
                {
                    _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
                    {
                        ["sessionId"] = _sessionId,
                        ["force"] = true,
                        ["timeoutMs"] = 2000
                    });
                }
            }
            catch
            {
            }

            await _mcp.DisposeAsync();
        }

        foreach (var path in _artifactPaths)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    [Test]
    public async Task Window_titles_report_native_captions_and_accept_the_uia_name_as_an_alias()
    {
        var nativeTitle = await _mcp.CallToolAsync<FocusWindowResponse>("set_active_window", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["title"] = _mainWindow.Title
        });
        var uiaAlias = await _mcp.CallToolAsync<FocusWindowResponse>("set_active_window", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["title"] = "Screenshot correlation probe"
        });

        Assert.Multiple(() =>
        {
            Assert.That(_mainWindow.Title, Is.EqualTo("WPF Tools MCP ScreenshotCorrelationProbe TestApp"));
            Assert.That(nativeTitle.Handle, Is.EqualTo(_mainWindow.Handle));
            Assert.That(nativeTitle.Title, Is.EqualTo(_mainWindow.Title));
            Assert.That(uiaAlias.Handle, Is.EqualTo(_mainWindow.Handle));
            Assert.That(uiaAlias.Title, Is.EqualTo(_mainWindow.Title));
        });
    }

    [Test]
    public async Task Overlap_region_returns_explicit_uia_and_wpf_candidates_with_bounded_context_and_annotations()
    {
        var back = await ResolveAsync("Correlation_BackElement", "uia");
        var front = await ResolveAsync("Correlation_FrontElement", "uia");
        var overlap = ScreenshotCorrelationGeometry.Intersect(back.Element.Bounds!, front.Element.Bounds!);
        Assert.That(overlap, Is.Not.Null, "The fixture overlap must remain deterministic.");

        var query = CenterRegion(overlap!, width: 20, height: 20);
        var imageQuery = MapScreenRegionToClientImage(query);
        var response = await TakeScreenshotAsync(
            new Dictionary<string, object?>
            {
                ["x"] = imageQuery.X,
                ["y"] = imageQuery.Y,
                ["width"] = imageQuery.Width,
                ["height"] = imageQuery.Height,
                ["backend"] = "both",
                ["maxCandidates"] = 12,
                ["maxNodes"] = 10_000,
                ["includeAncestors"] = true,
                ["maxAncestors"] = 3,
                ["annotate"] = true
            },
            CreateArtifactPath("overlap"));

        var correlation = response.Correlation;
        Assert.That(correlation, Is.Not.Null);
        var uia = correlation!.Backends.Single(result => result.Backend == InspectionBackend.Uia);
        var wpf = correlation.Backends.Single(result => result.Backend == InspectionBackend.Wpf);

        Assert.Multiple(() =>
        {
            Assert.That(correlation.Ambiguous, Is.True);
            Assert.That(uia.HasOverlaps, Is.True);
            Assert.That(wpf.HasOverlaps, Is.True);
            Assert.That(uia.Candidates, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(wpf.Candidates, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(
                correlation.Backends.All(result =>
                    result.Candidates.All(candidate =>
                        candidate.Backend == result.Backend &&
                        !string.IsNullOrWhiteSpace(candidate.Element.XPath) &&
                        candidate.Element.Bounds is { Width: > 0, Height: > 0 })),
                Is.True);
            Assert.That(
                uia.Candidates.Select(candidate => candidate.Element.AutomationId),
                Does.Contain("Correlation_BackElement"));
            Assert.That(
                uia.Candidates.Select(candidate => candidate.Element.AutomationId),
                Does.Contain("Correlation_FrontElement"));
            Assert.That(
                wpf.Candidates.Select(candidate => candidate.Element.AutomationId),
                Does.Contain("Correlation_BackElement"),
                DescribeCandidates(wpf.Candidates));
            Assert.That(
                wpf.Candidates.Select(candidate => candidate.Element.AutomationId),
                Does.Contain("Correlation_FrontElement"),
                DescribeCandidates(wpf.Candidates));
            Assert.That(
                correlation.Backends.SelectMany(result => result.Candidates)
                    .Where(candidate => candidate.Ancestors is not null)
                    .All(candidate => candidate.Ancestors!.Count <= 3),
                Is.True);
            Assert.That(
                correlation.Backends.SelectMany(result => result.Candidates)
                    .Any(candidate => candidate.Ancestors is { Count: > 0 }),
                Is.True);
        });

        AssertAnnotatedArtifact(response, minimumAnnotations: 2);
        AssertCaptureContext(response, expectedWasClipped: false);
    }

    [Test]
    public async Task Wpf_point_correlation_distinguishes_the_visible_sliver_from_clipped_away_bounds()
    {
        var clipped = await ResolveAsync("Correlation_ClippedElement", "wpf");
        var clipHost = await ResolveAsync("Correlation_ClipHost", "wpf");
        var visible = ScreenshotCorrelationGeometry.Intersect(clipped.Element.Bounds!, clipHost.Element.Bounds!);
        Assert.That(visible, Is.Not.Null.And.Property(nameof(Rect.Width)).GreaterThan(1));

        var visiblePoint = new Rect(
            visible!.X + Math.Max(1, visible.Width / 2),
            visible.Y + Math.Max(1, visible.Height / 2),
            1,
            1);
        var clippedAwayPoint = new Rect(
            Math.Min(
                clipped.Element.Bounds!.X + clipped.Element.Bounds.Width - 2,
                clipHost.Element.Bounds!.X + clipHost.Element.Bounds.Width + 8),
            visible.Y + Math.Max(1, visible.Height / 2),
            1,
            1);

        var visibleResponse = await CaptureClientPointAsync(visiblePoint, "visible-sliver");
        var clippedAwayResponse = await CaptureClientPointAsync(clippedAwayPoint, "clipped-away");
        var visibleCorrelation = visibleResponse.Correlation!;
        var clippedAwayCorrelation = clippedAwayResponse.Correlation!;
        var visibleWpf = visibleCorrelation.Backends.Single();
        var clippedAwayWpf = clippedAwayCorrelation.Backends.Single();

        Assert.Multiple(() =>
        {
            Assert.That(
                visibleWpf.Candidates.Select(candidate => candidate.Element.AutomationId),
                Does.Contain("Correlation_ClippedElement"),
                DescribeCandidates(visibleWpf.Candidates));
            Assert.That(
                clippedAwayWpf.Candidates.Select(candidate => candidate.Element.AutomationId),
                Does.Not.Contain("Correlation_ClippedElement"));
            Assert.That(clippedAwayWpf.Candidates, Is.Not.Empty);
            Assert.That(
                visibleWpf.Candidates.Single(candidate =>
                    candidate.Element.AutomationId == "Correlation_ClippedElement").MatchKind,
                Is.EqualTo(ScreenshotCorrelationMatchKind.DirectHit));
            Assert.That(visibleWpf.DirectHitIndex, Is.Not.Null);
            Assert.That(
                visibleWpf.Candidates.Single(candidate => candidate.Index == visibleWpf.DirectHitIndex).Element.AutomationId,
                Is.EqualTo("Correlation_ClippedElement"));
            Assert.That(
                visibleWpf.Candidates.Single(candidate =>
                    candidate.Element.AutomationId == "Correlation_ClippedDecoration").MatchKind,
                Is.EqualTo(ScreenshotCorrelationMatchKind.RenderedHit));
            Assert.That(visibleCorrelation.ScreenPointPhysicalPixels, Is.Not.Null);
            Assert.That(clippedAwayCorrelation.ScreenPointPhysicalPixels, Is.Not.Null);
            Assert.That(
                ScreenshotCorrelationGeometry.ContainsPoint(
                    visibleCorrelation.ScreenRegionPhysicalPixels,
                    visibleCorrelation.ScreenPointPhysicalPixels!.X,
                    visibleCorrelation.ScreenPointPhysicalPixels.Y),
                Is.True);
        });
    }

    [Test]
    public async Task Element_capture_reports_clipping_and_preserves_full_capture_context()
    {
        var clipped = await ResolveAsync("Correlation_ClippedElement", "wpf");
        var baselinePath = CreateArtifactPath("clipped-element-baseline");
        var baseline = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["elementId"] = clipped.Element.ElementId,
            ["backend"] = "wpf",
            ["captureMode"] = "printWindow",
            ["area"] = "client",
            ["clip"] = "intersect",
            ["autoScroll"] = false,
            ["fullyVisible"] = false,
            ["includeViewport"] = true,
            ["outputPath"] = baselinePath
        });

        Assert.That(baseline.WasClipped, Is.True, "The fixture target must protrude beyond the client capture area.");
        var visibleScreenRegion = ScreenshotCorrelationGeometry.Intersect(
            clipped.Element.Bounds!,
            baseline.CapturedBounds);
        Assert.That(visibleScreenRegion, Is.Not.Null);
        var imageRegion = ScreenshotCorrelationGeometry.MapScreenRegionToImage(
            CenterRegion(visibleScreenRegion!, width: 1, height: 1),
            baseline.Width,
            baseline.Height,
            baseline.CapturedBounds);
        Assert.That(imageRegion, Is.Not.Null);

        var response = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["elementId"] = clipped.Element.ElementId,
            ["backend"] = "wpf",
            ["captureMode"] = "printWindow",
            ["area"] = "client",
            ["clip"] = "intersect",
            ["autoScroll"] = false,
            ["fullyVisible"] = false,
            ["includeViewport"] = true,
            ["outputPath"] = CreateArtifactPath("clipped-element-correlated"),
            ["correlation"] = new Dictionary<string, object?>
            {
                ["x"] = imageRegion!.X,
                ["y"] = imageRegion.Y,
                ["width"] = 1,
                ["height"] = 1,
                ["backend"] = "wpf",
                ["maxCandidates"] = 8,
                ["includeAncestors"] = true,
                ["maxAncestors"] = 2,
                ["annotate"] = true
            }
        });

        AssertCaptureContext(response, expectedWasClipped: true);
        Assert.That(response.RequestedBounds, Is.Not.Null);
        Assert.That(response.Correlation!.CaptureContext.RequestedBounds, Is.EqualTo(response.RequestedBounds));
        Assert.That(
            response.Correlation.Backends.Single().Candidates
                .Select(candidate => candidate.Element.AutomationId),
            Does.Contain("Correlation_ClippedElement"));
    }

    [Test]
    public async Task Screen_capture_reports_an_owned_overlay_as_potential_obscuration()
    {
        WindowInfo? overlay = null;
        try
        {
            _ = await _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["windowHandle"] = _mainWindow.Handle,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Correlation_ShowOwnedOverlay"
                }
            });

            overlay = await WaitForWindowAsync(
                "WPF Tools MCP Correlation Owned Overlay",
                TimeSpan.FromSeconds(15));
            var response = await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["windowHandle"] = _clientCapture.WindowHandleUsed,
                ["captureMode"] = "screen",
                ["area"] = "client",
                ["outputPath"] = CreateArtifactPath("owned-overlay"),
                ["correlation"] = new Dictionary<string, object?>
                {
                    ["x"] = _clientCapture.Width / 2,
                    ["y"] = _clientCapture.Height / 2,
                    ["backend"] = "uia",
                    ["annotate"] = false
                }
            });

            var obscuration = response.Correlation!.CaptureContext.Obscuration;
            Assert.Multiple(() =>
            {
                Assert.That(response.CaptureModeUsed, Is.EqualTo(ScreenshotCaptureMode.Screen));
                Assert.That(obscuration.State, Is.EqualTo(ScreenshotObscurationState.PotentiallyObscured));
                Assert.That(obscuration.ObscuredPoints, Is.GreaterThan(0));
                Assert.That(obscuration.ObscuringWindowHandles, Does.Contain(overlay!.Handle));
            });
        }
        finally
        {
            _ = await _mcp.CallToolAsync<InvokeResponse>("invoke", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["windowHandle"] = _mainWindow.Handle,
                ["locator"] = new Dictionary<string, object?>
                {
                    ["automationId"] = "Correlation_HideOwnedOverlay"
                }
            });
            await WaitForWindowToCloseAsync(
                overlay?.Handle,
                "WPF Tools MCP Correlation Owned Overlay",
                TimeSpan.FromSeconds(15));
        }
    }

    [Test]
    public async Task Correlation_rejects_a_query_outside_the_captured_image()
    {
        var result = await _mcp.CallToolResultAsync("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["captureMode"] = "printWindow",
            ["area"] = "client",
            ["outputPath"] = CreateArtifactPath("invalid-query"),
            ["correlation"] = new Dictionary<string, object?>
            {
                ["x"] = _clientCapture.Width,
                ["y"] = 0,
                ["width"] = 1,
                ["height"] = 1,
                ["backend"] = "uia"
            }
        });

        Assert.That(result.IsError, Is.True);
    }

    private async Task<TakeScreenshotResponse> CaptureClientPointAsync(Rect screenPoint, string artifactName)
    {
        var imagePoint = MapScreenRegionToClientImage(screenPoint);
        return await TakeScreenshotAsync(
            new Dictionary<string, object?>
            {
                ["x"] = imagePoint.X,
                ["y"] = imagePoint.Y,
                ["width"] = 1,
                ["height"] = 1,
                ["backend"] = "wpf",
                ["maxCandidates"] = 8,
                ["maxNodes"] = 10_000,
                ["includeAncestors"] = true,
                ["maxAncestors"] = 3,
                ["annotate"] = true
            },
            CreateArtifactPath(artifactName));
    }

    private Task<TakeScreenshotResponse> TakeScreenshotAsync(
        IReadOnlyDictionary<string, object?>? correlation,
        string outputPath)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["captureMode"] = "printWindow",
            ["area"] = "client",
            ["clip"] = "intersect",
            ["includeViewport"] = true,
            ["outputPath"] = outputPath
        };
        if (correlation is not null)
        {
            arguments["correlation"] = correlation;
        }

        return _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", arguments);
    }

    private async Task<ResolveElementResponse> ResolveAsync(string automationId, string backend) =>
        await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["backend"] = backend,
            ["locator"] = new Dictionary<string, object?>
            {
                ["automationId"] = automationId
            }
        });

    private async Task<WindowInfo> WaitForWindowAsync(string title, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<WindowInfo> lastWindows = [];
        while (stopwatch.Elapsed < timeout)
        {
            var response = await ListWindowsAsync();
            lastWindows = response.Windows;
            var match = lastWindows.FirstOrDefault(window =>
                string.Equals(window.Title, title, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50);
        }

        throw new AssertionException(
            $"Window '{title}' did not appear within {timeout}. Last windows:{Environment.NewLine}{DescribeWindows(lastWindows)}");
    }

    private async Task WaitForWindowToCloseAsync(long? handle, string title, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? absentSince = null;
        IReadOnlyList<WindowInfo> lastWindows = [];
        while (stopwatch.Elapsed < timeout)
        {
            var response = await ListWindowsAsync();
            lastWindows = response.Windows;
            var present = lastWindows.Any(window =>
                (handle is long expectedHandle && window.Handle == expectedHandle) ||
                string.Equals(window.Title, title, StringComparison.Ordinal));
            if (present)
            {
                absentSince = null;
            }
            else
            {
                absentSince ??= stopwatch.Elapsed;
                if (stopwatch.Elapsed - absentSince >= TimeSpan.FromMilliseconds(250))
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        throw new AssertionException(
            $"Window '{title}' did not remain closed within {timeout}. Last windows:{Environment.NewLine}{DescribeWindows(lastWindows)}");
    }

    private Task<ListWindowsResponse> ListWindowsAsync() =>
        _mcp.CallToolAsync<ListWindowsResponse>("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId
        });

    private static string DescribeWindows(IReadOnlyList<WindowInfo> windows) =>
        string.Join(Environment.NewLine, windows.Select(window => $"{window.Handle}: {window.Title}"));

    private Rect MapScreenRegionToClientImage(Rect screenRegion)
    {
        var imageRegion = ScreenshotCorrelationGeometry.MapScreenRegionToImage(
            screenRegion,
            _clientCapture.Width,
            _clientCapture.Height,
            _clientCapture.CapturedBounds);
        Assert.That(imageRegion, Is.Not.Null, "Fixture query must be inside the captured client image.");
        return imageRegion!;
    }

    private static Rect CenterRegion(Rect bounds, int width, int height)
    {
        var boundedWidth = Math.Min(width, bounds.Width);
        var boundedHeight = Math.Min(height, bounds.Height);
        return new Rect(
            bounds.X + (bounds.Width - boundedWidth) / 2,
            bounds.Y + (bounds.Height - boundedHeight) / 2,
            boundedWidth,
            boundedHeight);
    }

    private void AssertAnnotatedArtifact(TakeScreenshotResponse response, int minimumAnnotations)
    {
        var correlation = response.Correlation!;
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(response.Path), Is.True);
            Assert.That(new FileInfo(response.Path).Length, Is.GreaterThan(0));
            Assert.That(correlation.Annotations, Has.Count.GreaterThanOrEqualTo(minimumAnnotations));
            Assert.That(
                correlation.Annotations.Select(annotation => annotation.Label).Distinct().ToArray(),
                Has.Length.EqualTo(correlation.Annotations.Count));
            Assert.That(
                correlation.Backends.SelectMany(result => result.Candidates)
                    .Count(candidate => candidate.Annotation is not null),
                Is.EqualTo(correlation.Annotations.Count));
            Assert.That(
                correlation.Annotations.All(annotation =>
                    annotation.ImageBounds.X >= 0 &&
                    annotation.ImageBounds.Y >= 0 &&
                    annotation.ImageBounds.X + annotation.ImageBounds.Width <= response.Width &&
                    annotation.ImageBounds.Y + annotation.ImageBounds.Height <= response.Height),
                Is.True);
        });

        using var baseline = new System.Drawing.Bitmap(_clientCapture.Path);
        using var annotated = new System.Drawing.Bitmap(response.Path);
        Assert.That(annotated.Size, Is.EqualTo(baseline.Size));
        Assert.That(
            correlation.Annotations.Any(annotation => AnnotationOutlineDiffers(annotation.ImageBounds, baseline, annotated)),
            Is.True,
            "At least one returned annotation outline must alter the captured pixels.");
    }

    private static bool AnnotationOutlineDiffers(
        Rect bounds,
        System.Drawing.Bitmap baseline,
        System.Drawing.Bitmap annotated)
    {
        var left = Math.Clamp(bounds.X, 0, annotated.Width - 1);
        var top = Math.Clamp(bounds.Y, 0, annotated.Height - 1);
        var right = Math.Clamp(bounds.X + bounds.Width - 1, left, annotated.Width - 1);
        var bottom = Math.Clamp(bounds.Y + bounds.Height - 1, top, annotated.Height - 1);

        for (var x = left; x <= right; x++)
        {
            if (baseline.GetPixel(x, top) != annotated.GetPixel(x, top) ||
                baseline.GetPixel(x, bottom) != annotated.GetPixel(x, bottom))
            {
                return true;
            }
        }

        for (var y = top; y <= bottom; y++)
        {
            if (baseline.GetPixel(left, y) != annotated.GetPixel(left, y) ||
                baseline.GetPixel(right, y) != annotated.GetPixel(right, y))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeCandidates(IReadOnlyList<ScreenshotCorrelationCandidate> candidates) =>
        string.Join(
            Environment.NewLine,
            candidates.Select(candidate =>
                $"{candidate.Index}: {candidate.Element.Type} id={candidate.Element.AutomationId ?? "<null>"} " +
                $"name={candidate.Element.Name ?? "<null>"} path={candidate.Element.XPath} " +
                $"match={candidate.MatchKind} bounds={candidate.Element.Bounds}"));

    private void AssertCaptureContext(TakeScreenshotResponse response, bool expectedWasClipped)
    {
        var context = response.Correlation!.CaptureContext;
        Assert.Multiple(() =>
        {
            Assert.That(context.Window.Handle, Is.EqualTo(response.WindowHandleUsed));
            Assert.That(response.WindowHandleUsed, Is.EqualTo(_mainWindow.Handle));
            Assert.That(_mainWindow.Title, Is.EqualTo("WPF Tools MCP ScreenshotCorrelationProbe TestApp"));
            Assert.That(context.Window.Title, Is.EqualTo(_mainWindow.Title));
            Assert.That(context.Window.Bounds, Is.EqualTo(context.Viewport.OuterBoundsPhysicalPixels));
            Assert.That(context.CapturedBounds, Is.EqualTo(response.CapturedBounds));
            Assert.That(context.CaptureModeRequested, Is.EqualTo(ScreenshotCaptureMode.PrintWindow));
            Assert.That(context.CaptureModeUsed, Is.EqualTo(response.CaptureModeUsed));
            Assert.That(context.Area, Is.EqualTo(ScreenshotCaptureArea.Client));
            Assert.That(context.Clip, Is.EqualTo(ScreenshotClipMode.Intersect));
            Assert.That(context.WasClipped, Is.EqualTo(expectedWasClipped));
            Assert.That(response.WasClipped, Is.EqualTo(expectedWasClipped));
            Assert.That(context.Viewport.ClientBoundsPhysicalPixels.Width, Is.GreaterThan(0));
            Assert.That(context.Viewport.ClientBoundsPhysicalPixels.Height, Is.GreaterThan(0));
            Assert.That(context.Viewport.OuterBoundsPhysicalPixels.Width, Is.GreaterThan(0));
            Assert.That(context.Viewport.OuterBoundsPhysicalPixels.Height, Is.GreaterThan(0));
            Assert.That(context.Viewport.Dpi.WindowDpiX, Is.GreaterThan(0));
            Assert.That(context.Viewport.Dpi.WindowDpiY, Is.GreaterThan(0));
            Assert.That(
                context.Viewport.Dpi.WindowScaleX,
                Is.EqualTo(context.Viewport.Dpi.WindowDpiX / 96d).Within(0.000_001d));
            Assert.That(
                context.Viewport.Dpi.WindowScaleY,
                Is.EqualTo(context.Viewport.Dpi.WindowDpiY / 96d).Within(0.000_001d));
            Assert.That(context.Obscuration.State, Is.EqualTo(ScreenshotObscurationState.NotApplicable));
        });
    }

    private string CreateArtifactPath(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-screenshot-correlation-{name}-{Guid.NewGuid():N}.png");
        _artifactPaths.Add(path);
        return path;
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
