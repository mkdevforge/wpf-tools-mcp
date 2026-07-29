using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;
using ContractRect = WpfToolsMcp.Contracts.Rect;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const int MaximumScreenshotCorrelationCandidates = 25;
    private const int MaximumScreenshotCorrelationNodes = 200_000;
    private const int MaximumScreenshotCorrelationAncestors = 20;

    public static CorrelateWpfScreenshotRegionResponse CorrelateScreenshotRegion(
        string ownerId,
        CorrelateWpfScreenshotRegionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);

        ValidateScreenshotCorrelationRequest(request);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var scan = ScanScreenshotCorrelationTree(
            window,
            treeService,
            request.ScreenRegionPhysicalPixels,
            request.MaxNodes,
            cancellationToken);

        var matches = new List<WpfScreenshotCorrelationMatch>();
        var matchesByElement = new Dictionary<DependencyObject, WpfScreenshotCorrelationMatch>(
            ReferenceEqualityComparer.Instance);

        if (request.ScreenPointPhysicalPixels is { } screenPoint)
        {
            var directHit = PickWpfDependencyObjectAtPoint(
                window,
                screenPoint.X,
                screenPoint.Y);
            var directNode = ResolveScreenshotCorrelationHit(directHit, window, scan.NodesByElement);
            if (directNode is not null)
            {
                AddScreenshotCorrelationMatch(
                    matches,
                    matchesByElement,
                    directNode,
                    ScreenshotCorrelationMatchKind.DirectHit,
                    request.ScreenRegionPhysicalPixels);
            }
        }

        var renderedHits = HitTestScreenshotCorrelationRegion(
            window,
            request.ScreenRegionPhysicalPixels,
            request.ScreenPointPhysicalPixels,
            scan.NodesByElement,
            cancellationToken);
        foreach (var renderedNode in renderedHits.Nodes)
        {
            AddScreenshotCorrelationMatch(
                matches,
                matchesByElement,
                renderedNode,
                ScreenshotCorrelationMatchKind.RenderedHit,
                request.ScreenRegionPhysicalPixels);
        }

        if (!renderedHits.Succeeded)
        {
            foreach (var node in scan.Nodes)
            {
                if (node.Bounds is null ||
                    !MatchesScreenshotCorrelationQuery(
                        node.Bounds,
                        request.ScreenRegionPhysicalPixels,
                        request.ScreenPointPhysicalPixels))
                {
                    continue;
                }

                AddScreenshotCorrelationMatch(
                    matches,
                    matchesByElement,
                    node,
                    ScreenshotCorrelationMatchKind.BoundsIntersection,
                    request.ScreenRegionPhysicalPixels);
            }
        }

        var relevantMatches = RemoveScreenshotCorrelationContainers(matches);
        var discoveredCandidates = relevantMatches.Count;
        var returnedMatches = relevantMatches.Take(request.MaxCandidates).ToArray();
        var hasOverlaps = HasOverlappingScreenshotCorrelationCandidates(
            relevantMatches,
            request.ScreenPointPhysicalPixels is not null);
        var candidates = new ScreenshotCorrelationCandidate[returnedMatches.Length];
        int? directHitIndex = null;

        for (var index = 0; index < returnedMatches.Length; index++)
        {
            var match = returnedMatches[index];
            var candidateIndex = index + 1;
            var ancestors = request.IncludeAncestors
                ? BuildScreenshotCorrelationAncestors(
                    ownerId,
                    match.Node,
                    scan.NodesByXPath,
                    request.MaxAncestors)
                : null;

            candidates[index] = new ScreenshotCorrelationCandidate(
                Index: candidateIndex,
                Backend: InspectionBackend.Wpf,
                Element: BuildElementRefWpf(
                    ownerId,
                    match.Node.Element,
                    match.Node.XPath,
                    FindReturnFields.Standard) with
                {
                    Bounds = match.Node.Bounds
                },
                MatchKind: match.MatchKind,
                IntersectionPhysicalPixels: match.Intersection,
                Ancestors: ancestors,
                Annotation: null);

            if (match.MatchKind == ScreenshotCorrelationMatchKind.DirectHit)
            {
                directHitIndex = candidateIndex;
            }
        }

        var candidatesTruncated = discoveredCandidates > candidates.Length;
        var truncated = !scan.ScanComplete || candidatesTruncated;
        var truncatedReason = !scan.ScanComplete
            ? "maxNodes"
            : candidatesTruncated
                ? "maxCandidates"
                : null;

        return new CorrelateWpfScreenshotRegionResponse(
            new ScreenshotCorrelationBackendResult(
                Backend: InspectionBackend.Wpf,
                Candidates: candidates,
                ReturnedCandidates: candidates.Length,
                DiscoveredCandidates: discoveredCandidates,
                ScannedNodes: scan.ScannedNodes,
                ScanComplete: scan.ScanComplete,
                Truncated: truncated,
                TruncatedReason: truncatedReason,
                DirectHitIndex: directHitIndex,
                HasOverlaps: hasOverlaps));
    }

    private static void ValidateScreenshotCorrelationRequest(CorrelateWpfScreenshotRegionRequest request)
    {
        var region = request.ScreenRegionPhysicalPixels;
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ScreenRegionPhysicalPixels),
                "Screenshot correlation region dimensions must be positive.");
        }

        if (request.ScreenPointPhysicalPixels is { } point &&
            !ContainsScreenshotCorrelationPoint(region, point))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ScreenPointPhysicalPixels),
                "Screenshot correlation point must be inside the mapped screen region.");
        }

        if (request.MaxCandidates is < 1 or > MaximumScreenshotCorrelationCandidates)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxCandidates),
                $"maxCandidates must be between 1 and {MaximumScreenshotCorrelationCandidates}.");
        }

        if (request.MaxNodes is < 1 or > MaximumScreenshotCorrelationNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxNodes),
                $"maxNodes must be between 1 and {MaximumScreenshotCorrelationNodes}.");
        }

        if (request.MaxAncestors is < 0 or > MaximumScreenshotCorrelationAncestors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxAncestors),
                $"maxAncestors must be between 0 and {MaximumScreenshotCorrelationAncestors}.");
        }
    }

    private static WpfScreenshotCorrelationScan ScanScreenshotCorrelationTree(
        Window window,
        VisualTreeService treeService,
        ContractRect screenRegion,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var nodes = new List<WpfScreenshotCorrelationNode>(Math.Min(maxNodes, 1024));
        var nodesByElement = new Dictionary<DependencyObject, WpfScreenshotCorrelationNode>(
            ReferenceEqualityComparer.Instance);
        var nodesByXPath = new Dictionary<string, WpfScreenshotCorrelationNode>(StringComparer.Ordinal);
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(DependencyObject Element, string XPath)>();
        stack.Push((window, "/Window"));
        var scannedNodes = 0;

        while (stack.Count > 0 && scannedNodes < maxNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, xpath) = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            scannedNodes++;
            if (!ReferenceEquals(current, window) &&
                !ShouldIncludeWpfElement(
                    current,
                    visibleOnly: true,
                    includeOffViewport: true,
                    viewportBounds: screenRegion))
            {
                continue;
            }

            var node = new WpfScreenshotCorrelationNode(
                current,
                xpath,
                GetScreenshotCorrelationBounds(current),
                GetScreenshotCorrelationIdentityRank(current));
            nodes.Add(node);
            nodesByElement.Add(current, node);
            nodesByXPath.Add(xpath, node);

            var children = GetChildrenWpf(
                current,
                treeService,
                visibleOnly: true,
                includeOffViewport: true,
                viewportBounds: screenRegion);
            PushScreenshotCorrelationChildren(stack, children, xpath);
        }

        return new WpfScreenshotCorrelationScan(
            Nodes: nodes,
            NodesByElement: nodesByElement,
            NodesByXPath: nodesByXPath,
            ScannedNodes: scannedNodes,
            ScanComplete: stack.Count == 0);
    }

    private static void PushScreenshotCorrelationChildren(
        Stack<(DependencyObject Element, string XPath)> stack,
        IReadOnlyList<DependencyObject> children,
        string parentXPath)
    {
        if (children.Count == 0)
        {
            return;
        }

        var labels = children.Select(GetXPathLabel).ToArray();
        var countsByLabel = labels
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var reverseIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = children.Count - 1; index >= 0; index--)
        {
            var child = children[index];
            var label = labels[index];
            reverseIndexByLabel.TryGetValue(label, out var reverseIndex);
            reverseIndex++;
            reverseIndexByLabel[label] = reverseIndex;

            var segment = label;
            if (countsByLabel[label] > 1)
            {
                var forwardIndex = countsByLabel[label] - reverseIndex + 1;
                segment = $"{label}[{forwardIndex}]";
            }

            stack.Push((child, $"{parentXPath}/{segment}"));
        }
    }

    private static WpfScreenshotCorrelationHitTest HitTestScreenshotCorrelationRegion(
        Window window,
        ContractRect screenRegion,
        ScreenshotCorrelationPoint? screenPoint,
        IReadOnlyDictionary<DependencyObject, WpfScreenshotCorrelationNode> nodesByElement,
        CancellationToken cancellationToken)
    {
        var hits = new List<WpfScreenshotCorrelationNode>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);

        HitTestFilterBehavior Filter(DependencyObject candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return nodesByElement.ContainsKey(candidate)
                ? HitTestFilterBehavior.Continue
                : HitTestFilterBehavior.ContinueSkipSelfAndChildren;
        }

        HitTestResultBehavior Collect(HitTestResult result)
        {
            var node = ResolveScreenshotCorrelationHit(result.VisualHit, window, nodesByElement);
            if (node is not null && seen.Add(node.Element))
            {
                hits.Add(node);
            }

            return HitTestResultBehavior.Continue;
        }

        try
        {
            if (screenPoint is not null)
            {
                var clientPoint = window.PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
                VisualTreeHelper.HitTest(
                    window,
                    Filter,
                    Collect,
                    new PointHitTestParameters(clientPoint));
            }
            else
            {
                var topLeft = window.PointFromScreen(new Point(screenRegion.X, screenRegion.Y));
                var bottomRight = window.PointFromScreen(new Point(
                    (long)screenRegion.X + screenRegion.Width,
                    (long)screenRegion.Y + screenRegion.Height));
                var clientRegion = new System.Windows.Rect(
                    new Point(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y)),
                    new Point(Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y)));

                VisualTreeHelper.HitTest(
                    window,
                    Filter,
                    Collect,
                    new GeometryHitTestParameters(new RectangleGeometry(clientRegion)));
            }

            return new WpfScreenshotCorrelationHitTest(hits, Succeeded: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Bounds correlation still provides useful evidence when WPF hit testing is unavailable.
            return new WpfScreenshotCorrelationHitTest(hits, Succeeded: false);
        }
    }

    private static WpfScreenshotCorrelationNode? ResolveScreenshotCorrelationHit(
        DependencyObject hit,
        Window window,
        IReadOnlyDictionary<DependencyObject, WpfScreenshotCorrelationNode> nodesByElement)
    {
        var current = PromotePickedWpfElement(hit, window);
        WpfScreenshotCorrelationNode? nearestNode = null;
        var safety = 0;

        while (current is not null && safety++ < 2048)
        {
            if (nodesByElement.TryGetValue(current, out var node) && node.Bounds is not null)
            {
                nearestNode ??= node;
                if (node.IdentityRank > 0 || ReferenceEquals(current, window))
                {
                    return node;
                }
            }

            if (ReferenceEquals(current, window))
            {
                break;
            }

            current = GetScreenshotCorrelationParent(current);
        }

        return nearestNode;
    }

    private static DependencyObject? GetScreenshotCorrelationParent(DependencyObject element)
    {
        try
        {
            if (element is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var visualParent = VisualTreeHelper.GetParent(element);
                if (visualParent is not null)
                {
                    return visualParent;
                }
            }
        }
        catch
        {
        }

        try
        {
            return element switch
            {
                FrameworkContentElement contentElement => contentElement.Parent,
                FrameworkElement frameworkElement => frameworkElement.Parent,
                _ => LogicalTreeHelper.GetParent(element)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void AddScreenshotCorrelationMatch(
        ICollection<WpfScreenshotCorrelationMatch> matches,
        IDictionary<DependencyObject, WpfScreenshotCorrelationMatch> matchesByElement,
        WpfScreenshotCorrelationNode node,
        ScreenshotCorrelationMatchKind matchKind,
        ContractRect screenRegion)
    {
        if (matchesByElement.TryGetValue(node.Element, out var existing))
        {
            if (matchKind < existing.MatchKind)
            {
                existing.MatchKind = matchKind;
            }

            return;
        }

        if (node.Bounds is null)
        {
            return;
        }

        var intersection = IntersectScreenshotCorrelationRects(node.Bounds, screenRegion);
        if (intersection is null)
        {
            return;
        }

        var match = new WpfScreenshotCorrelationMatch(node, matchKind, intersection);
        matches.Add(match);
        matchesByElement.Add(node.Element, match);
    }

    private static IReadOnlyList<WpfScreenshotCorrelationMatch> RemoveScreenshotCorrelationContainers(
        IReadOnlyList<WpfScreenshotCorrelationMatch> matches)
    {
        if (matches.Count < 2)
        {
            return matches;
        }

        var removed = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var matchesByXPath = matches.ToDictionary(match => match.Node.XPath, StringComparer.Ordinal);

        foreach (var descendant in matches)
        {
            var ancestorXPath = GetScreenshotCorrelationParentXPath(descendant.Node.XPath);
            while (ancestorXPath is not null)
            {
                if (matchesByXPath.TryGetValue(ancestorXPath, out var ancestor))
                {
                    if (ancestor.MatchKind != ScreenshotCorrelationMatchKind.DirectHit)
                    {
                        removed.Add(ancestor.Node.Element);
                    }
                }

                ancestorXPath = GetScreenshotCorrelationParentXPath(ancestorXPath);
            }
        }

        return matches.Where(match => !removed.Contains(match.Node.Element)).ToArray();
    }

    private static IReadOnlyList<ElementRef> BuildScreenshotCorrelationAncestors(
        string ownerId,
        WpfScreenshotCorrelationNode node,
        IReadOnlyDictionary<string, WpfScreenshotCorrelationNode> nodesByXPath,
        int maxAncestors)
    {
        if (maxAncestors == 0)
        {
            return [];
        }

        var ancestors = new List<ElementRef>(maxAncestors);
        var parentXPath = GetScreenshotCorrelationParentXPath(node.XPath);
        while (parentXPath is not null && ancestors.Count < maxAncestors)
        {
            if (nodesByXPath.TryGetValue(parentXPath, out var parent))
            {
                ancestors.Add(BuildElementRefWpf(
                    ownerId,
                    parent.Element,
                    parent.XPath,
                    FindReturnFields.Standard) with
                {
                    Bounds = parent.Bounds
                });
            }

            parentXPath = GetScreenshotCorrelationParentXPath(parentXPath);
        }

        return ancestors;
    }

    private static string? GetScreenshotCorrelationParentXPath(string xpath)
    {
        var separator = xpath.LastIndexOf('/');
        return separator <= 0 ? null : xpath[..separator];
    }

    private static int GetScreenshotCorrelationIdentityRank(DependencyObject element)
    {
        if (!string.IsNullOrWhiteSpace(GetAutomationId(element)))
        {
            return 3;
        }

        if (!string.IsNullOrWhiteSpace(GetName(element)))
        {
            return 2;
        }

        return element is Control ? 1 : 0;
    }

    private static ContractRect? GetScreenshotCorrelationBounds(DependencyObject element)
    {
        if (element is not UIElement uiElement)
        {
            return GetBoundsWpf(element);
        }

        try
        {
            var size = uiElement.RenderSize;
            var width = size.Width;
            var height = size.Height;
            if (element is FrameworkElement frameworkElement &&
                frameworkElement.ActualWidth > 0 &&
                frameworkElement.ActualHeight > 0)
            {
                width = frameworkElement.ActualWidth;
                height = frameworkElement.ActualHeight;
            }

            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            {
                return GetBoundsWpf(element);
            }

            var corners = new[]
            {
                uiElement.PointToScreen(new Point(0, 0)),
                uiElement.PointToScreen(new Point(width, 0)),
                uiElement.PointToScreen(new Point(0, height)),
                uiElement.PointToScreen(new Point(width, height))
            };
            if (corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            {
                return GetBoundsWpf(element);
            }

            var left = Math.Floor(corners.Min(point => point.X));
            var top = Math.Floor(corners.Min(point => point.Y));
            var right = Math.Ceiling(corners.Max(point => point.X));
            var bottom = Math.Ceiling(corners.Max(point => point.Y));
            if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue)
            {
                return GetBoundsWpf(element);
            }

            var physicalWidth = right - left;
            var physicalHeight = bottom - top;
            return physicalWidth > 0 && physicalHeight > 0
                ? new ContractRect(
                    checked((int)left),
                    checked((int)top),
                    checked((int)physicalWidth),
                    checked((int)physicalHeight))
                : GetBoundsWpf(element);
        }
        catch
        {
            return GetBoundsWpf(element);
        }
    }

    private static bool HasOverlappingScreenshotCorrelationCandidates(
        IReadOnlyList<WpfScreenshotCorrelationMatch> matches,
        bool isPointQuery)
    {
        if (isPointQuery)
        {
            return matches.Count > 1;
        }

        return ScreenshotCorrelationOverlap.HasAnyOverlap(matches.Select(match => match.Intersection));
    }

    private static bool MatchesScreenshotCorrelationQuery(
        ContractRect bounds,
        ContractRect screenRegion,
        ScreenshotCorrelationPoint? screenPoint) =>
        screenPoint is not null
            ? ContainsScreenshotCorrelationPoint(bounds, screenPoint)
            : IntersectScreenshotCorrelationRects(bounds, screenRegion) is not null;

    private static bool ContainsScreenshotCorrelationPoint(
        ContractRect bounds,
        ScreenshotCorrelationPoint point) =>
        bounds.Width > 0 &&
        bounds.Height > 0 &&
        point.X >= bounds.X &&
        point.X < (long)bounds.X + bounds.Width &&
        point.Y >= bounds.Y &&
        point.Y < (long)bounds.Y + bounds.Height;

    private static ContractRect? IntersectScreenshotCorrelationRects(ContractRect first, ContractRect second)
    {
        if (first.Width <= 0 || first.Height <= 0 || second.Width <= 0 || second.Height <= 0)
        {
            return null;
        }

        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min((long)first.X + first.Width, (long)second.X + second.Width);
        var bottom = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height);

        return right > left && bottom > top
            ? new ContractRect(left, top, checked((int)(right - left)), checked((int)(bottom - top)))
            : null;
    }

    private sealed record WpfScreenshotCorrelationNode(
        DependencyObject Element,
        string XPath,
        ContractRect? Bounds,
        int IdentityRank);

    private sealed class WpfScreenshotCorrelationMatch(
        WpfScreenshotCorrelationNode node,
        ScreenshotCorrelationMatchKind matchKind,
        ContractRect intersection)
    {
        public WpfScreenshotCorrelationNode Node { get; } = node;

        public ScreenshotCorrelationMatchKind MatchKind { get; set; } = matchKind;

        public ContractRect Intersection { get; } = intersection;
    }

    private sealed record WpfScreenshotCorrelationScan(
        IReadOnlyList<WpfScreenshotCorrelationNode> Nodes,
        IReadOnlyDictionary<DependencyObject, WpfScreenshotCorrelationNode> NodesByElement,
        IReadOnlyDictionary<string, WpfScreenshotCorrelationNode> NodesByXPath,
        int ScannedNodes,
        bool ScanComplete);

    private sealed record WpfScreenshotCorrelationHitTest(
        IReadOnlyList<WpfScreenshotCorrelationNode> Nodes,
        bool Succeeded);
}
