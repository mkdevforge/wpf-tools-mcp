using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public const int DefaultUiaMappingMaxNodes = 5_000;
    internal const int MaximumUiaMappingNodes = 50_000;
    private const string WpfUiaMappingMethod = "scoredWindowScan";

    private sealed record RankedWpfUiaCandidate(
        AutomationElement Element,
        string XPath,
        int[]? RuntimeId,
        string ElementType,
        string? AutomationId,
        string? Name,
        string? ClassName,
        Rect Bounds,
        ElementMappingScoring.CandidateScore Ranking);

    private sealed record WpfUiaScan(
        IReadOnlyList<RankedWpfUiaCandidate> Candidates,
        IReadOnlyList<AutomationElement> Elements,
        int ScannedNodes,
        bool Complete,
        string? IncompleteReason);

    private sealed record RegisteredUiaCandidate(
        AutomationElement Element,
        string ElementId,
        IReadOnlyList<string> Evidence);

    private sealed record WpfUiaMappingResult(
        AutomationElement? SelectedElement,
        string? SelectedXPath,
        string? SelectedElementId,
        IReadOnlyList<AutomationElement> ScannedElements,
        UiaMappingDiagnostics Diagnostics);

    private static ElementHandle RefreshWpfMappingSource(ElementHandle handle, ElementRef current) =>
        handle with
        {
            XPath = current.XPath,
            WpfAgentElementId = string.IsNullOrWhiteSpace(current.ElementIdWpf)
                ? handle.WpfAgentElementId
                : current.ElementIdWpf,
            Type = string.IsNullOrWhiteSpace(current.Type) ? handle.Type : current.Type,
            AutomationId = string.IsNullOrWhiteSpace(current.AutomationId) ? handle.AutomationId : current.AutomationId,
            Name = string.IsNullOrWhiteSpace(current.Name) ? handle.Name : current.Name,
            ClassName = string.IsNullOrWhiteSpace(current.ClassName) ? handle.ClassName : current.ClassName,
            Bounds = current.Bounds ?? handle.Bounds
        };

    internal static void ValidateUiaMappingWindowScope(long? requestedWindowHandle, long elementWindowHandle)
    {
        if (requestedWindowHandle is long requested && requested != elementWindowHandle)
        {
            throw new ArgumentException("windowHandle does not match the elementId window.");
        }
    }

    private WpfUiaMappingResult MapWpfHandleToUia(
        Window window,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        ElementHandle source,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var scan = ScanWpfUiaCandidates(
            window,
            controlWalker,
            rawWalker,
            source,
            maxNodes,
            cancellationToken);
        var ordered = scan.Candidates
            .OrderByDescending(candidate => candidate.Ranking.Score)
            .ThenBy(candidate => GetXPathDepth(candidate.XPath))
            .ThenBy(candidate => candidate.XPath, StringComparer.Ordinal)
            .ToArray();
        var decisionScores = ordered.Select(candidate => candidate.Ranking).ToArray();
        var decision = ElementMappingScoring.Decide(decisionScores, scan.Complete);

        RegisteredUiaCandidate? selectedRegistration = null;
        var selectedRegistrationAttempted = decision.SelectedIndex == 0;
        var selectedRegistrationFailed = false;
        if (selectedRegistrationAttempted)
        {
            selectedRegistration = TryRegisterUiaMappingCandidate(
                window,
                rawWalker,
                source.WindowHandle,
                ordered[0]);
            if (selectedRegistration is null)
            {
                selectedRegistrationFailed = true;
                decisionScores[0] = decisionScores[0] with
                {
                    Reusable = false,
                    Evidence = ReplaceRuntimeEvidence(
                        decisionScores[0].Evidence,
                        "runtime_identity_unverifiable")
                };
                decision = ElementMappingScoring.Decide(decisionScores, scan.Complete);
            }
        }

        var returnedCandidates = new List<UiaMappingCandidate>(
            Math.Min(ordered.Length, MaximumUiaMappingCandidates));
        for (var index = 0; index < Math.Min(ordered.Length, MaximumUiaMappingCandidates); index++)
        {
            var candidate = ordered[index];
            var effectiveRanking = index == 0 && selectedRegistrationAttempted
                ? decisionScores[0]
                : candidate.Ranking;
            var registration = index == 0 && selectedRegistrationAttempted
                ? selectedRegistration
                : TryRegisterUiaMappingCandidate(window, rawWalker, source.WindowHandle, candidate);
            var evidence = registration?.Evidence ??
                (effectiveRanking.Reusable
                    ? ReplaceRuntimeEvidence(effectiveRanking.Evidence, "runtime_identity_unverifiable")
                    : effectiveRanking.Evidence);

            returnedCandidates.Add(new UiaMappingCandidate(
                ElementType: candidate.ElementType,
                AutomationId: candidate.AutomationId,
                Name: candidate.Name,
                ClassName: candidate.ClassName,
                Bounds: candidate.Bounds,
                XPath: candidate.XPath,
                Score: candidate.Ranking.Score)
            {
                ElementId = registration?.ElementId,
                Reusable = registration is not null,
                Evidence = evidence
            });
        }

        var selected = decision.SelectedIndex == 0 && selectedRegistration is not null;
        IReadOnlyList<string> decisionEvidence = selected
            ? decision.Evidence.Concat(["runtime_identity_verified"]).ToArray()
            : selectedRegistrationFailed
                ? ReplaceRuntimeEvidence(decision.Evidence, "runtime_identity_unverifiable")
                : decision.Evidence;
        var candidatesTruncated = ordered.Length > MaximumUiaMappingCandidates;
        var diagnostics = new UiaMappingDiagnostics(
            Ambiguous: decision.Status == ElementMappingStatus.Ambiguous,
            SelectedXPath: selected ? ordered[0].XPath : null,
            Candidates: returnedCandidates,
            ReturnedCandidates: returnedCandidates.Count,
            TotalCandidates: ordered.Length,
            Truncated: !scan.Complete || candidatesTruncated)
        {
            Status = decision.Status,
            Method = WpfUiaMappingMethod,
            SelectedElementId = selected ? selectedRegistration!.ElementId : null,
            Score = decision.Score ?? 0,
            ScoreLead = decision.ScoreLead,
            Evidence = decisionEvidence,
            ScannedNodes = scan.ScannedNodes,
            ScanComplete = scan.Complete,
            TruncatedReason = !scan.Complete
                ? scan.IncompleteReason
                : candidatesTruncated ? "maxCandidates" : null
        };

        return new WpfUiaMappingResult(
            SelectedElement: selected ? selectedRegistration!.Element : null,
            SelectedXPath: selected ? ordered[0].XPath : null,
            SelectedElementId: selected ? selectedRegistration!.ElementId : null,
            ScannedElements: scan.Elements,
            Diagnostics: diagnostics);
    }

    private static WpfUiaScan ScanWpfUiaCandidates(
        Window window,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        ElementHandle source,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RankedWpfUiaCandidate>();
        var elements = new List<AutomationElement>(Math.Min(maxNodes, 1024));
        var pending = new Queue<AutomationElement>();
        pending.Enqueue(window);
        var scannedNodes = 0;
        var complete = true;
        string? incompleteReason = null;
        var sourceFacts = new ElementMappingScoring.Facts(
            source.AutomationId,
            source.Name,
            source.ClassName,
            source.Bounds);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                complete = false;
                incompleteReason = "maxNodes";
                break;
            }

            var element = pending.Dequeue();
            scannedNodes++;
            elements.Add(element);

            try
            {
                var elementType = element.ControlType.ToString();
                var automationId = GetAutomationId(element);
                var name = GetName(element);
                var className = GetClassName(element);
                var bounds = ToRect(element.BoundingRectangle);
                var runtimeId = TryGetRuntimeId(element);
                var reusable = runtimeId is { Length: > 0 };
                var ranking = ElementMappingScoring.Score(
                    sourceFacts,
                    new ElementMappingScoring.Facts(automationId, name, className, bounds),
                    AreWpfAndUiaTypesCompatible(source.Type, elementType),
                    reusable);

                if (ranking is not null)
                {
                    var xpath = ComputeXPath(window, element, rawWalker);
                    candidates.Add(new RankedWpfUiaCandidate(
                        element,
                        xpath,
                        runtimeId,
                        elementType,
                        automationId,
                        name,
                        className,
                        bounds,
                        ranking));
                }
            }
            catch
            {
                complete = false;
                incompleteReason ??= "uiaTraversalUnavailable";
            }

            try
            {
                var child = controlWalker.GetFirstChild(element);
                while (child is not null)
                {
                    if (scannedNodes + pending.Count >= maxNodes)
                    {
                        complete = false;
                        incompleteReason = "maxNodes";
                        break;
                    }

                    pending.Enqueue(child);
                    child = controlWalker.GetNextSibling(child);
                }
            }
            catch
            {
                complete = false;
                incompleteReason ??= "uiaTraversalUnavailable";
            }
        }

        return new WpfUiaScan(
            candidates,
            elements,
            scannedNodes,
            complete && pending.Count == 0,
            incompleteReason);
    }

    private RegisteredUiaCandidate? TryRegisterUiaMappingCandidate(
        Window window,
        ITreeWalker rawWalker,
        long windowHandle,
        RankedWpfUiaCandidate candidate)
    {
        if (candidate.RuntimeId is not { Length: > 0 } expectedRuntimeId)
        {
            return null;
        }

        try
        {
            var resolved = TryResolveByXPath(
                window,
                new ElementLocator(XPath: candidate.XPath),
                rawWalker);
            var actualRuntimeId = resolved is null ? null : TryGetRuntimeId(resolved);
            if (resolved is null ||
                actualRuntimeId is null ||
                !actualRuntimeId.SequenceEqual(expectedRuntimeId))
            {
                return null;
            }

            var elementId = _elementHandles.RegisterUia(
                windowHandle,
                candidate.XPath,
                expectedRuntimeId,
                candidate.ElementType,
                candidate.AutomationId,
                candidate.Name,
                candidate.ClassName,
                candidate.Bounds);
            return new RegisteredUiaCandidate(
                resolved,
                elementId,
                candidate.Ranking.Evidence.Concat(["runtime_identity_verified"]).ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReplaceRuntimeEvidence(
        IReadOnlyList<string> evidence,
        string replacement) =>
        evidence
            .Where(item => !item.StartsWith("runtime_identity_", StringComparison.Ordinal))
            .Append(replacement)
            .ToArray();

    internal static bool AreWpfAndUiaTypesCompatible(
        string? wpfType,
        string? uiaControlType)
    {
        var expected = GetSimpleTypeName(wpfType);
        if (expected is null || string.IsNullOrWhiteSpace(uiaControlType))
        {
            return false;
        }

        if (string.Equals(expected, uiaControlType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var compatibleControlType = expected.ToUpperInvariant() switch
        {
            "TEXTBLOCK" or "LABEL" => "Text",
            "TEXTBOX" or "PASSWORDBOX" or "RICHTEXTBOX" => "Edit",
            "TOGGLEBUTTON" or "REPEATBUTTON" => "Button",
            "LISTBOX" or "LISTVIEW" => "List",
            "LISTBOXITEM" or "LISTVIEWITEM" => "ListItem",
            "TREEVIEW" => "Tree",
            "TREEVIEWITEM" => "TreeItem",
            "TABCONTROL" => "Tab",
            "TABITEM" => "TabItem",
            "MENU" or "CONTEXTMENU" => "Menu",
            "MENUITEM" => "MenuItem",
            "DATAGRID" => "DataGrid",
            "DATAGRIDROW" => "DataItem",
            "DATAGRIDCELL" => "Custom",
            "SCROLLVIEWER" or "GRID" or "STACKPANEL" or "DOCKPANEL" or "WRAPPANEL" or "BORDER" => "Pane",
            _ => null
        };

        return compatibleControlType is not null &&
               string.Equals(compatibleControlType, uiaControlType, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSimpleTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var trimmed = typeName.Trim();
        var separator = Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('+'));
        return separator >= 0 && separator < trimmed.Length - 1
            ? trimmed[(separator + 1)..]
            : trimmed;
    }
}
