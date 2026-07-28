using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;
using ContractRect = WpfToolsMcp.Contracts.Rect;
using WpfRect = System.Windows.Rect;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const int MaxLayoutAncestors = 32;
    private const int MaxLayoutSiblings = 128;
    private const int MaxLayoutGridDefinitions = 256;
    private const int MaxLayoutUnavailableEvidence = 128;
    private const int MaxLayoutResolutionNodes = 200_000;
    private const int MaxLayoutTypeLength = 128;
    private const int MaxLayoutXPathLength = 2048;
    private const int MaxLayoutIdentityValueLength = 256;
    private const int MaxLayoutEvidenceFieldLength = 128;
    private const int MaxLayoutEvidenceReasonLength = 128;

    private sealed record LayoutEdge(
        int ContextDepth,
        (DependencyObject Element, string XPath) Subject,
        (DependencyObject Element, string XPath) Parent,
        int ChildCount,
        int SubjectVisualIndex);

    private sealed record LayoutEdgeDiscovery(
        IReadOnlyList<LayoutEdge> EligibleEdges,
        int DiscoveredSiblings,
        int DiscoveredGridContexts,
        int DiscoveredGridDefinitions);

    private enum LayoutGridAxis
    {
        Row,
        Column
    }

    private sealed record LayoutGridDefinitionSelection(
        LayoutEdge Edge,
        LayoutGridAxis Axis,
        int Index,
        bool? IsAllocated,
        bool? IsNeighbor,
        int Phase,
        int Round);

    private sealed record LayoutSiblingCandidate(
        LayoutEdge Edge,
        DependencyObject Sibling,
        string XPathLabel,
        int VisualIndex,
        int Priority,
        int RelevanceDistance,
        int VisualDistance);

    private sealed class LayoutSiblingCandidateComparer : IComparer<LayoutSiblingCandidate>
    {
        public static LayoutSiblingCandidateComparer Instance { get; } = new();

        public int Compare(LayoutSiblingCandidate? left, LayoutSiblingCandidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var comparison = left.Priority.CompareTo(right.Priority);
            if (comparison == 0)
            {
                comparison = left.RelevanceDistance.CompareTo(right.RelevanceDistance);
            }

            if (comparison == 0)
            {
                comparison = left.Edge.ContextDepth.CompareTo(right.Edge.ContextDepth);
            }

            if (comparison == 0)
            {
                comparison = left.VisualDistance.CompareTo(right.VisualDistance);
            }

            if (comparison == 0)
            {
                comparison = left.VisualIndex.CompareTo(right.VisualIndex);
            }

            return comparison;
        }
    }

    private sealed class LayoutEvidenceCollector
    {
        private readonly int _capacity;
        private readonly List<LayoutUnavailableEvidence> _items;
        private readonly HashSet<(string SubjectXPath, string Field, LayoutEvidenceStatus Status, string Reason)> _seen = [];

        public LayoutEvidenceCollector(int capacity)
        {
            _capacity = capacity;
            _items = new List<LayoutUnavailableEvidence>(capacity);
        }

        public IReadOnlyList<LayoutUnavailableEvidence> Items => _items;

        public int DiscoveredCount => _seen.Count;

        public bool Truncated => _seen.Count > _items.Count;

        public void Add(string subjectXPath, string field, LayoutEvidenceStatus status, string reason)
        {
            var boundedSubject = BoundLayoutString(subjectXPath, MaxLayoutXPathLength, out _);
            var boundedField = BoundLayoutString(field, MaxLayoutEvidenceFieldLength, out _);
            var boundedReason = BoundLayoutString(reason, MaxLayoutEvidenceReasonLength, out _);
            var key = (boundedSubject, boundedField, status, boundedReason);
            if (!_seen.Add(key) || _items.Count >= _capacity)
            {
                return;
            }

            _items.Add(new LayoutUnavailableEvidence(
                SubjectXPath: boundedSubject,
                Field: boundedField,
                Status: status,
                Reason: boundedReason));
        }
    }

    public static GetLayoutContextResponse GetLayoutContext(
        string ownerId,
        GetLayoutContextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        var maxAncestors = Math.Clamp(request.MaxAncestors, 0, MaxLayoutAncestors);
        var maxSiblings = Math.Clamp(request.MaxSiblings, 0, MaxLayoutSiblings);
        var maxGridDefinitions = Math.Clamp(request.MaxGridDefinitions, 0, MaxLayoutGridDefinitions);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();
        var resolved = ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            request.Locator,
            request.ElementId,
            request.WindowHandle,
            visibleOnly: false,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: MaxLayoutResolutionNodes,
            cancellationToken);

        var chain = BuildXPathChainForElement(
            treeService,
            window,
            resolved.Element,
            visibleOnly: false,
            maxNodes: MaxLayoutResolutionNodes,
            cancellationToken);
        if (chain.Count == 0 || !ReferenceEquals(chain[^1].Element, resolved.Element))
        {
            throw new InvalidOperationException("wpf_layout_context: target is detached from the selected window.");
        }

        var evidence = new LayoutEvidenceCollector(MaxLayoutUnavailableEvidence);
        var edgeDiscovery = BuildLayoutEdges(
            chain,
            treeService,
            maxAncestors,
            evidence,
            cancellationToken);
        var edges = edgeDiscovery.EligibleEdges;
        var targetParent = chain.Count > 1 ? chain[^2].Element : null;
        var targetEdge = edges.FirstOrDefault(edge => edge.ContextDepth == 0);
        var target = BuildLayoutElementMetrics(
            resolved.Element,
            resolved.XPath,
            targetParent,
            targetEdge?.SubjectVisualIndex,
            window,
            evidence);
        var elementRef = BuildElementRefWpf(ownerId, resolved.Element, resolved.XPath, FindReturnFields.Minimal);

        var discoveredAncestors = Math.Max(0, chain.Count - 1);
        var returnedAncestorCount = Math.Min(discoveredAncestors, maxAncestors);
        var ancestors = new List<LayoutAncestorContext>(returnedAncestorCount);
        for (var depth = 1; depth <= returnedAncestorCount; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chainIndex = chain.Count - 1 - depth;
            var ancestor = chain[chainIndex];
            var parent = chainIndex > 0 ? chain[chainIndex - 1].Element : null;
            var edge = edges.FirstOrDefault(candidate => candidate.ContextDepth == depth);
            ancestors.Add(new LayoutAncestorContext(
                Depth: depth,
                Element: BuildLayoutIdentity(ancestor.Element, ancestor.XPath),
                Layout: BuildLayoutElementMetrics(
                    ancestor.Element,
                    ancestor.XPath,
                    parent,
                    edge?.SubjectVisualIndex,
                    window,
                    evidence)));
        }

        // The target's direct parent remains eligible even when no ancestor metrics are requested.
        var eligibleEdges = edges;

        var discoveredSiblings = edgeDiscovery.DiscoveredSiblings;
        var eligibleSiblings = SaturatingSum(eligibleEdges, edge => Math.Max(0, edge.ChildCount - 1));
        var siblings = BuildLayoutSiblingContexts(
            eligibleEdges,
            maxSiblings,
            treeService,
            window,
            evidence,
            cancellationToken);

        var eligibleGridEdges = eligibleEdges.Where(edge => edge.Parent.Element is Grid).ToArray();
        var discoveredGridDefinitions = edgeDiscovery.DiscoveredGridDefinitions;
        var eligibleGridDefinitions = SaturatingSum(eligibleGridEdges, edge =>
        {
            var grid = (Grid)edge.Parent.Element;
            return SaturatingAdd(
                GetEffectiveGridDefinitionCount(grid.RowDefinitions.Count),
                GetEffectiveGridDefinitionCount(grid.ColumnDefinitions.Count));
        });

        var gridContexts = BuildLayoutGridContexts(
            eligibleGridEdges,
            maxGridDefinitions,
            evidence,
            cancellationToken);
        var returnedGridDefinitions = SaturatingSum(
            gridContexts,
            context => context.ReturnedRows + context.ReturnedColumns);

        var truncatedReasons = new List<string>(4);
        if (discoveredAncestors > returnedAncestorCount)
        {
            truncatedReasons.Add("maxAncestors");
        }

        if (eligibleSiblings > siblings.Count)
        {
            truncatedReasons.Add("maxSiblings");
        }

        if (eligibleGridDefinitions > returnedGridDefinitions)
        {
            truncatedReasons.Add("maxGridDefinitions");
        }

        if (evidence.Truncated)
        {
            truncatedReasons.Add("maxUnavailableEvidence");
        }

        var counts = new LayoutContextCounts(
            DiscoveredAncestors: discoveredAncestors,
            ReturnedAncestors: ancestors.Count,
            DiscoveredSiblings: discoveredSiblings,
            ReturnedSiblings: siblings.Count,
            DiscoveredGridContexts: edgeDiscovery.DiscoveredGridContexts,
            ReturnedGridContexts: gridContexts.Count,
            DiscoveredGridDefinitions: discoveredGridDefinitions,
            ReturnedGridDefinitions: returnedGridDefinitions,
            DiscoveredUnavailableEvidence: evidence.DiscoveredCount,
            ReturnedUnavailableEvidence: evidence.Items.Count);

        return new GetLayoutContextResponse(
            Element: elementRef,
            Target: target,
            Ancestors: ancestors,
            Siblings: siblings,
            GridContexts: gridContexts,
            Counts: counts,
            UnavailableEvidence: evidence.Items,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault(),
            TruncatedReasons: truncatedReasons.Count > 0 ? truncatedReasons : null);
    }

    private static LayoutEdgeDiscovery BuildLayoutEdges(
        IReadOnlyList<(DependencyObject Element, string XPath)> chain,
        VisualTreeService treeService,
        int maxAncestors,
        LayoutEvidenceCollector evidence,
        CancellationToken cancellationToken)
    {
        var eligibleCapacity = Math.Min(Math.Max(0, chain.Count - 1), maxAncestors + 1);
        var eligibleEdges = new List<LayoutEdge>(eligibleCapacity);
        var discoveredSiblings = 0;
        var discoveredGridContexts = 0;
        var discoveredGridDefinitions = 0;

        for (var contextDepth = 0; contextDepth < chain.Count - 1; contextDepth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subjectIndex = chain.Count - 1 - contextDepth;
            var subject = chain[subjectIndex];
            var parent = chain[subjectIndex - 1];
            var childCount = 0;
            var visualIndex = -1;
            foreach (var child in treeService.GetChildren(parent.Element).OfType<DependencyObject>())
            {
                if ((childCount & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (ReferenceEquals(child, subject.Element))
                {
                    visualIndex = childCount;
                }

                childCount++;
            }

            if (visualIndex < 0)
            {
                evidence.Add(
                    subject.XPath,
                    "visualIndexInParent",
                    LayoutEvidenceStatus.Unavailable,
                    "subject_not_in_direct_visual_children");
                continue;
            }

            discoveredSiblings = SaturatingAdd(discoveredSiblings, Math.Max(0, childCount - 1));
            if (parent.Element is Grid grid)
            {
                discoveredGridContexts = SaturatingAdd(discoveredGridContexts, 1);
                discoveredGridDefinitions = SaturatingAdd(
                    discoveredGridDefinitions,
                    SaturatingAdd(
                        GetEffectiveGridDefinitionCount(grid.RowDefinitions.Count),
                        GetEffectiveGridDefinitionCount(grid.ColumnDefinitions.Count)));
            }

            if (contextDepth <= maxAncestors)
            {
                eligibleEdges.Add(new LayoutEdge(
                    ContextDepth: contextDepth,
                    Subject: subject,
                    Parent: parent,
                    ChildCount: childCount,
                    SubjectVisualIndex: visualIndex));
            }
        }

        return new LayoutEdgeDiscovery(
            EligibleEdges: eligibleEdges,
            DiscoveredSiblings: discoveredSiblings,
            DiscoveredGridContexts: discoveredGridContexts,
            DiscoveredGridDefinitions: discoveredGridDefinitions);
    }

    private static IReadOnlyList<LayoutSiblingContext> BuildLayoutSiblingContexts(
        IReadOnlyList<LayoutEdge> edges,
        int maxSiblings,
        VisualTreeService treeService,
        Window window,
        LayoutEvidenceCollector evidence,
        CancellationToken cancellationToken)
    {
        if (maxSiblings <= 0)
        {
            return [];
        }

        var candidates = new SortedSet<LayoutSiblingCandidate>(LayoutSiblingCandidateComparer.Instance);
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subjectPlacement = edge.Parent.Element is Grid subjectGrid
                ? TryGetGridPlacement(
                    subjectGrid,
                    edge.Subject.Element,
                    edge.Subject.XPath,
                    evidence,
                    recordEvidence: false)
                : null;

            var visualIndex = 0;
            foreach (var sibling in treeService.GetChildren(edge.Parent.Element).OfType<DependencyObject>())
            {
                if ((visualIndex & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (visualIndex == edge.SubjectVisualIndex)
                {
                    visualIndex++;
                    continue;
                }

                var priority = 3;
                var relevanceDistance = int.MaxValue;
                if (edge.Parent.Element is Grid grid && subjectPlacement is not null)
                {
                    var siblingPlacement = TryGetGridPlacement(
                        grid,
                        sibling,
                        edge.Parent.XPath,
                        evidence,
                        recordEvidence: false);
                    var isAdjacent = siblingPlacement is not null &&
                                     AreGridPlacementsAdjacent(subjectPlacement.Effective, siblingPlacement.Effective);
                    var splitterDistance = sibling is GridSplitter splitter && siblingPlacement is not null
                        ? GetGridSplitterRelevanceDistance(
                            subjectPlacement.Effective,
                            siblingPlacement.Effective,
                            splitter)
                        : null;
                    priority = sibling switch
                    {
                        GridSplitter when splitterDistance is not null => 0,
                        not GridSplitter when isAdjacent => 1,
                        _ => 2
                    };
                    relevanceDistance = splitterDistance ??
                        (isAdjacent ? GetGridCellDistance(subjectPlacement.Effective, siblingPlacement!.Effective) : int.MaxValue);
                }

                candidates.Add(new LayoutSiblingCandidate(
                    Edge: edge,
                    Sibling: sibling,
                    XPathLabel: GetXPathLabel(sibling),
                    VisualIndex: visualIndex,
                    Priority: priority,
                    RelevanceDistance: relevanceDistance,
                    VisualDistance: Math.Abs(visualIndex - edge.SubjectVisualIndex)));

                if (candidates.Count > maxSiblings)
                {
                    candidates.Remove(candidates.Max!);
                }

                visualIndex++;
            }
        }

        var selected = candidates.ToArray();
        var selectedXPaths = BuildSelectedSiblingXPaths(selected, treeService, cancellationToken);

        var siblings = new List<LayoutSiblingContext>(selected.Length);
        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edge = candidate.Edge;
            var sibling = candidate.Sibling;
            var siblingXPath = selectedXPaths[(edge.ContextDepth, candidate.VisualIndex)];
            var boundsInParent = TryGetRenderBoundsInAncestor(
                sibling,
                edge.Parent.Element,
                siblingXPath,
                "renderBoundsInParentWpfDips",
                evidence);
            var boundsInWindow = TryGetRenderBoundsInAncestor(
                sibling,
                window,
                siblingXPath,
                "renderBoundsInWindowWpfDips",
                evidence);
            ContractRect? screenBounds = null;
            if (sibling is UIElement siblingUiElement)
            {
                screenBounds = TryGetTransformedScreenBounds(siblingUiElement, siblingXPath, evidence);
            }
            else
            {
                evidence.Add(
                    siblingXPath,
                    "screenBoundsPhysicalPixels",
                    LayoutEvidenceStatus.Unsupported,
                    "not_ui_element");
            }

            var (dpiScaleX, dpiScaleY) = TryGetLayoutDpiScales(sibling, siblingXPath, evidence);
            var gridPlacement = edge.Parent.Element is Grid grid
                ? TryGetGridPlacement(grid, sibling, siblingXPath, evidence)
                : null;

            int? zIndex = null;
            if (edge.Parent.Element is Panel && sibling is UIElement siblingUi)
            {
                try
                {
                    zIndex = Panel.GetZIndex(siblingUi);
                }
                catch
                {
                    evidence.Add(siblingXPath, "zIndex", LayoutEvidenceStatus.Unavailable, "z_index_read_failed");
                }
            }
            else
            {
                evidence.Add(siblingXPath, "zIndex", LayoutEvidenceStatus.NotApplicable, "parent_not_panel");
            }

            siblings.Add(new LayoutSiblingContext(
                ContextDepth: edge.ContextDepth,
                Parent: BuildLayoutIdentity(edge.Parent.Element, edge.Parent.XPath),
                VisualIndex: candidate.VisualIndex,
                RelativeVisualIndex: candidate.VisualIndex - edge.SubjectVisualIndex,
                Element: BuildLayoutIdentity(sibling, siblingXPath),
                Layout: BuildLayoutSiblingMetrics(sibling, siblingXPath, evidence),
                RenderBoundsInParentWpfDips: boundsInParent,
                RenderBoundsInWindowWpfDips: boundsInWindow,
                ScreenBoundsPhysicalPixels: screenBounds,
                DpiScaleX: dpiScaleX,
                DpiScaleY: dpiScaleY,
                GridPlacement: gridPlacement,
                ZIndex: zIndex,
                GridSplitter: sibling is GridSplitter splitter
                    ? BuildGridSplitterInfo(splitter, siblingXPath, evidence)
                    : null));
        }

        return siblings;
    }

    private static IReadOnlyDictionary<(int ContextDepth, int VisualIndex), string> BuildSelectedSiblingXPaths(
        IReadOnlyList<LayoutSiblingCandidate> selected,
        VisualTreeService treeService,
        CancellationToken cancellationToken)
    {
        var paths = new Dictionary<(int ContextDepth, int VisualIndex), string>();
        foreach (var group in selected.GroupBy(candidate => candidate.Edge.ContextDepth))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edge = group.First().Edge;
            var selectedByIndex = group.ToDictionary(candidate => candidate.VisualIndex);
            var totalsByLabel = group
                .Select(candidate => candidate.XPathLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(label => label, _ => 0, StringComparer.OrdinalIgnoreCase);
            var occurrenceByIndex = new Dictionary<int, int>();
            var visualIndex = 0;

            foreach (var child in treeService.GetChildren(edge.Parent.Element).OfType<DependencyObject>())
            {
                if ((visualIndex & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var label = GetXPathLabel(child);
                if (totalsByLabel.TryGetValue(label, out var total))
                {
                    total++;
                    totalsByLabel[label] = total;
                    if (selectedByIndex.ContainsKey(visualIndex))
                    {
                        occurrenceByIndex[visualIndex] = total;
                    }
                }

                visualIndex++;
            }

            foreach (var candidate in group)
            {
                var total = totalsByLabel[candidate.XPathLabel];
                if (!occurrenceByIndex.TryGetValue(candidate.VisualIndex, out var occurrence))
                {
                    occurrence = 1;
                    total = 1;
                }

                var segment = total > 1
                    ? $"{candidate.XPathLabel}[{occurrence}]"
                    : candidate.XPathLabel;
                paths[(edge.ContextDepth, candidate.VisualIndex)] = $"{edge.Parent.XPath}/{segment}";
            }
        }

        return paths;
    }

    private static bool AreGridPlacementsAdjacent(
        LayoutGridCellPlacement left,
        LayoutGridCellPlacement right)
    {
        var leftRowEnd = left.Row + left.RowSpan - 1;
        var rightRowEnd = right.Row + right.RowSpan - 1;
        var leftColumnEnd = left.Column + left.ColumnSpan - 1;
        var rightColumnEnd = right.Column + right.ColumnSpan - 1;
        var rowGap = RangeGap(left.Row, leftRowEnd, right.Row, rightRowEnd);
        var columnGap = RangeGap(left.Column, leftColumnEnd, right.Column, rightColumnEnd);
        return rowGap <= 1 && columnGap <= 1;
    }

    private static int GetGridCellDistance(
        LayoutGridCellPlacement left,
        LayoutGridCellPlacement right)
    {
        var rowGap = RangeGap(
            left.Row,
            left.Row + left.RowSpan - 1,
            right.Row,
            right.Row + right.RowSpan - 1);
        var columnGap = RangeGap(
            left.Column,
            left.Column + left.ColumnSpan - 1,
            right.Column,
            right.Column + right.ColumnSpan - 1);
        return Math.Max(rowGap, columnGap);
    }

    private static int? GetGridSplitterRelevanceDistance(
        LayoutGridCellPlacement subject,
        LayoutGridCellPlacement splitterPlacement,
        GridSplitter splitter)
    {
        var subjectRowEnd = subject.Row + subject.RowSpan - 1;
        var splitterRowEnd = splitterPlacement.Row + splitterPlacement.RowSpan - 1;
        var subjectColumnEnd = subject.Column + subject.ColumnSpan - 1;
        var splitterColumnEnd = splitterPlacement.Column + splitterPlacement.ColumnSpan - 1;
        var rowGap = RangeGap(subject.Row, subjectRowEnd, splitterPlacement.Row, splitterRowEnd);
        var columnGap = RangeGap(subject.Column, subjectColumnEnd, splitterPlacement.Column, splitterColumnEnd);

        try
        {
            return splitter.ResizeDirection switch
            {
                GridResizeDirection.Columns when rowGap == 0 => columnGap,
                GridResizeDirection.Rows when columnGap == 0 => rowGap,
                GridResizeDirection.Auto when rowGap == 0 && columnGap == 0 => 0,
                GridResizeDirection.Auto when rowGap == 0 => columnGap,
                GridResizeDirection.Auto when columnGap == 0 => rowGap,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static int RangeGap(int firstStart, int firstEnd, int secondStart, int secondEnd)
    {
        if (firstEnd < secondStart)
        {
            return secondStart - firstEnd;
        }

        return secondEnd < firstStart ? firstStart - secondEnd : 0;
    }

    private static LayoutSiblingMetrics BuildLayoutSiblingMetrics(
        DependencyObject element,
        string xpath,
        LayoutEvidenceCollector evidence)
    {
        LayoutSize? desiredSize = null;
        LayoutSize? renderSize = null;
        LayoutVisibilityInfo? visibility = null;
        if (element is UIElement uiElement)
        {
            desiredSize = ToLayoutSize(uiElement.DesiredSize, xpath, "desiredSizeWpfDips", evidence);
            renderSize = ToLayoutSize(uiElement.RenderSize, xpath, "renderSizeWpfDips", evidence);
            visibility = new LayoutVisibilityInfo(
                Visibility: uiElement.Visibility.ToString(),
                IsVisible: uiElement.IsVisible,
                IsMeasureValid: uiElement.IsMeasureValid,
                IsArrangeValid: uiElement.IsArrangeValid);
        }
        else
        {
            evidence.Add(xpath, "renderSizeWpfDips", LayoutEvidenceStatus.Unsupported, "not_ui_element");
        }

        if (element is not FrameworkElement frameworkElement)
        {
            evidence.Add(xpath, "frameworkLayout", LayoutEvidenceStatus.Unsupported, "not_framework_element");
            return new LayoutSiblingMetrics(
                DesiredSizeWpfDips: desiredSize,
                RenderSizeWpfDips: renderSize,
                Visibility: visibility);
        }

        return new LayoutSiblingMetrics(
            DesiredSizeWpfDips: desiredSize,
            RenderSizeWpfDips: renderSize,
            ActualSizeWpfDips: ToLayoutSize(
                frameworkElement.ActualWidth,
                frameworkElement.ActualHeight,
                xpath,
                "actualSizeWpfDips",
                evidence),
            ConfiguredWidthWpfDips: ToConfiguredLayoutLength(
                frameworkElement.Width,
                xpath,
                "configuredWidthWpfDips",
                evidence),
            ConfiguredHeightWpfDips: ToConfiguredLayoutLength(
                frameworkElement.Height,
                xpath,
                "configuredHeightWpfDips",
                evidence),
            MinimumSizeWpfDips: ToLayoutSize(
                frameworkElement.MinWidth,
                frameworkElement.MinHeight,
                xpath,
                "minimumSizeWpfDips",
                evidence),
            MaximumWidthWpfDips: ToMaximumLayoutLength(
                frameworkElement.MaxWidth,
                xpath,
                "maximumWidthWpfDips",
                evidence),
            MaximumHeightWpfDips: ToMaximumLayoutLength(
                frameworkElement.MaxHeight,
                xpath,
                "maximumHeightWpfDips",
                evidence),
            MarginWpfDips: ToLayoutThickness(
                frameworkElement.Margin,
                xpath,
                "marginWpfDips",
                evidence),
            Alignment: new LayoutAlignmentInfo(
                Horizontal: frameworkElement.HorizontalAlignment.ToString(),
                Vertical: frameworkElement.VerticalAlignment.ToString(),
                HorizontalContent: element is Control control
                    ? control.HorizontalContentAlignment.ToString()
                    : null,
                VerticalContent: element is Control verticalControl
                    ? verticalControl.VerticalContentAlignment.ToString()
                    : null),
            Visibility: visibility);
    }

    private static LayoutGridSplitterInfo BuildGridSplitterInfo(
        GridSplitter splitter,
        string xpath,
        LayoutEvidenceCollector evidence)
    {
        try
        {
            return new LayoutGridSplitterInfo(
                ResizeDirection: splitter.ResizeDirection.ToString(),
                ResizeBehavior: splitter.ResizeBehavior.ToString(),
                ShowsPreview: splitter.ShowsPreview,
                DragIncrementWpfDips: ToFiniteOrUnavailable(
                    splitter.DragIncrement,
                    xpath,
                    "gridSplitter.dragIncrementWpfDips",
                    evidence),
                KeyboardIncrementWpfDips: ToFiniteOrUnavailable(
                    splitter.KeyboardIncrement,
                    xpath,
                    "gridSplitter.keyboardIncrementWpfDips",
                    evidence));
        }
        catch
        {
            evidence.Add(xpath, "gridSplitter", LayoutEvidenceStatus.Unavailable, "grid_splitter_read_failed");
            return new LayoutGridSplitterInfo();
        }
    }

    private static IReadOnlyList<LayoutGridContext> BuildLayoutGridContexts(
        IReadOnlyList<LayoutEdge> gridEdges,
        int maxGridDefinitions,
        LayoutEvidenceCollector evidence,
        CancellationToken cancellationToken)
    {
        var placements = new Dictionary<int, LayoutGridPlacement?>();
        var candidates = new List<LayoutGridDefinitionSelection>();
        foreach (var edge in gridEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grid = (Grid)edge.Parent.Element;
            var placement = TryGetGridPlacement(grid, edge.Subject.Element, edge.Subject.XPath, evidence);
            placements[edge.ContextDepth] = placement;
            candidates.AddRange(BuildGridDefinitionSelections(
                edge,
                grid,
                placement,
                maxGridDefinitions));
        }

        // Round-robin within each relevance phase prevents an inner Grid from consuming the global budget.
        var selected = candidates
            .OrderBy(candidate => candidate.Phase)
            .ThenBy(candidate => candidate.Round)
            .ThenBy(candidate => candidate.Edge.ContextDepth)
            .ThenBy(candidate => candidate.Axis)
            .ThenBy(candidate => candidate.Index)
            .Take(maxGridDefinitions)
            .ToArray();
        var selectedKeys = selected
            .Select(candidate => (candidate.Edge.ContextDepth, candidate.Axis, candidate.Index))
            .ToHashSet();

        var contexts = new List<LayoutGridContext>(gridEdges.Count);
        foreach (var edge in gridEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grid = (Grid)edge.Parent.Element;
            var placement = placements[edge.ContextDepth];
            var definitions = candidates
                .Where(candidate =>
                    candidate.Edge.ContextDepth == edge.ContextDepth &&
                    selectedKeys.Contains((candidate.Edge.ContextDepth, candidate.Axis, candidate.Index)))
                .ToArray();
            var rows = definitions
                .Where(selection => selection.Axis == LayoutGridAxis.Row)
                .OrderBy(selection => selection.Index)
                .Select(selection => BuildGridDefinition(grid, edge.Parent.XPath, selection, evidence))
                .ToArray();
            var columns = definitions
                .Where(selection => selection.Axis == LayoutGridAxis.Column)
                .OrderBy(selection => selection.Index)
                .Select(selection => BuildGridDefinition(grid, edge.Parent.XPath, selection, evidence))
                .ToArray();
            var totalRows = GetEffectiveGridDefinitionCount(grid.RowDefinitions.Count);
            var totalColumns = GetEffectiveGridDefinitionCount(grid.ColumnDefinitions.Count);

            contexts.Add(new LayoutGridContext(
                ContextDepth: edge.ContextDepth,
                Grid: BuildLayoutIdentity(grid, edge.Parent.XPath),
                AllocatedChild: BuildLayoutIdentity(edge.Subject.Element, edge.Subject.XPath),
                Placement: placement,
                AllocationWpfDips: TryGetLayoutSlot(
                    edge.Subject.Element,
                    edge.Subject.XPath,
                    evidence,
                    addUnsupportedEvidence: true),
                Rows: rows,
                Columns: columns,
                TotalRows: totalRows,
                ReturnedRows: rows.Length,
                TotalColumns: totalColumns,
                ReturnedColumns: columns.Length,
                Truncated: rows.Length + columns.Length < totalRows + totalColumns));
        }

        return contexts;
    }

    private static IReadOnlyList<LayoutGridDefinitionSelection> BuildGridDefinitionSelections(
        LayoutEdge edge,
        Grid grid,
        LayoutGridPlacement? placement,
        int maxGridDefinitions)
    {
        if (maxGridDefinitions <= 0)
        {
            return [];
        }

        var candidates = new List<LayoutGridDefinitionSelection>(maxGridDefinitions * 2);
        AddGridDefinitionSelections(
            candidates,
            edge,
            LayoutGridAxis.Row,
            GetEffectiveGridDefinitionCount(grid.RowDefinitions.Count),
            placement?.Effective.Row,
            placement?.Effective.RowSpan,
            maxGridDefinitions);
        AddGridDefinitionSelections(
            candidates,
            edge,
            LayoutGridAxis.Column,
            GetEffectiveGridDefinitionCount(grid.ColumnDefinitions.Count),
            placement?.Effective.Column,
            placement?.Effective.ColumnSpan,
            maxGridDefinitions);
        return candidates;
    }

    private static void AddGridDefinitionSelections(
        List<LayoutGridDefinitionSelection> candidates,
        LayoutEdge edge,
        LayoutGridAxis axis,
        int count,
        int? start,
        int? span,
        int candidateLimit)
    {
        var indices = new List<int>(Math.Min(count, candidateLimit));
        var seen = new HashSet<int>();

        void AddIndex(long index)
        {
            if (indices.Count < candidateLimit && index >= 0 && index < count && seen.Add((int)index))
            {
                indices.Add((int)index);
            }
        }

        if (start is null || span is null)
        {
            for (var index = 0; index < count && indices.Count < candidateLimit; index++)
            {
                AddIndex(index);
            }
        }
        else
        {
            var startIndex = (long)start.Value;
            var end = startIndex + span.Value - 1L;
            for (var index = startIndex; index <= end && indices.Count < candidateLimit; index++)
            {
                AddIndex(index);
            }

            AddIndex(startIndex - 1);
            AddIndex(end + 1);
            for (var distance = 2; indices.Count < candidateLimit; distance++)
            {
                var beforeCount = indices.Count;
                AddIndex(startIndex - distance);
                AddIndex(end + distance);
                if (indices.Count == beforeCount && startIndex - distance < 0 && end + distance >= count)
                {
                    break;
                }
            }
        }

        foreach (var index in indices)
        {
            var distance = start is null
                ? int.MaxValue
                : index < start.Value
                    ? start.Value - index
                    : index >= start.Value + span!.Value
                        ? index - (start.Value + span.Value - 1)
                        : 0;
            bool? isAllocated = start is null ? null : distance == 0;
            bool? isNeighbor = start is null ? null : distance == 1;
            int phase;
            int round;
            if (isAllocated == true && index == start)
            {
                phase = 0;
                round = (int)axis;
            }
            else if (isAllocated == true)
            {
                phase = 1;
                round = Math.Abs(index - start!.Value) * 2 + (int)axis;
            }
            else if (isNeighbor == true)
            {
                phase = 2;
                round = (int)axis;
            }
            else
            {
                phase = 3;
                round = distance == int.MaxValue
                    ? (int)axis
                    : distance * 2 + (int)axis;
            }

            candidates.Add(new LayoutGridDefinitionSelection(
                Edge: edge,
                Axis: axis,
                Index: index,
                IsAllocated: isAllocated,
                IsNeighbor: isNeighbor,
                Phase: phase,
                Round: round));
        }
    }

    private static LayoutGridDefinition BuildGridDefinition(
        Grid grid,
        string gridXPath,
        LayoutGridDefinitionSelection selection,
        LayoutEvidenceCollector evidence)
    {
        if (selection.Axis == LayoutGridAxis.Row && grid.RowDefinitions.Count == 0)
        {
            return BuildImplicitGridDefinition(
                grid.ActualHeight,
                gridXPath,
                "rows[0].actualSizeWpfDips",
                selection,
                evidence);
        }

        if (selection.Axis == LayoutGridAxis.Column && grid.ColumnDefinitions.Count == 0)
        {
            return BuildImplicitGridDefinition(
                grid.ActualWidth,
                gridXPath,
                "columns[0].actualSizeWpfDips",
                selection,
                evidence);
        }

        if (selection.Axis == LayoutGridAxis.Row)
        {
            var definition = grid.RowDefinitions[selection.Index];
            var unitType = ToLayoutGridUnitType(definition.Height.GridUnitType);
            if (unitType == LayoutGridUnitType.Unknown)
            {
                evidence.Add(
                    gridXPath,
                    $"rows[{selection.Index}].unitType",
                    LayoutEvidenceStatus.Unsupported,
                    "unknown_grid_unit_type");
            }

            return new LayoutGridDefinition(
                Index: selection.Index,
                UnitType: unitType,
                ConfiguredValue: ToGridConfiguredValue(
                    definition.Height,
                    gridXPath,
                    $"rows[{selection.Index}].configuredValue",
                    evidence),
                ActualSizeWpfDips: ToFiniteOrUnavailable(
                    definition.ActualHeight,
                    gridXPath,
                    $"rows[{selection.Index}].actualSizeWpfDips",
                    evidence),
                MinimumSizeWpfDips: ToFiniteOrUnavailable(
                    definition.MinHeight,
                    gridXPath,
                    $"rows[{selection.Index}].minimumSizeWpfDips",
                    evidence),
                MaximumSizeWpfDips: ToMaximumLayoutLength(
                    definition.MaxHeight,
                    gridXPath,
                    $"rows[{selection.Index}].maximumSizeWpfDips",
                    evidence),
                IsImplicit: false,
                IsAllocated: selection.IsAllocated,
                IsNeighbor: selection.IsNeighbor);
        }

        var column = grid.ColumnDefinitions[selection.Index];
        var columnUnitType = ToLayoutGridUnitType(column.Width.GridUnitType);
        if (columnUnitType == LayoutGridUnitType.Unknown)
        {
            evidence.Add(
                gridXPath,
                $"columns[{selection.Index}].unitType",
                LayoutEvidenceStatus.Unsupported,
                "unknown_grid_unit_type");
        }

        return new LayoutGridDefinition(
            Index: selection.Index,
            UnitType: columnUnitType,
            ConfiguredValue: ToGridConfiguredValue(
                column.Width,
                gridXPath,
                $"columns[{selection.Index}].configuredValue",
                evidence),
            ActualSizeWpfDips: ToFiniteOrUnavailable(
                column.ActualWidth,
                gridXPath,
                $"columns[{selection.Index}].actualSizeWpfDips",
                evidence),
            MinimumSizeWpfDips: ToFiniteOrUnavailable(
                column.MinWidth,
                gridXPath,
                $"columns[{selection.Index}].minimumSizeWpfDips",
                evidence),
            MaximumSizeWpfDips: ToMaximumLayoutLength(
                column.MaxWidth,
                gridXPath,
                $"columns[{selection.Index}].maximumSizeWpfDips",
                evidence),
            IsImplicit: false,
            IsAllocated: selection.IsAllocated,
            IsNeighbor: selection.IsNeighbor);
    }

    private static LayoutGridDefinition BuildImplicitGridDefinition(
        double actualSize,
        string gridXPath,
        string actualSizeField,
        LayoutGridDefinitionSelection selection,
        LayoutEvidenceCollector evidence) =>
        new(
            Index: 0,
            UnitType: LayoutGridUnitType.Star,
            ConfiguredValue: 1,
            ActualSizeWpfDips: ToFiniteOrUnavailable(
                actualSize,
                gridXPath,
                actualSizeField,
                evidence),
            MinimumSizeWpfDips: 0,
            MaximumSizeWpfDips: new LayoutLength(LayoutLengthKind.Unbounded),
            IsImplicit: true,
            IsAllocated: selection.IsAllocated,
            IsNeighbor: selection.IsNeighbor);

    private static int GetEffectiveGridDefinitionCount(int explicitCount) => Math.Max(1, explicitCount);

    private static LayoutGridUnitType ToLayoutGridUnitType(GridUnitType unitType) => unitType switch
    {
        GridUnitType.Auto => LayoutGridUnitType.Auto,
        GridUnitType.Pixel => LayoutGridUnitType.Pixel,
        GridUnitType.Star => LayoutGridUnitType.Star,
        _ => LayoutGridUnitType.Unknown
    };

    private static double? ToGridConfiguredValue(
        GridLength length,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence) =>
        length.IsAuto
            ? null
            : ToFiniteOrUnavailable(length.Value, xpath, field, evidence);

    private static LayoutGridPlacement? TryGetGridPlacement(
        Grid grid,
        DependencyObject element,
        string xpath,
        LayoutEvidenceCollector evidence,
        bool recordEvidence = true)
    {
        if (element is not UIElement child)
        {
            if (recordEvidence)
            {
                evidence.Add(xpath, "gridPlacement", LayoutEvidenceStatus.Unsupported, "not_ui_element");
            }

            return null;
        }

        try
        {
            var rawRow = Grid.GetRow(child);
            var rawColumn = Grid.GetColumn(child);
            var rawRowSpan = Grid.GetRowSpan(child);
            var rawColumnSpan = Grid.GetColumnSpan(child);
            var effectiveRowCount = GetEffectiveGridDefinitionCount(grid.RowDefinitions.Count);
            var effectiveColumnCount = GetEffectiveGridDefinitionCount(grid.ColumnDefinitions.Count);
            var effectiveRow = Math.Min(rawRow, effectiveRowCount - 1);
            var effectiveColumn = Math.Min(rawColumn, effectiveColumnCount - 1);
            var effectiveRowSpan = Math.Min(rawRowSpan, effectiveRowCount - effectiveRow);
            var effectiveColumnSpan = Math.Min(rawColumnSpan, effectiveColumnCount - effectiveColumn);
            return new LayoutGridPlacement(
                Raw: new LayoutGridCellPlacement(rawRow, rawColumn, rawRowSpan, rawColumnSpan),
                Effective: new LayoutGridCellPlacement(
                    effectiveRow,
                    effectiveColumn,
                    effectiveRowSpan,
                    effectiveColumnSpan),
                UsesImplicitRowDefinition: grid.RowDefinitions.Count == 0,
                UsesImplicitColumnDefinition: grid.ColumnDefinitions.Count == 0);
        }
        catch
        {
            if (recordEvidence)
            {
                evidence.Add(xpath, "gridPlacement", LayoutEvidenceStatus.Unavailable, "grid_placement_read_failed");
            }

            return null;
        }
    }

    private static LayoutElementMetrics BuildLayoutElementMetrics(
        DependencyObject element,
        string xpath,
        DependencyObject? parent,
        int? visualIndexInParent,
        Window window,
        LayoutEvidenceCollector evidence)
    {
        if (parent is null)
        {
            evidence.Add(xpath, "visualIndexInParent", LayoutEvidenceStatus.NotApplicable, "no_parent");
        }
        else if (visualIndexInParent is null)
        {
            evidence.Add(xpath, "visualIndexInParent", LayoutEvidenceStatus.Unavailable, "visual_index_not_resolved");
        }

        if (element is not UIElement uiElement)
        {
            evidence.Add(xpath, "layout", LayoutEvidenceStatus.Unsupported, "not_ui_element");
            evidence.Add(xpath, "paddingWpfDips", LayoutEvidenceStatus.NotApplicable, "not_framework_element");
            return new LayoutElementMetrics(
                VisualIndexInParent: visualIndexInParent,
                TemplatedParent: BuildTemplatedParentSummary(element));
        }

        var desiredSize = ToLayoutSize(uiElement.DesiredSize, xpath, "desiredSizeWpfDips", evidence);
        var renderSize = ToLayoutSize(uiElement.RenderSize, xpath, "renderSizeWpfDips", evidence);

        LayoutSize? actualSize = null;
        LayoutLength? configuredWidth = null;
        LayoutLength? configuredHeight = null;
        LayoutSize? minimumSize = null;
        LayoutLength? maximumWidth = null;
        LayoutLength? maximumHeight = null;
        LayoutThickness? margin = null;
        LayoutThickness? padding = null;
        LayoutAlignmentInfo? alignment = null;
        LayoutTransformInfo? layoutTransform = null;
        LayoutRect? layoutSlot = null;
        bool? hasLayoutClip = null;
        bool? layoutClipIsEmpty = null;
        LayoutRect? layoutClip = null;

        if (element is FrameworkElement frameworkElement)
        {
            actualSize = ToLayoutSize(
                frameworkElement.ActualWidth,
                frameworkElement.ActualHeight,
                xpath,
                "actualSizeWpfDips",
                evidence);
            configuredWidth = ToConfiguredLayoutLength(
                frameworkElement.Width,
                xpath,
                "configuredWidthWpfDips",
                evidence);
            configuredHeight = ToConfiguredLayoutLength(
                frameworkElement.Height,
                xpath,
                "configuredHeightWpfDips",
                evidence);
            minimumSize = ToLayoutSize(
                frameworkElement.MinWidth,
                frameworkElement.MinHeight,
                xpath,
                "minimumSizeWpfDips",
                evidence);
            maximumWidth = ToMaximumLayoutLength(
                frameworkElement.MaxWidth,
                xpath,
                "maximumWidthWpfDips",
                evidence);
            maximumHeight = ToMaximumLayoutLength(
                frameworkElement.MaxHeight,
                xpath,
                "maximumHeightWpfDips",
                evidence);
            margin = ToLayoutThickness(frameworkElement.Margin, xpath, "marginWpfDips", evidence);
            padding = TryGetPadding(frameworkElement, xpath, evidence);
            alignment = new LayoutAlignmentInfo(
                Horizontal: frameworkElement.HorizontalAlignment.ToString(),
                Vertical: frameworkElement.VerticalAlignment.ToString(),
                HorizontalContent: element is Control control
                    ? control.HorizontalContentAlignment.ToString()
                    : null,
                VerticalContent: element is Control verticalControl
                    ? verticalControl.VerticalContentAlignment.ToString()
                    : null);
            layoutTransform = ToLayoutTransform(
                frameworkElement.LayoutTransform,
                xpath,
                "layoutTransform",
                evidence);
            if (parent is null)
            {
                evidence.Add(
                    xpath,
                    "layoutSlotInParentWpfDips",
                    LayoutEvidenceStatus.NotApplicable,
                    "no_parent");
            }
            else
            {
                layoutSlot = TryGetLayoutSlot(element, xpath, evidence, addUnsupportedEvidence: false);
            }

            try
            {
                var clip = LayoutInformation.GetLayoutClip(frameworkElement);
                hasLayoutClip = clip is not null;
                if (clip is not null)
                {
                    var clipBounds = clip.Bounds;
                    layoutClipIsEmpty = clipBounds.IsEmpty;
                    if (!clipBounds.IsEmpty)
                    {
                        layoutClip = ToLayoutRect(clipBounds);
                        if (layoutClip is null)
                        {
                            evidence.Add(xpath, "clipping.layoutClip", LayoutEvidenceStatus.Unavailable, "layout_clip_bounds_invalid");
                        }
                    }
                }
            }
            catch
            {
                evidence.Add(xpath, "clipping.layoutClip", LayoutEvidenceStatus.Unavailable, "layout_clip_read_failed");
            }
        }
        else
        {
            evidence.Add(xpath, "frameworkLayout", LayoutEvidenceStatus.Unsupported, "not_framework_element");
            evidence.Add(xpath, "paddingWpfDips", LayoutEvidenceStatus.NotApplicable, "not_framework_element");
            evidence.Add(xpath, "clipping.layoutClip", LayoutEvidenceStatus.NotApplicable, "not_framework_element");
        }

        LayoutRect? renderBoundsInParent = null;
        if (parent is null)
        {
            evidence.Add(
                xpath,
                "renderBoundsInParentWpfDips",
                LayoutEvidenceStatus.NotApplicable,
                "no_parent");
        }
        else
        {
            renderBoundsInParent = TryGetRenderBoundsInAncestor(
                element,
                parent,
                xpath,
                "renderBoundsInParentWpfDips",
                evidence);
        }

        var renderBoundsInWindow = ReferenceEquals(element, window)
            ? ToLayoutRect(new WpfRect(new Point(0, 0), uiElement.RenderSize))
            : TryGetRenderBoundsInAncestor(
                element,
                window,
                xpath,
                "renderBoundsInWindowWpfDips",
                evidence);
        var screenBounds = TryGetTransformedScreenBounds(uiElement, xpath, evidence);

        var (dpiScaleX, dpiScaleY) = TryGetLayoutDpiScales(element, xpath, evidence);

        bool? clipToBounds = null;
        bool? hasExplicitClip = null;
        bool? explicitClipIsEmpty = null;
        LayoutRect? explicitClip = null;
        try
        {
            clipToBounds = uiElement.ClipToBounds;
            var clip = uiElement.Clip;
            hasExplicitClip = clip is not null;
            if (clip is not null)
            {
                var clipBounds = clip.Bounds;
                explicitClipIsEmpty = clipBounds.IsEmpty;
                if (!clipBounds.IsEmpty)
                {
                    explicitClip = ToLayoutRect(clipBounds);
                    if (explicitClip is null)
                    {
                        evidence.Add(xpath, "clipping.explicitClip", LayoutEvidenceStatus.Unavailable, "clip_bounds_invalid");
                    }
                }
            }
        }
        catch
        {
            evidence.Add(xpath, "clipping.explicitClip", LayoutEvidenceStatus.Unavailable, "clip_read_failed");
        }

        var renderTransform = ToLayoutTransform(uiElement.RenderTransform, xpath, "renderTransform", evidence);
        LayoutPoint? renderTransformOrigin = null;
        try
        {
            var origin = uiElement.RenderTransformOrigin;
            if (double.IsFinite(origin.X) && double.IsFinite(origin.Y))
            {
                renderTransformOrigin = new LayoutPoint(origin.X, origin.Y);
            }
            else
            {
                evidence.Add(xpath, "renderTransformOrigin", LayoutEvidenceStatus.Unavailable, "non_finite_value");
            }
        }
        catch
        {
            evidence.Add(xpath, "renderTransformOrigin", LayoutEvidenceStatus.Unavailable, "transform_origin_read_failed");
        }

        int? zIndex = null;
        if (parent is Panel)
        {
            try
            {
                zIndex = Panel.GetZIndex(uiElement);
            }
            catch
            {
                evidence.Add(xpath, "zIndex", LayoutEvidenceStatus.Unavailable, "z_index_read_failed");
            }
        }
        else if (parent is null)
        {
            evidence.Add(xpath, "zIndex", LayoutEvidenceStatus.NotApplicable, "no_parent");
        }
        else
        {
            evidence.Add(xpath, "zIndex", LayoutEvidenceStatus.NotApplicable, "parent_not_panel");
        }

        return new LayoutElementMetrics(
            VisualIndexInParent: visualIndexInParent,
            DesiredSizeWpfDips: desiredSize,
            RenderSizeWpfDips: renderSize,
            ActualSizeWpfDips: actualSize,
            ConfiguredWidthWpfDips: configuredWidth,
            ConfiguredHeightWpfDips: configuredHeight,
            MinimumSizeWpfDips: minimumSize,
            MaximumWidthWpfDips: maximumWidth,
            MaximumHeightWpfDips: maximumHeight,
            MarginWpfDips: margin,
            PaddingWpfDips: padding,
            Alignment: alignment,
            Visibility: new LayoutVisibilityInfo(
                Visibility: uiElement.Visibility.ToString(),
                IsVisible: uiElement.IsVisible,
                IsMeasureValid: uiElement.IsMeasureValid,
                IsArrangeValid: uiElement.IsArrangeValid),
            Geometry: new LayoutGeometryInfo(
                LayoutSlotInParentWpfDips: layoutSlot,
                RenderBoundsInParentWpfDips: renderBoundsInParent,
                RenderBoundsInWindowWpfDips: renderBoundsInWindow,
                ScreenBoundsPhysicalPixels: screenBounds,
                DpiScaleX: dpiScaleX,
                DpiScaleY: dpiScaleY),
            Clipping: new LayoutClippingInfo(
                ClipToBounds: clipToBounds,
                HasExplicitClip: hasExplicitClip,
                ExplicitClipIsEmpty: explicitClipIsEmpty,
                ExplicitClipBoundsLocalWpfDips: explicitClip,
                HasLayoutClip: hasLayoutClip,
                LayoutClipIsEmpty: layoutClipIsEmpty,
                LayoutClipBoundsLocalWpfDips: layoutClip),
            LayoutTransform: layoutTransform,
            RenderTransform: renderTransform,
            RenderTransformOrigin: renderTransformOrigin,
            ZIndex: zIndex,
            TemplatedParent: BuildTemplatedParentSummary(element));
    }

    private static (double? DpiScaleX, double? DpiScaleY) TryGetLayoutDpiScales(
        DependencyObject element,
        string xpath,
        LayoutEvidenceCollector evidence)
    {
        if (element is not Visual visual)
        {
            evidence.Add(xpath, "geometry.dpi", LayoutEvidenceStatus.Unsupported, "not_visual");
            return (null, null);
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            if (double.IsFinite(dpi.DpiScaleX) && double.IsFinite(dpi.DpiScaleY))
            {
                return (dpi.DpiScaleX, dpi.DpiScaleY);
            }

            evidence.Add(xpath, "geometry.dpi", LayoutEvidenceStatus.Unavailable, "non_finite_value");
        }
        catch
        {
            evidence.Add(xpath, "geometry.dpi", LayoutEvidenceStatus.Unavailable, "dpi_read_failed");
        }

        return (null, null);
    }

    private static LayoutElementIdentity BuildLayoutIdentity(DependencyObject element, string xpath)
    {
        var type = BoundLayoutString(element.GetType().Name, MaxLayoutTypeLength, out var typeTruncated);
        var boundedXPath = BoundLayoutString(xpath, MaxLayoutXPathLength, out var xpathTruncated);
        var automationId = BoundOptionalLayoutString(
            GetAutomationId(element),
            MaxLayoutIdentityValueLength,
            out var automationIdTruncated);
        var name = BoundOptionalLayoutString(
            GetName(element),
            MaxLayoutIdentityValueLength,
            out var nameTruncated);
        var className = BoundOptionalLayoutString(
            element.GetType().FullName,
            MaxLayoutIdentityValueLength,
            out var classNameTruncated);
        return new LayoutElementIdentity(
            Type: type,
            XPath: boundedXPath,
            AutomationId: automationId,
            Name: name,
            ClassName: className,
            IdentityTruncated: typeTruncated || xpathTruncated || automationIdTruncated || nameTruncated || classNameTruncated);
    }

    private static LayoutElementSummary? BuildTemplatedParentSummary(DependencyObject element)
    {
        DependencyObject? templatedParent = element switch
        {
            FrameworkElement frameworkElement => frameworkElement.TemplatedParent,
            FrameworkContentElement frameworkContentElement => frameworkContentElement.TemplatedParent,
            _ => null
        };
        if (templatedParent is null)
        {
            return null;
        }

        var type = BoundLayoutString(templatedParent.GetType().Name, MaxLayoutTypeLength, out var typeTruncated);
        var automationId = BoundOptionalLayoutString(
            GetAutomationId(templatedParent),
            MaxLayoutIdentityValueLength,
            out var automationIdTruncated);
        var name = BoundOptionalLayoutString(
            GetName(templatedParent),
            MaxLayoutIdentityValueLength,
            out var nameTruncated);
        var className = BoundOptionalLayoutString(
            templatedParent.GetType().FullName,
            MaxLayoutIdentityValueLength,
            out var classNameTruncated);
        return new LayoutElementSummary(
            Type: type,
            AutomationId: automationId,
            Name: name,
            ClassName: className,
            IdentityTruncated: typeTruncated || automationIdTruncated || nameTruncated || classNameTruncated);
    }

    private static LayoutThickness? TryGetPadding(
        FrameworkElement element,
        string xpath,
        LayoutEvidenceCollector evidence)
    {
        Thickness? padding;
        try
        {
            padding = element switch
            {
                Control control => control.Padding,
                Border border => border.Padding,
                TextBlock textBlock => textBlock.Padding,
                _ => null
            };
        }
        catch
        {
            evidence.Add(xpath, "paddingWpfDips", LayoutEvidenceStatus.Unavailable, "padding_read_failed");
            return null;
        }

        if (padding is null)
        {
            evidence.Add(xpath, "paddingWpfDips", LayoutEvidenceStatus.NotApplicable, "type_has_no_padding");
            return null;
        }

        return ToLayoutThickness(padding.Value, xpath, "paddingWpfDips", evidence);
    }

    private static LayoutRect? TryGetLayoutSlot(
        DependencyObject element,
        string xpath,
        LayoutEvidenceCollector evidence,
        bool addUnsupportedEvidence)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            if (addUnsupportedEvidence)
            {
                evidence.Add(xpath, "layoutSlotInParentWpfDips", LayoutEvidenceStatus.Unsupported, "not_framework_element");
            }

            return null;
        }

        try
        {
            var converted = ToLayoutRect(LayoutInformation.GetLayoutSlot(frameworkElement));
            if (converted is null)
            {
                evidence.Add(xpath, "layoutSlotInParentWpfDips", LayoutEvidenceStatus.Unavailable, "layout_slot_invalid");
            }

            return converted;
        }
        catch
        {
            evidence.Add(xpath, "layoutSlotInParentWpfDips", LayoutEvidenceStatus.Unavailable, "layout_slot_read_failed");
            return null;
        }
    }

    private static LayoutRect? TryGetRenderBoundsInAncestor(
        DependencyObject element,
        DependencyObject ancestor,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (element is not Visual visual || ancestor is not Visual ancestorVisual || element is not UIElement uiElement)
        {
            evidence.Add(xpath, field, LayoutEvidenceStatus.Unsupported, "visual_transform_not_supported");
            return null;
        }

        try
        {
            var bounds = visual.TransformToAncestor(ancestorVisual)
                .TransformBounds(new WpfRect(new Point(0, 0), uiElement.RenderSize));
            var converted = ToLayoutRect(bounds);
            if (converted is null)
            {
                evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "visual_bounds_invalid");
            }

            return converted;
        }
        catch
        {
            evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "visual_transform_failed");
            return null;
        }
    }

    private static ContractRect? TryGetTransformedScreenBounds(
        UIElement element,
        string xpath,
        LayoutEvidenceCollector evidence)
    {
        try
        {
            var size = element.RenderSize;
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height))
            {
                evidence.Add(xpath, "screenBoundsPhysicalPixels", LayoutEvidenceStatus.Unavailable, "non_finite_value");
                return null;
            }

            var points = new[]
            {
                element.PointToScreen(new Point(0, 0)),
                element.PointToScreen(new Point(size.Width, 0)),
                element.PointToScreen(new Point(0, size.Height)),
                element.PointToScreen(new Point(size.Width, size.Height))
            };
            if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            {
                evidence.Add(xpath, "screenBoundsPhysicalPixels", LayoutEvidenceStatus.Unavailable, "non_finite_value");
                return null;
            }

            var left = Math.Floor(points.Min(point => point.X));
            var top = Math.Floor(points.Min(point => point.Y));
            var right = Math.Ceiling(points.Max(point => point.X));
            var bottom = Math.Ceiling(points.Max(point => point.Y));
            var width = right - left;
            var height = bottom - top;
            if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue ||
                width < 0 || height < 0 || width > int.MaxValue || height > int.MaxValue)
            {
                evidence.Add(xpath, "screenBoundsPhysicalPixels", LayoutEvidenceStatus.Unavailable, "value_out_of_range");
                return null;
            }

            return new ContractRect(
                X: checked((int)left),
                Y: checked((int)top),
                Width: checked((int)width),
                Height: checked((int)height));
        }
        catch
        {
            evidence.Add(xpath, "screenBoundsPhysicalPixels", LayoutEvidenceStatus.Unavailable, "point_to_screen_failed");
            return null;
        }
    }

    private static LayoutTransformInfo? ToLayoutTransform(
        Transform? transform,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (transform is null)
        {
            evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "transform_not_available");
            return null;
        }

        try
        {
            var matrix = transform.Value;
            var values = new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY };
            if (values.Any(value => !double.IsFinite(value)))
            {
                evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "non_finite_value");
                return null;
            }

            var type = BoundLayoutString(transform.GetType().Name, MaxLayoutTypeLength, out _);
            return new LayoutTransformInfo(
                Type: type,
                Matrix: new LayoutMatrix(
                    matrix.M11,
                    matrix.M12,
                    matrix.M21,
                    matrix.M22,
                    matrix.OffsetX,
                    matrix.OffsetY),
                IsIdentity: matrix.IsIdentity);
        }
        catch
        {
            evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "transform_read_failed");
            return null;
        }
    }

    private static LayoutLength? ToConfiguredLayoutLength(
        double value,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (double.IsNaN(value))
        {
            return new LayoutLength(LayoutLengthKind.Auto);
        }

        if (double.IsPositiveInfinity(value))
        {
            return new LayoutLength(LayoutLengthKind.Unbounded);
        }

        if (double.IsFinite(value))
        {
            return new LayoutLength(LayoutLengthKind.Value, value);
        }

        evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "unexpected_non_finite_value");
        return null;
    }

    private static LayoutLength? ToMaximumLayoutLength(
        double value,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (double.IsPositiveInfinity(value))
        {
            return new LayoutLength(LayoutLengthKind.Unbounded);
        }

        if (double.IsFinite(value))
        {
            return new LayoutLength(LayoutLengthKind.Value, value);
        }

        evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "unexpected_non_finite_value");
        return null;
    }

    private static LayoutSize? ToLayoutSize(
        Size size,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence) =>
        ToLayoutSize(size.Width, size.Height, xpath, field, evidence);

    private static LayoutSize? ToLayoutSize(
        double width,
        double height,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (double.IsFinite(width) && double.IsFinite(height))
        {
            return new LayoutSize(width, height);
        }

        evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "non_finite_value");
        return null;
    }

    private static LayoutThickness? ToLayoutThickness(
        Thickness thickness,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (double.IsFinite(thickness.Left) &&
            double.IsFinite(thickness.Top) &&
            double.IsFinite(thickness.Right) &&
            double.IsFinite(thickness.Bottom))
        {
            return new LayoutThickness(thickness.Left, thickness.Top, thickness.Right, thickness.Bottom);
        }

        evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "non_finite_value");
        return null;
    }

    private static LayoutRect? ToLayoutRect(WpfRect? rect)
    {
        if (rect is null || rect.Value.IsEmpty)
        {
            return null;
        }

        var value = rect.Value;
        return double.IsFinite(value.X) &&
               double.IsFinite(value.Y) &&
               double.IsFinite(value.Width) &&
               double.IsFinite(value.Height)
            ? new LayoutRect(value.X, value.Y, value.Width, value.Height)
            : null;
    }

    private static double? ToFiniteOrUnavailable(
        double value,
        string xpath,
        string field,
        LayoutEvidenceCollector evidence)
    {
        if (double.IsFinite(value))
        {
            return value;
        }

        evidence.Add(xpath, field, LayoutEvidenceStatus.Unavailable, "non_finite_value");
        return null;
    }

    private static string BoundLayoutString(string? value, int maxLength, out bool truncated)
        => LayoutContextText.TruncateAtValidUtf16Boundary(value, maxLength, out truncated);

    private static string? BoundOptionalLayoutString(string? value, int maxLength, out bool truncated)
    {
        if (string.IsNullOrEmpty(value))
        {
            truncated = false;
            return null;
        }

        return BoundLayoutString(value, maxLength, out truncated);
    }

    private static int SaturatingSum<T>(IEnumerable<T> items, Func<T, int> selector)
    {
        long total = 0;
        foreach (var item in items)
        {
            total += selector(item);
            if (total >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)total;
    }

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;
}
