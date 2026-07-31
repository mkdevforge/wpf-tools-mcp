using System.Windows;
using System.Windows.Automation.Peers;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;
using ContractRect = WpfToolsMcp.Contracts.Rect;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private const int MaximumReturnedWpfMappingCandidates = 10;
    private const string UiaWpfMappingMethod = "automationPeerScoredWindowScan";

    private sealed record RankedUiaWpfCandidate(
        DependencyObject Element,
        string XPath,
        int TraversalOrdinal,
        ElementMappingScoring.CandidateScore Ranking);

    public static MapUiaToWpfAgentResponse MapUiaToWpf(
        string ownerId,
        MapUiaToWpfAgentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        var maxNodes = Math.Clamp(request.MaxNodes, 1, 50_000);
        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var sourceFacts = new ElementMappingScoring.Facts(
            request.Source.AutomationId,
            request.Source.Name,
            request.Source.ClassName,
            request.Source.Bounds);
        var ranked = new List<RankedUiaWpfCandidate>();
        var stack = new Stack<(DependencyObject Element, string XPath)>();
        stack.Push((window, "/Window"));

        var scannedNodes = 0;
        var scanComplete = true;
        string? truncatedReason = null;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scannedNodes >= maxNodes)
            {
                scanComplete = false;
                truncatedReason = "maxNodes";
                break;
            }

            var (current, xpath) = stack.Pop();
            var traversalOrdinal = scannedNodes++;
            var peerProjection = TryProjectAutomationPeer(current, out var projected);
            if (peerProjection == AutomationPeerProjectionResult.Failed)
            {
                scanComplete = false;
                truncatedReason ??= "automationPeerCreationFailed";
            }
            else if (peerProjection == AutomationPeerProjectionResult.Available)
            {
                var ranking = ElementMappingScoring.Score(
                    sourceFacts,
                    new ElementMappingScoring.Facts(
                        projected.AutomationId,
                        projected.Name,
                        projected.ClassName,
                        projected.Bounds),
                    typeCompatible: string.Equals(
                        projected.ControlType,
                        request.Source.ControlType,
                        StringComparison.OrdinalIgnoreCase) ||
                        ElementMappingScoring.AreWpfAndUiaTypesCompatible(
                            current.GetType().FullName,
                            request.Source.ControlType),
                    reusable: true);
                if (ranking is not null)
                {
                    ranked.Add(new RankedUiaWpfCandidate(
                        current,
                        xpath,
                        traversalOrdinal,
                        ranking with
                        {
                            Evidence = ranking.Evidence.Concat(["automation_peer_projection"]).ToArray()
                        }));
                }
            }

            DependencyObject[] children;
            try
            {
                children = GetChildrenWpf(
                    current,
                    treeService,
                    visibleOnly: false,
                    includeOffViewport: true,
                    viewportBounds: null);
            }
            catch
            {
                scanComplete = false;
                truncatedReason ??= "wpfTraversalUnavailable";
                continue;
            }

            if (children.Length == 0)
            {
                continue;
            }

            var labels = children.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var reverseIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = children.Length - 1; i >= 0; i--)
            {
                var label = labels[i];
                reverseIndexByLabel.TryGetValue(label, out var reverseIndex);
                reverseIndex++;
                reverseIndexByLabel[label] = reverseIndex;
                var segment = countsByLabel[label] > 1
                    ? $"{label}[{countsByLabel[label] - reverseIndex + 1}]"
                    : label;
                stack.Push((children[i], $"{xpath}/{segment}"));
            }
        }

        var ordered = ranked
            .OrderByDescending(candidate => candidate.Ranking.Score)
            .ThenBy(candidate => candidate.TraversalOrdinal)
            .ToArray();
        var decision = ElementMappingScoring.Decide(
            ordered.Select(candidate => candidate.Ranking).ToArray(),
            scanComplete);

        ElementRef? selected = null;
        if (decision.SelectedIndex is int selectedIndex)
        {
            var candidate = ordered[selectedIndex];
            selected = BuildElementRefWpf(
                ownerId,
                candidate.Element,
                candidate.XPath,
                FindReturnFields.Standard,
                includeElementId: true);
        }

        var candidates = ordered
            .Take(MaximumReturnedWpfMappingCandidates)
            .Select(candidate => new WpfMappingCandidate(
                BuildElementRefWpf(
                    ownerId,
                    candidate.Element,
                    candidate.XPath,
                    FindReturnFields.Standard,
                    includeElementId: false),
                candidate.Ranking.Score,
                candidate.Ranking.Evidence))
            .ToArray();
        var truncated = !scanComplete || ordered.Length > candidates.Length;

        return new MapUiaToWpfAgentResponse(
            selected,
            new WpfMappingDiagnostics(
                Available: true,
                Method: UiaWpfMappingMethod,
                Candidates: candidates,
                ReturnedCandidates: candidates.Length,
                TotalCandidates: ordered.Length,
                ScannedNodes: scannedNodes,
                ScanComplete: scanComplete,
                Truncated: truncated,
                TruncatedReason: truncatedReason ?? (ordered.Length > candidates.Length ? "candidateLimit" : null))
            {
                Status = decision.Status,
                SelectedXPath = selected?.XPath,
                Score = decision.Score,
                ScoreLead = decision.ScoreLead,
                Evidence = decision.Evidence
            });
    }

    private static AutomationPeerProjectionResult TryProjectAutomationPeer(
        DependencyObject element,
        out AutomationPeerProjection projection)
    {
        projection = default!;
        AutomationPeer? peer;
        try
        {
            peer = element switch
            {
                UIElement uiElement => UIElementAutomationPeer.CreatePeerForElement(uiElement),
                ContentElement contentElement => ContentElementAutomationPeer.CreatePeerForElement(contentElement),
                _ => null
            };
        }
        catch
        {
            return AutomationPeerProjectionResult.Failed;
        }

        if (peer is null)
        {
            return AutomationPeerProjectionResult.Unavailable;
        }

        projection = new AutomationPeerProjection(
            ReadPeerValue(() => peer.GetAutomationControlType().ToString()) ?? element.GetType().Name,
            ReadPeerValue(peer.GetAutomationId) ?? GetAutomationId(element),
            ReadPeerValue(peer.GetName) ?? GetName(element),
            ReadPeerValue(peer.GetClassName) ?? element.GetType().Name,
            ReadPeerBounds(peer) ?? GetBoundsWpf(element));
        return AutomationPeerProjectionResult.Available;
    }

    private static string? ReadPeerValue(Func<string?> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static ContractRect? ReadPeerBounds(AutomationPeer peer)
    {
        try
        {
            var bounds = peer.GetBoundingRectangle();
            if (bounds.IsEmpty ||
                !double.IsFinite(bounds.X) ||
                !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) ||
                !double.IsFinite(bounds.Height))
            {
                return null;
            }

            return new ContractRect(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height));
        }
        catch
        {
            return null;
        }
    }

    private sealed record AutomationPeerProjection(
        string ControlType,
        string? AutomationId,
        string? Name,
        string? ClassName,
        ContractRect? Bounds);

    private enum AutomationPeerProjectionResult
    {
        Unavailable,
        Available,
        Failed
    }
}
