using System.Text.Json;
using System.Threading;
using NUnit.Framework;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class LayoutContextTests
{
    private McpTestContext _mcp = null!;
    private string _layoutSessionId = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "diagnostics");
        var launch = await LaunchAppAsync(TestAppPaths.FindLayoutProbeTestAppExecutable());
        _layoutSessionId = launch.SessionId;

        try
        {
            _ = await _mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
            {
                ["sessionId"] = _layoutSessionId
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

        await CloseSessionBestEffortAsync(_layoutSessionId);
        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task Reports_spacing_grid_allocation_and_prioritizes_splitter_evidence()
    {
        var full = await GetLayoutContextAsync("LayoutProbe_SpacingTarget", maxSiblings: 32, maxGridDefinitions: 32);
        var splitterFocused = await GetLayoutContextAsync("LayoutProbe_SpacingTarget", maxSiblings: 1, maxGridDefinitions: 32);
        var fairBudget = await GetLayoutContextAsync(
            "LayoutProbe_SpacingTarget",
            maxAncestors: 3,
            maxSiblings: 1,
            maxGridDefinitions: 6);
        var paddedCard = full.Ancestors.Single(item =>
            item.Element.AutomationId == "LayoutProbe_PaddedCard");
        var fullSplitter = full.Siblings.Single(item =>
            item.Element.AutomationId == "LayoutProbe_Splitter");
        var nearDecoy = full.Siblings.Single(item =>
            item.Element.AutomationId == "LayoutProbe_NearDecoy");
        var unrelatedSplitter = full.Siblings.Single(item =>
            item.Element.AutomationId == "LayoutProbe_UnrelatedSplitter");
        var dedicatedSpacer = full.Siblings.Single(item =>
            item.Element.AutomationId == "LayoutProbe_DedicatedSpacer");
        var targetWindowBounds = full.Target.Geometry!.RenderBoundsInWindowWpfDips!;
        var splitterWindowBounds = fullSplitter.RenderBoundsInWindowWpfDips!;
        var targetScreenBounds = full.Target.Geometry.ScreenBoundsPhysicalPixels!;
        var splitterScreenBounds = fullSplitter.ScreenBoundsPhysicalPixels!;
        var dipGap = splitterWindowBounds.X - (targetWindowBounds.X + targetWindowBounds.Width);
        var physicalGap = splitterScreenBounds.X - (targetScreenBounds.X + targetScreenBounds.Width);

        Assert.Multiple(() =>
        {
            Assert.That(full.Target.ConfiguredWidthWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Value, 120)));
            Assert.That(full.Target.MaximumWidthWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Unbounded)));
            Assert.That(full.Target.MaximumHeightWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Unbounded)));
            Assert.That(full.Target.MinimumSizeWpfDips, Is.EqualTo(new LayoutSize(0, 0)));
            Assert.That(full.Target.RenderSizeWpfDips, Is.EqualTo(new LayoutSize(120, 72)));
            Assert.That(full.Target.ActualSizeWpfDips, Is.EqualTo(new LayoutSize(120, 72)));
            Assert.That(full.Target.DesiredSizeWpfDips!.Width, Is.GreaterThanOrEqualTo(120));
            Assert.That(full.Target.DesiredSizeWpfDips.Height, Is.GreaterThanOrEqualTo(72));
            AssertThickness(full.Target.MarginWpfDips, 11, 7, 13, 9);
            AssertThickness(full.Target.PaddingWpfDips, 9, 5, 7, 3);
            Assert.That(full.Target.Alignment!.Horizontal, Is.EqualTo("Left"));
            Assert.That(full.Target.Alignment.Vertical, Is.EqualTo("Top"));
            Assert.That(full.Target.Visibility!.Visibility, Is.EqualTo("Visible"));
            Assert.That(full.Target.Visibility.IsVisible, Is.True);
            Assert.That(full.Target.Visibility.IsMeasureValid, Is.True);
            Assert.That(full.Target.Visibility.IsArrangeValid, Is.True);
            Assert.That(full.Target.VisualIndexInParent, Is.Not.Null);
            Assert.That(full.Target.ZIndex, Is.EqualTo(7));

            Assert.That(full.Ancestors[0].Element.AutomationId, Is.EqualTo("LayoutProbe_TargetHostGrid"));
            Assert.That(full.Ancestors.Select(item => item.Depth), Is.Ordered.Ascending);
            Assert.That(full.Ancestors.Select(item => item.Depth), Is.EqualTo(Enumerable.Range(1, full.Ancestors.Count)));
            Assert.That(full.Ancestors.Any(item => item.Element.AutomationId == "LayoutProbe_PaddedCard"), Is.True);
            AssertThickness(paddedCard.Layout.PaddingWpfDips, 14, 10, 18, 12);
            AssertThickness(paddedCard.Layout.MarginWpfDips, 7, 9, 11, 13);

            Assert.That(splitterFocused.Siblings, Has.Count.EqualTo(1));
            Assert.That(splitterFocused.Siblings[0].Element.AutomationId, Is.EqualTo("LayoutProbe_Splitter"));
            Assert.That(nearDecoy.ContextDepth, Is.LessThan(fullSplitter.ContextDepth));
            Assert.That(unrelatedSplitter.GridSplitter, Is.Not.Null);
            Assert.That(dedicatedSpacer.Layout.Visibility!.Visibility, Is.EqualTo("Hidden"));
            Assert.That(dedicatedSpacer.Layout.Visibility.IsVisible, Is.False);
            Assert.That(splitterFocused.Siblings[0].GridSplitter, Is.Not.Null);
            Assert.That(splitterFocused.Siblings[0].GridSplitter!.ResizeDirection, Is.EqualTo("Columns"));
            Assert.That(splitterFocused.Siblings[0].GridSplitter!.ResizeBehavior, Is.EqualTo("PreviousAndNext"));
            Assert.That(splitterFocused.Siblings[0].GridSplitter!.ShowsPreview, Is.True);
            Assert.That(splitterFocused.Siblings[0].Layout.ConfiguredWidthWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Value, 8)));
            Assert.That(splitterFocused.Siblings[0].Layout.ConfiguredHeightWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Auto)));
            Assert.That(splitterFocused.Siblings[0].Layout.DesiredSizeWpfDips!.Width, Is.GreaterThanOrEqualTo(8));
            Assert.That(splitterFocused.Siblings[0].Layout.ActualSizeWpfDips!.Width, Is.EqualTo(8));
            Assert.That(splitterFocused.Siblings[0].Layout.ActualSizeWpfDips!.Height, Is.GreaterThan(0));
            Assert.That(splitterFocused.Siblings[0].Layout.MinimumSizeWpfDips, Is.EqualTo(new LayoutSize(0, 0)));
            Assert.That(splitterFocused.Siblings[0].Layout.MaximumWidthWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Unbounded)));
            Assert.That(splitterFocused.Siblings[0].Layout.Alignment!.Horizontal, Is.EqualTo("Stretch"));
            AssertThickness(splitterFocused.Siblings[0].Layout.MarginWpfDips, 3, 0, 5, 0);
            Assert.That(splitterFocused.Siblings[0].GridPlacement!.Raw,
                Is.EqualTo(new LayoutGridCellPlacement(1, 2, 2, 1)));
            Assert.That(splitterFocused.Siblings[0].RenderBoundsInParentWpfDips!.Width, Is.EqualTo(8));
            Assert.That(fullSplitter.DpiScaleX, Is.EqualTo(full.Target.Geometry.DpiScaleX));
            Assert.That(fullSplitter.DpiScaleY, Is.EqualTo(full.Target.Geometry.DpiScaleY));
            Assert.That(dipGap, Is.GreaterThanOrEqualTo(12));
            Assert.That(physicalGap, Is.EqualTo(dipGap * fullSplitter.DpiScaleX!.Value).Within(2));
        });

        var allocationGrid = full.GridContexts.Single(context =>
            context.Grid.AutomationId == "LayoutProbe_AllocationGrid");
        var autoRow = allocationGrid.Rows.Single(item => item.Index == 0);
        var starRow = allocationGrid.Rows.Single(item => item.Index == 1);
        var pixelRow = allocationGrid.Rows.Single(item => item.Index == 2);
        var pixelColumn = allocationGrid.Columns.Single(item => item.Index == 0);
        var spacerColumn = allocationGrid.Columns.Single(item => item.Index == 1);
        var autoColumn = allocationGrid.Columns.Single(item => item.Index == 2);
        var starColumn = allocationGrid.Columns.Single(item => item.Index == 3);
        Assert.Multiple(() =>
        {
            Assert.That(allocationGrid.AllocatedChild.AutomationId, Is.EqualTo("LayoutProbe_TargetHostGrid"));
            Assert.That(allocationGrid.Placement!.Raw, Is.EqualTo(new LayoutGridCellPlacement(1, 0, 1, 1)));
            Assert.That(allocationGrid.Placement.Effective, Is.EqualTo(new LayoutGridCellPlacement(1, 0, 1, 1)));
            Assert.That(allocationGrid.Rows.Concat(allocationGrid.Columns).Select(item => item.UnitType).Distinct(),
                Is.EquivalentTo(new[] { LayoutGridUnitType.Auto, LayoutGridUnitType.Pixel, LayoutGridUnitType.Star }));
            Assert.That(autoRow.UnitType, Is.EqualTo(LayoutGridUnitType.Auto));
            Assert.That(autoRow.ConfiguredValue, Is.Null);
            Assert.That(starRow.UnitType, Is.EqualTo(LayoutGridUnitType.Star));
            Assert.That(starRow.ConfiguredValue, Is.EqualTo(1));
            Assert.That(starRow.IsAllocated, Is.True);
            Assert.That(pixelRow.UnitType, Is.EqualTo(LayoutGridUnitType.Pixel));
            Assert.That(pixelRow.ConfiguredValue, Is.EqualTo(54));
            Assert.That(pixelRow.IsNeighbor, Is.True);
            Assert.That(pixelColumn.UnitType, Is.EqualTo(LayoutGridUnitType.Pixel));
            Assert.That(pixelColumn.ConfiguredValue, Is.EqualTo(180));
            Assert.That(pixelColumn.IsAllocated, Is.True);
            Assert.That(spacerColumn.UnitType, Is.EqualTo(LayoutGridUnitType.Pixel));
            Assert.That(spacerColumn.ConfiguredValue, Is.EqualTo(12));
            Assert.That(spacerColumn.IsNeighbor, Is.True);
            Assert.That(autoColumn.UnitType, Is.EqualTo(LayoutGridUnitType.Auto));
            Assert.That(autoColumn.ConfiguredValue, Is.Null);
            Assert.That(autoColumn.IsNeighbor, Is.False);
            Assert.That(starColumn.UnitType, Is.EqualTo(LayoutGridUnitType.Star));
            Assert.That(starColumn.ConfiguredValue, Is.EqualTo(1));
            Assert.That(allocationGrid.Rows.Concat(allocationGrid.Columns),
                Has.All.Matches<LayoutGridDefinition>(definition => definition.ActualSizeWpfDips > 0));
            Assert.That(allocationGrid.TotalRows, Is.EqualTo(3));
            Assert.That(allocationGrid.ReturnedRows, Is.EqualTo(3));
            Assert.That(allocationGrid.TotalColumns, Is.EqualTo(4));
            Assert.That(allocationGrid.ReturnedColumns, Is.EqualTo(4));
            Assert.That(allocationGrid.Truncated, Is.False);
            Assert.That(allocationGrid.AllocationWpfDips, Is.Not.Null);
            Assert.That(allocationGrid.AllocationWpfDips!.Width, Is.EqualTo(180));
            Assert.That(allocationGrid.AllocationWpfDips.Height, Is.GreaterThan(0));
        });

        Assert.That(fairBudget.GridContexts, Is.Not.Empty);
        Assert.That(
            fairBudget.GridContexts,
            Has.All.Matches<LayoutGridContext>(context => context.ReturnedRows >= 1 && context.ReturnedColumns >= 1),
            "A six-definition budget should preserve one allocated row and column for each of the three fixture Grids.");
        Assert.That(fairBudget.GridContexts, Has.All.Matches<LayoutGridContext>(context => context.Truncated));
        Assert.That(
            fairBudget.GridContexts.Single(context => context.Grid.AutomationId == "LayoutProbe_AllocationGrid").ReturnedRows,
            Is.EqualTo(1));
        Assert.That(
            fairBudget.GridContexts.Single(context => context.Grid.AutomationId == "LayoutProbe_AllocationGrid").ReturnedColumns,
            Is.EqualTo(1));
    }

    [Test]
    public async Task Reports_clipping_transforms_and_dip_to_physical_pixel_geometry()
    {
        var response = await GetLayoutContextAsync("LayoutProbe_TransformedTarget", maxAncestors: 8);
        var geometry = response.Target.Geometry!;
        var physical = geometry.ScreenBoundsPhysicalPixels!;
        var windowDips = geometry.RenderBoundsInWindowWpfDips!;
        var clipHost = response.Ancestors.Single(item =>
            item.Element.AutomationId == "LayoutProbe_ClipHost");

        Assert.Multiple(() =>
        {
            Assert.That(response.Target.ConfiguredWidthWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Value, 96)));
            Assert.That(response.Target.MaximumWidthWpfDips, Is.EqualTo(new LayoutLength(LayoutLengthKind.Unbounded)));
            Assert.That(response.Target.Clipping!.ClipToBounds, Is.False);
            Assert.That(response.Target.Clipping!.HasExplicitClip, Is.True);
            Assert.That(response.Target.Clipping.ExplicitClipBoundsLocalWpfDips, Is.EqualTo(new LayoutRect(4, 3, 80, 36)));
            Assert.That(response.Target.Clipping.HasLayoutClip, Is.Not.Null);
            Assert.That(response.Target.LayoutTransform!.IsIdentity, Is.False);
            Assert.That(response.Target.LayoutTransform.Matrix.M11, Is.EqualTo(1.05).Within(0.0001));
            Assert.That(response.Target.LayoutTransform.Matrix.M22, Is.EqualTo(1.1).Within(0.0001));
            Assert.That(response.Target.RenderTransform!.IsIdentity, Is.False);
            Assert.That(response.Target.RenderTransform.Matrix.M11, Is.EqualTo(Math.Cos(Math.PI / 12)).Within(0.0001));
            Assert.That(response.Target.RenderTransform.Matrix.M12, Is.EqualTo(Math.Sin(Math.PI / 12)).Within(0.0001));
            Assert.That(response.Target.RenderTransformOrigin, Is.EqualTo(new LayoutPoint(0.5, 0.5)));
            Assert.That(response.Target.ZIndex, Is.EqualTo(11));
            Assert.That(clipHost.Layout.Clipping!.ClipToBounds, Is.True);
            Assert.That(clipHost.Layout.Clipping.HasExplicitClip, Is.False);
            Assert.That(clipHost.Layout.Clipping.HasLayoutClip, Is.Not.Null);
            Assert.That(geometry.DpiScaleX, Is.GreaterThan(0));
            Assert.That(geometry.DpiScaleY, Is.GreaterThan(0));
            Assert.That(physical.Width, Is.GreaterThan(0));
            Assert.That(physical.Height, Is.GreaterThan(0));
            Assert.That(response.Element.Bounds, Is.Null);
            Assert.That(physical.Width, Is.EqualTo(windowDips.Width * geometry.DpiScaleX!.Value).Within(2));
            Assert.That(physical.Height, Is.EqualTo(windowDips.Height * geometry.DpiScaleY!.Value).Within(2));
        });
    }

    [Test]
    public async Task Reports_an_empty_explicit_clip_as_known_empty_not_unavailable()
    {
        var response = await GetLayoutContextAsync("LayoutProbe_EmptyClipTarget");

        Assert.Multiple(() =>
        {
            Assert.That(response.Target.Clipping!.HasExplicitClip, Is.True);
            Assert.That(response.Target.Clipping.ExplicitClipIsEmpty, Is.True);
            Assert.That(response.Target.Clipping.ExplicitClipBoundsLocalWpfDips, Is.Null);
            Assert.That(
                response.UnavailableEvidence.Any(item =>
                    item.SubjectXPath == response.Element.XPath &&
                    item.Field == "clipping.explicitClip"),
                Is.False);
        });
    }

    [Test]
    public async Task Models_implicit_grid_as_effective_one_by_one_without_hiding_raw_placement()
    {
        var response = await GetLayoutContextAsync("LayoutProbe_ImplicitTarget");
        var grid = response.GridContexts.Single(context =>
            context.Grid.AutomationId == "LayoutProbe_ImplicitGrid");

        Assert.Multiple(() =>
        {
            Assert.That(grid.Placement!.Raw, Is.EqualTo(new LayoutGridCellPlacement(4, 3, 3, 2)));
            Assert.That(grid.Placement.Effective, Is.EqualTo(new LayoutGridCellPlacement(0, 0, 1, 1)));
            Assert.That(grid.Placement.UsesImplicitRowDefinition, Is.True);
            Assert.That(grid.Placement.UsesImplicitColumnDefinition, Is.True);
            Assert.That(grid.TotalRows, Is.EqualTo(1));
            Assert.That(grid.TotalColumns, Is.EqualTo(1));
            Assert.That(grid.Rows.Single().IsImplicit, Is.True);
            Assert.That(grid.Columns.Single().IsImplicit, Is.True);
        });
    }

    [Test]
    public async Task Reports_not_applicable_padding_as_bounded_stable_evidence_without_a_value_guess()
    {
        var response = await GetLayoutContextAsync("LayoutProbe_AllocationGrid");
        var paddingEvidence = response.UnavailableEvidence.Single(item =>
            item.SubjectXPath == response.Element.XPath && item.Field == "paddingWpfDips");

        Assert.Multiple(() =>
        {
            Assert.That(response.Target.PaddingWpfDips, Is.Null);
            Assert.That(paddingEvidence.Status, Is.EqualTo(LayoutEvidenceStatus.NotApplicable));
            Assert.That(paddingEvidence.Reason, Is.EqualTo("type_has_no_padding"));
            Assert.That(response.Counts.ReturnedUnavailableEvidence, Is.EqualTo(response.UnavailableEvidence.Count));
            Assert.That(response.Counts.DiscoveredUnavailableEvidence, Is.EqualTo(response.Counts.ReturnedUnavailableEvidence));
            Assert.That(response.TruncatedReasons, Does.Not.Contain("maxUnavailableEvidence"));
        });
    }

    [Test]
    public async Task Reports_templated_parent_identity_for_a_template_part()
    {
        var response = await GetLayoutContextAsync("LayoutProbe_TemplatePart");

        Assert.Multiple(() =>
        {
            Assert.That(response.Target.TemplatedParent, Is.Not.Null);
            Assert.That(response.Target.TemplatedParent!.Type, Is.EqualTo("Button"));
            Assert.That(response.Target.TemplatedParent.AutomationId, Is.EqualTo("LayoutProbe_TemplatedButton"));
            Assert.That(response.Target.TemplatedParent.IdentityTruncated, Is.False);
        });
    }

    [Test]
    public async Task Preserves_public_target_handle_and_never_leaks_private_nested_handles()
    {
        var resolved = await _mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
        {
            ["sessionId"] = _layoutSessionId,
            ["backend"] = "wpf",
            ["locator"] = Locator("LayoutProbe_AutoTarget")
        });
        var response = await _mcp.CallToolAsync<GetLayoutContextResponse>("get_layout_context", new Dictionary<string, object?>
        {
            ["sessionId"] = _layoutSessionId,
            ["elementId"] = resolved.Element.ElementId
        });
        var locatorResponse = await GetLayoutContextAsync("LayoutProbe_AutoTarget");
        var handleAfterLocator = await _mcp.CallToolAsync<GetLayoutContextResponse>("get_layout_context", new Dictionary<string, object?>
        {
            ["sessionId"] = _layoutSessionId,
            ["elementId"] = resolved.Element.ElementId
        });
        var json = JsonSerializer.Serialize(response);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Element.ElementId, Does.StartWith("wpf_"));
            Assert.That(response.Element.ElementId, Is.EqualTo(resolved.Element.ElementId));
            Assert.That(response.Element.ElementIdWpf, Is.Null);
            Assert.That(response.Target.ConfiguredWidthWpfDips!.Kind, Is.EqualTo(LayoutLengthKind.Auto));
            Assert.That(response.Target.MaximumWidthWpfDips!.Kind, Is.EqualTo(LayoutLengthKind.Unbounded));
            Assert.That(json, Does.Not.Contain("wpfobj_"));
            Assert.That(locatorResponse.Target, Is.EqualTo(response.Target));
            Assert.That(ToStableBoundedProjection(locatorResponse), Is.EqualTo(ToStableBoundedProjection(response)));
            Assert.That(handleAfterLocator.Element.ElementId, Is.EqualTo(resolved.Element.ElementId));
            Assert.That(handleAfterLocator.Element.ElementIdWpf, Is.Null);
        });
    }

    [Test]
    public async Task Public_target_handle_recovers_after_private_agent_handle_eviction()
    {
        await using var mcp = await McpTestContext.StartAsync(
            McpServerPaths.FindMcpServerExecutable(),
            toolProfile: "diagnostics",
            environmentVariables: new Dictionary<string, string?>
            {
                ["WPF_TOOLS_MCP_AGENT_MAX_WPF_HANDLES"] = "1"
            });
        var executablePath = TestAppPaths.FindLayoutProbeTestAppExecutable();
        var launch = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = executablePath,
            ["workingDirectory"] = Path.GetDirectoryName(executablePath)!
        });

        try
        {
            try
            {
                _ = await mcp.CallToolAsync<InjectAgentResponse>("inject_agent", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId
                });
            }
            catch (InvalidOperationException ex) when (ShouldSkipForMissingAssets(ex))
            {
                Assert.Ignore(ex.Message);
            }

            var target = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["backend"] = "wpf",
                ["locator"] = Locator("LayoutProbe_AutoTarget")
            });
            var evicting = await mcp.CallToolAsync<ResolveElementResponse>("resolve_element", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["backend"] = "wpf",
                ["locator"] = Locator("LayoutProbe_SpacingTarget")
            });
            var recovered = await mcp.CallToolAsync<GetLayoutContextResponse>("get_layout_context", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["elementId"] = target.Element.ElementId
            });
            var byLocator = await mcp.CallToolAsync<GetLayoutContextResponse>("get_layout_context", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = Locator("LayoutProbe_AutoTarget")
            });

            Assert.Multiple(() =>
            {
                Assert.That(target.Element.ElementId, Does.StartWith("wpf_"));
                Assert.That(evicting.Element.ElementId, Is.Not.EqualTo(target.Element.ElementId));
                Assert.That(recovered.Element.ElementId, Is.EqualTo(target.Element.ElementId));
                Assert.That(recovered.Element.ElementIdWpf, Is.Null);
                Assert.That(recovered.Target, Is.EqualTo(byLocator.Target));
                Assert.That(ToStableBoundedProjection(recovered), Is.EqualTo(ToStableBoundedProjection(byLocator)));
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

    [Test]
    public async Task Truncation_counts_reasons_and_selection_order_are_stable_across_all_limits()
    {
        var first = await GetLayoutContextAsync(
            "LayoutProbe_SpacingTarget",
            maxAncestors: 1,
            maxSiblings: 1,
            maxGridDefinitions: 1);
        var second = await GetLayoutContextAsync(
            "LayoutProbe_SpacingTarget",
            maxAncestors: 1,
            maxSiblings: 1,
            maxGridDefinitions: 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.Truncated, Is.True);
            Assert.That(first.TruncatedReason, Is.EqualTo("maxAncestors"));
            Assert.That(first.TruncatedReasons, Is.EqualTo(new[]
            {
                "maxAncestors",
                "maxSiblings",
                "maxGridDefinitions"
            }));
            Assert.That(first.Counts.ReturnedAncestors, Is.EqualTo(1));
            Assert.That(first.Counts.DiscoveredAncestors, Is.GreaterThan(1));
            Assert.That(first.Counts.ReturnedSiblings, Is.EqualTo(1));
            Assert.That(first.Counts.DiscoveredSiblings, Is.GreaterThan(1));
            Assert.That(first.Counts.ReturnedGridDefinitions, Is.EqualTo(1));
            Assert.That(first.Counts.DiscoveredGridDefinitions, Is.GreaterThan(1));
            Assert.That(first.Counts.ReturnedAncestors, Is.EqualTo(first.Ancestors.Count));
            Assert.That(first.Counts.ReturnedSiblings, Is.EqualTo(first.Siblings.Count));
            Assert.That(first.Counts.ReturnedGridContexts, Is.EqualTo(first.GridContexts.Count));
            Assert.That(first.Counts.ReturnedGridDefinitions,
                Is.EqualTo(first.GridContexts.Sum(context => context.ReturnedRows + context.ReturnedColumns)));
            Assert.That(first.Counts.ReturnedUnavailableEvidence, Is.EqualTo(first.UnavailableEvidence.Count));
            Assert.That(first.Counts.DiscoveredAncestors, Is.GreaterThanOrEqualTo(first.Counts.ReturnedAncestors));
            Assert.That(first.Counts.DiscoveredSiblings, Is.GreaterThanOrEqualTo(first.Counts.ReturnedSiblings));
            Assert.That(first.Counts.DiscoveredGridContexts, Is.GreaterThanOrEqualTo(first.Counts.ReturnedGridContexts));
            Assert.That(first.Counts.DiscoveredGridDefinitions, Is.GreaterThanOrEqualTo(first.Counts.ReturnedGridDefinitions));
            Assert.That(first.Counts.DiscoveredUnavailableEvidence, Is.GreaterThanOrEqualTo(first.Counts.ReturnedUnavailableEvidence));
            Assert.That(second.Counts, Is.EqualTo(first.Counts));
            Assert.That(second.TruncatedReasons, Is.EqualTo(first.TruncatedReasons));
            Assert.That(ToStableBoundedProjection(second), Is.EqualTo(ToStableBoundedProjection(first)));
        });
    }

    [Test]
    public async Task Deeply_nested_fixture_reports_nearest_first_ancestor_truncation()
    {
        var launch = await LaunchAppAsync(TestAppPaths.FindDeeplyNestedTestAppExecutable());
        try
        {
            var response = await _mcp.CallToolAsync<GetLayoutContextResponse>("get_layout_context", new Dictionary<string, object?>
            {
                ["sessionId"] = launch.SessionId,
                ["locator"] = Locator("Nested_TargetButton"),
                ["maxAncestors"] = 2,
                ["maxSiblings"] = 0,
                ["maxGridDefinitions"] = 0
            });

            Assert.Multiple(() =>
            {
                Assert.That(response.Ancestors.Select(item => item.Element.AutomationId),
                    Is.EqualTo(new[] { "Nested_Level_18", "Nested_Level_17" }));
                Assert.That(response.Ancestors.Select(item => item.Depth), Is.EqualTo(new[] { 1, 2 }));
                Assert.That(response.Counts.DiscoveredAncestors, Is.GreaterThan(18));
                Assert.That(response.Counts.ReturnedAncestors, Is.EqualTo(2));
                Assert.That(response.TruncatedReason, Is.EqualTo("maxAncestors"));
                Assert.That(response.TruncatedReasons, Is.EqualTo(new[] { "maxAncestors" }));
            });
        }
        finally
        {
            await CloseSessionBestEffortAsync(launch.SessionId);
        }
    }

    private Task<GetLayoutContextResponse> GetLayoutContextAsync(
        string automationId,
        int maxAncestors = 6,
        int maxSiblings = 8,
        int maxGridDefinitions = 32) =>
        _mcp.CallToolAsync<GetLayoutContextResponse>("get_layout_context", new Dictionary<string, object?>
        {
            ["sessionId"] = _layoutSessionId,
            ["locator"] = Locator(automationId),
            ["maxAncestors"] = maxAncestors,
            ["maxSiblings"] = maxSiblings,
            ["maxGridDefinitions"] = maxGridDefinitions
        });

    private async Task<LaunchAppResponse> LaunchAppAsync(string executablePath) =>
        await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = executablePath,
            ["workingDirectory"] = Path.GetDirectoryName(executablePath)!
        });

    private async Task CloseSessionBestEffortAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["force"] = true,
                ["timeoutMs"] = 2000
            });
        }
        catch
        {
        }
    }

    private static Dictionary<string, object?> Locator(string automationId) =>
        new() { ["automationId"] = automationId };

    private static void AssertThickness(
        LayoutThickness? actual,
        double left,
        double top,
        double right,
        double bottom) =>
        Assert.That(actual, Is.EqualTo(new LayoutThickness(left, top, right, bottom)));

    private static string ToStableBoundedProjection(GetLayoutContextResponse response) =>
        JsonSerializer.Serialize(new
        {
            response.Counts,
            response.TruncatedReason,
            response.TruncatedReasons,
            Ancestors = response.Ancestors.Select(item => new
            {
                item.Depth,
                item.Element.Type,
                item.Element.AutomationId,
                item.Element.XPath
            }),
            Siblings = response.Siblings.Select(item => new
            {
                item.ContextDepth,
                item.VisualIndex,
                item.RelativeVisualIndex,
                item.Element.Type,
                item.Element.AutomationId,
                item.Element.XPath,
                item.GridPlacement,
                item.ZIndex,
                item.GridSplitter
            }),
            Grids = response.GridContexts.Select(item => new
            {
                item.ContextDepth,
                GridAutomationId = item.Grid.AutomationId,
                GridXPath = item.Grid.XPath,
                ChildAutomationId = item.AllocatedChild.AutomationId,
                item.Placement,
                Rows = item.Rows.Select(row => new { row.Index, row.UnitType, row.IsAllocated, row.IsNeighbor }),
                Columns = item.Columns.Select(column => new { column.Index, column.UnitType, column.IsAllocated, column.IsNeighbor }),
                item.TotalRows,
                item.ReturnedRows,
                item.TotalColumns,
                item.ReturnedColumns,
                item.Truncated
            })
        });

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
