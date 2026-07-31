using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public const int DefaultUiaMappingMaxNodes = 5_000;
    internal const int MaximumUiaMappingNodes = 50_000;
    private const string WpfUiaMappingMethod = "scoredWindowScan";
    private const string UiaWpfMappingMethod = "automationPeerScoredWindowScan";
    private const string FrameworkClassificationMappingMethod = "frameworkClassification";

    private sealed record RankedWpfUiaCandidate(
        AutomationElement Element,
        int TraversalOrdinal,
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
        bool Complete);

    private sealed record PreparedWpfUiaCandidate(
        RankedWpfUiaCandidate Candidate,
        string? XPath,
        IReadOnlyList<string> Evidence);

    private sealed record RegisteredUiaCandidate(
        AutomationElement Element,
        string ElementId,
        IReadOnlyList<string> Evidence);

    private sealed record WpfUiaMappingResult(
        AutomationElement? SelectedElement,
        string? SelectedXPath,
        string? SelectedFlaUiXPath,
        string? SelectedElementId,
        IReadOnlyList<AutomationElement> ScannedElements,
        UiaMappingDiagnostics Diagnostics);

    private sealed record UiaWpfMappingResult(
        WpfLocatorIdentity? Wpf,
        WpfMappingDiagnostics Diagnostics);

    private sealed record CandidateRegistrationAttempt(
        RegisteredUiaCandidate? Registration,
        string? FailureEvidence,
        string? IncompleteReason);

    internal sealed class UiaMappingTraversalBudget
    {
        private readonly int _maxNodes;

        internal UiaMappingTraversalBudget(int maxNodes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxNodes, 1);
            _maxNodes = maxNodes;
        }

        internal int VisitedNodes { get; private set; }

        internal string? IncompleteReason { get; private set; }

        internal bool HasRemainingNodes => VisitedNodes < _maxNodes;

        internal bool TryVisitNode(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (VisitedNodes >= _maxNodes)
            {
                MarkIncomplete("maxNodes");
                return false;
            }

            VisitedNodes++;
            return true;
        }

        internal bool TryReadNode<T>(
            Func<T?> readNode,
            string unavailableReason,
            CancellationToken cancellationToken,
            out T? node,
            out bool budgetExhausted)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(readNode);
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingNodes)
            {
                MarkIncomplete("maxNodes");
                node = null;
                budgetExhausted = true;
                return false;
            }

            try
            {
                node = readNode();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                MarkIncomplete(unavailableReason);
                node = null;
                budgetExhausted = false;
                return false;
            }

            budgetExhausted = false;
            return node is null || TryVisitNode(cancellationToken);
        }

        internal void MarkIncomplete(string reason)
        {
            if (IncompleteReason is null)
            {
                IncompleteReason = reason;
            }
        }
    }

    internal static ElementHandle RefreshWpfMappingSource(ElementHandle handle, ElementRef current) =>
        handle with
        {
            XPath = current.XPath,
            WpfAgentElementId = string.IsNullOrWhiteSpace(current.ElementIdWpf)
                ? handle.WpfAgentElementId
                : current.ElementIdWpf,
            Type = current.Type,
            AutomationId = current.AutomationId,
            Name = current.Name,
            ClassName = current.ClassName,
            Bounds = current.Bounds
        };

    internal static void ValidateUiaMappingWindowScope(long? requestedWindowHandle, long elementWindowHandle)
    {
        if (requestedWindowHandle is long requested && requested != elementWindowHandle)
        {
            throw new ArgumentException("windowHandle does not match the elementId window.");
        }
    }

    internal static bool IsUiaMappingProcessInScope(int? candidateProcessId, int attachedProcessId) =>
        candidateProcessId == attachedProcessId;

    private WpfUiaMappingResult MapWpfHandleToUia(
        Window window,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        ElementHandle source,
        string sourceElementId,
        int attachedProcessId,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var traversalBudget = new UiaMappingTraversalBudget(maxNodes);
        var scan = ScanWpfUiaCandidates(
            window,
            controlWalker,
            source,
            attachedProcessId,
            traversalBudget,
            cancellationToken);
        var ordered = scan.Candidates
            .OrderByDescending(candidate => candidate.Ranking.Score)
            .ThenBy(candidate => candidate.TraversalOrdinal)
            .ToArray();
        var decisionScores = ordered.Select(candidate => candidate.Ranking).ToArray();

        var preparedCandidates = new List<PreparedWpfUiaCandidate>(
            Math.Min(ordered.Length, MaximumUiaMappingCandidates));
        var pathWorkComplete = true;
        foreach (var candidate in ordered.Take(MaximumUiaMappingCandidates))
        {
            if (!traversalBudget.HasRemainingNodes)
            {
                traversalBudget.MarkIncomplete("maxNodes");
                pathWorkComplete = false;
                preparedCandidates.Add(new PreparedWpfUiaCandidate(
                    candidate,
                    XPath: null,
                    candidate.Ranking.Evidence.Concat(["uia_path_budget_exhausted"]).ToArray()));
                continue;
            }

            if (TryComputeBoundedXPath(
                    window,
                    candidate.Element,
                    rawWalker,
                    traversalBudget,
                    cancellationToken,
                    out var xpath,
                    out var failureEvidence))
            {
                preparedCandidates.Add(new PreparedWpfUiaCandidate(
                    candidate,
                    xpath,
                    candidate.Ranking.Evidence.Concat(["uia_xpath_available"]).ToArray()));
            }
            else
            {
                pathWorkComplete = false;
                preparedCandidates.Add(new PreparedWpfUiaCandidate(
                    candidate,
                    XPath: null,
                    candidate.Ranking.Evidence.Concat([failureEvidence]).ToArray()));
            }
        }

        var mappingComplete = scan.Complete && pathWorkComplete;
        var decision = ElementMappingScoring.Decide(decisionScores, mappingComplete);

        RegisteredUiaCandidate? selectedRegistration = null;
        string? selectedFlaUiXPath = null;
        var selectedRegistrationAttempted = decision.SelectedIndex == 0;
        if (selectedRegistrationAttempted)
        {
            if (!TryComputeBoundedFlaUiXPath(
                    window,
                    ordered[0].Element,
                    controlWalker,
                    traversalBudget,
                    cancellationToken,
                    out selectedFlaUiXPath,
                    out var flaUiPathFailure))
            {
                preparedCandidates[0] = preparedCandidates[0] with
                {
                    Evidence = preparedCandidates[0].Evidence.Concat([flaUiPathFailure]).ToArray()
                };
                mappingComplete = false;
                decision = ElementMappingScoring.Decide(decisionScores, scanComplete: false);
            }
        }

        CandidateRegistrationAttempt? registrationAttempt = null;
        if (decision.SelectedIndex == 0 && selectedFlaUiXPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            registrationAttempt = TryRegisterSelectedUiaMappingCandidate(
                window,
                rawWalker,
                sourceElementId,
                source.WindowHandle,
                attachedProcessId,
                preparedCandidates[0],
                traversalBudget,
                cancellationToken);
            selectedRegistration = registrationAttempt.Registration;
            if (selectedRegistration is null)
            {
                if (registrationAttempt.IncompleteReason is { } incompleteReason)
                {
                    traversalBudget.MarkIncomplete(incompleteReason);
                    mappingComplete = false;
                }

                var failureEvidence = registrationAttempt.FailureEvidence ?? "runtime_identity_unverifiable";
                decisionScores[0] = decisionScores[0] with
                {
                    Reusable = false,
                    Evidence = ApplyRegistrationFailureToCandidateEvidence(
                        decisionScores[0].Evidence,
                        failureEvidence)
                };
                decision = ElementMappingScoring.Decide(decisionScores, mappingComplete);
            }
        }

        var returnedCandidates = new List<UiaMappingCandidate>(
            preparedCandidates.Count);
        for (var index = 0; index < preparedCandidates.Count; index++)
        {
            var prepared = preparedCandidates[index];
            var candidate = prepared.Candidate;
            var registration = index == 0 && selectedRegistrationAttempted
                ? selectedRegistration
                : null;
            var evidence = registration?.Evidence ?? prepared.Evidence;
            if (index == 0 && registrationAttempt?.FailureEvidence is { } candidateRegistrationFailure)
            {
                evidence = ApplyRegistrationFailureToCandidateEvidence(
                    evidence,
                    candidateRegistrationFailure);
            }

            if (registration is null)
            {
                evidence = evidence.Concat(["public_handle_not_registered"]).ToArray();
            }

            returnedCandidates.Add(new UiaMappingCandidate(
                ElementType: candidate.ElementType,
                AutomationId: candidate.AutomationId,
                Name: candidate.Name,
                ClassName: candidate.ClassName,
                Bounds: candidate.Bounds,
                XPath: prepared.XPath,
                Score: candidate.Ranking.Score,
                XPathOmitted: prepared.XPath is null ? true : null)
            {
                ElementId = registration?.ElementId,
                Reusable = registration is not null ? true : null,
                Evidence = evidence
            });
        }

        var selected = decision.SelectedIndex == 0 && selectedRegistration is not null;
        IReadOnlyList<string> decisionEvidence = selected
            ? decision.Evidence.Concat(["runtime_identity_verified"]).ToArray()
            : registrationAttempt?.FailureEvidence is { } decisionRegistrationFailure
                ? ReplaceRuntimeEvidence(decision.Evidence, decisionRegistrationFailure)
                : decision.Evidence;
        var candidatesTruncated = ordered.Length > MaximumUiaMappingCandidates;
        var diagnostics = new UiaMappingDiagnostics(
            Ambiguous: decision.Status == ElementMappingStatus.Ambiguous,
            SelectedXPath: selected ? preparedCandidates[0].XPath : null,
            Candidates: returnedCandidates,
            ReturnedCandidates: returnedCandidates.Count,
            TotalCandidates: ordered.Length,
            Truncated: !mappingComplete || candidatesTruncated)
        {
            Status = decision.Status,
            Method = WpfUiaMappingMethod,
            SelectedElementId = selected ? selectedRegistration!.ElementId : null,
            Score = decision.Score ?? 0,
            ScoreLead = decision.ScoreLead,
            Evidence = decisionEvidence,
            ScannedNodes = traversalBudget.VisitedNodes,
            ScanComplete = mappingComplete,
            TruncatedReason = !mappingComplete
                ? traversalBudget.IncompleteReason
                : candidatesTruncated ? "maxCandidates" : null
        };

        return new WpfUiaMappingResult(
            SelectedElement: selected ? selectedRegistration!.Element : null,
            SelectedXPath: selected ? preparedCandidates[0].XPath : null,
            SelectedFlaUiXPath: selected ? selectedFlaUiXPath : null,
            SelectedElementId: selected ? selectedRegistration!.ElementId : null,
            ScannedElements: scan.Elements,
            Diagnostics: diagnostics);
    }

    private static WpfUiaScan ScanWpfUiaCandidates(
        Window window,
        ITreeWalker controlWalker,
        ElementHandle source,
        int attachedProcessId,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RankedWpfUiaCandidate>();
        var elements = new List<AutomationElement>();
        var pending = new Queue<AutomationElement>();
        if (!traversalBudget.TryVisitNode(cancellationToken))
        {
            return new WpfUiaScan(candidates, elements, Complete: false);
        }

        pending.Enqueue(window);
        var complete = true;
        var traversalOrdinal = 0;
        var sourceFacts = new ElementMappingScoring.Facts(
            source.AutomationId,
            source.Name,
            source.ClassName,
            source.Bounds);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = pending.Dequeue();
            var currentOrdinal = traversalOrdinal++;

            var candidateProcessId = TryGetUiaProcessId(element);
            if (candidateProcessId is null)
            {
                complete = false;
                traversalBudget.MarkIncomplete("processIdentityUnavailable");
                continue;
            }

            if (!IsUiaMappingProcessInScope(candidateProcessId, attachedProcessId))
            {
                continue;
            }

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
                    candidates.Add(new RankedWpfUiaCandidate(
                        element,
                        currentOrdinal,
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
                traversalBudget.MarkIncomplete("uiaTraversalUnavailable");
            }

            if (!traversalBudget.TryReadNode(
                    () => controlWalker.GetFirstChild(element),
                    "uiaTraversalUnavailable",
                    cancellationToken,
                    out var child,
                    out _))
            {
                complete = false;
            }

            while (child is not null)
            {
                pending.Enqueue(child);
                var currentChild = child;
                if (!traversalBudget.TryReadNode(
                        () => controlWalker.GetNextSibling(currentChild),
                        "uiaTraversalUnavailable",
                        cancellationToken,
                        out child,
                        out _))
                {
                    complete = false;
                    break;
                }
            }
        }

        return new WpfUiaScan(
            candidates,
            elements,
            complete && pending.Count == 0 && traversalBudget.IncompleteReason is null);
    }

    private static int? TryGetUiaProcessId(AutomationElement element)
    {
        try
        {
            return element.Properties.ProcessId.Value;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryComputeBoundedXPath(
        Window window,
        AutomationElement element,
        ITreeWalker walker,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken,
        out string? xpath,
        out string failureEvidence) =>
        TryComputeBoundedPath(
            window,
            element,
            walker,
            GetXPathLabel,
            rootPath: "/Window",
            includeRootSegment: true,
            traversalBudget,
            cancellationToken,
            out xpath,
            out failureEvidence);

    private static bool TryComputeBoundedFlaUiXPath(
        Window window,
        AutomationElement element,
        ITreeWalker walker,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken,
        out string? xpath,
        out string failureEvidence) =>
        TryComputeBoundedPath(
            window,
            element,
            walker,
            GetFlaUiXPathLabel,
            rootPath: "/",
            includeRootSegment: false,
            traversalBudget,
            cancellationToken,
            out xpath,
            out failureEvidence);

    private static bool TryComputeBoundedPath(
        Window window,
        AutomationElement element,
        ITreeWalker walker,
        Func<AutomationElement, string> labelFactory,
        string rootPath,
        bool includeRootSegment,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken,
        out string? xpath,
        out string failureEvidence)
    {
        xpath = null;
        failureEvidence = "uia_path_unavailable";

        try
        {
            if (AreSameElement(window, element))
            {
                xpath = rootPath;
                failureEvidence = "";
                return true;
            }

            var segments = new List<string>();
            AutomationElement? current = element;
            while (current is not null && !AreSameElement(current, window))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentElement = current;
                if (!traversalBudget.TryReadNode(
                        () => walker.GetParent(currentElement),
                        "uiaPathUnavailable",
                        cancellationToken,
                        out var parent,
                        out var budgetExhausted))
                {
                    failureEvidence = budgetExhausted
                        ? "uia_path_budget_exhausted"
                        : "uia_path_unavailable";
                    return false;
                }

                if (parent is null)
                {
                    traversalBudget.MarkIncomplete("uiaPathUnavailable");
                    return false;
                }

                if (!TryComputeBoundedPathSegment(
                        parent,
                        current,
                        walker,
                        labelFactory,
                        traversalBudget,
                        cancellationToken,
                        out var segment,
                        out failureEvidence))
                {
                    return false;
                }

                segments.Add(segment!);
                current = parent;
            }

            if (current is null)
            {
                traversalBudget.MarkIncomplete("uiaPathUnavailable");
                return false;
            }

            segments.Reverse();
            xpath = includeRootSegment
                ? rootPath + "/" + string.Join('/', segments)
                : segments.Count == 0 ? rootPath : "/" + string.Join('/', segments);
            failureEvidence = "";
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }
    }

    private static bool TryComputeBoundedPathSegment(
        AutomationElement parent,
        AutomationElement target,
        ITreeWalker walker,
        Func<AutomationElement, string> labelFactory,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken,
        out string? segment,
        out string failureEvidence)
    {
        segment = null;
        failureEvidence = "uia_path_unavailable";

        string targetLabel;
        try
        {
            targetLabel = labelFactory(target);
        }
        catch
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }

        if (!traversalBudget.TryReadNode(
                () => walker.GetFirstChild(parent),
                "uiaPathUnavailable",
                cancellationToken,
                out var sibling,
                out var budgetExhausted))
        {
            failureEvidence = budgetExhausted
                ? "uia_path_budget_exhausted"
                : "uia_path_unavailable";
            return false;
        }

        var matchingSiblings = 0;
        var targetIndex = 0;
        while (sibling is not null)
        {
            string siblingLabel;
            try
            {
                siblingLabel = labelFactory(sibling);
            }
            catch
            {
                traversalBudget.MarkIncomplete("uiaPathUnavailable");
                return false;
            }

            if (string.Equals(siblingLabel, targetLabel, StringComparison.OrdinalIgnoreCase))
            {
                matchingSiblings++;
                if (AreSameElement(sibling, target))
                {
                    targetIndex = matchingSiblings;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var currentSibling = sibling;
            if (!traversalBudget.TryReadNode(
                    () => walker.GetNextSibling(currentSibling),
                    "uiaPathUnavailable",
                    cancellationToken,
                    out sibling,
                    out budgetExhausted))
            {
                failureEvidence = budgetExhausted
                    ? "uia_path_budget_exhausted"
                    : "uia_path_unavailable";
                return false;
            }
        }

        if (targetIndex == 0)
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }

        segment = matchingSiblings <= 1
            ? targetLabel
            : $"{targetLabel}[{targetIndex}]";
        failureEvidence = "";
        return true;
    }

    private static bool TryResolveBoundedXPath(
        Window window,
        string xpath,
        ITreeWalker walker,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken,
        out AutomationElement? resolved,
        out string failureEvidence)
    {
        resolved = null;
        failureEvidence = "uia_path_unavailable";

        XPathSegment[] segments;
        try
        {
            segments = xpath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseXPathSegment)
                .ToArray();
        }
        catch
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }

        if (segments.Length == 0)
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }

        AutomationElement current = window;
        try
        {
            if (string.Equals(
                    segments[0].TypeName,
                    GetXPathLabel(current),
                    StringComparison.OrdinalIgnoreCase))
            {
                segments = segments.Skip(1).ToArray();
            }
        }
        catch
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }

        foreach (var segment in segments)
        {
            if (!TryResolveBoundedXPathSegment(
                    current,
                    segment,
                    walker,
                    traversalBudget,
                    cancellationToken,
                    out var next,
                    out failureEvidence))
            {
                return false;
            }

            current = next!;
        }

        resolved = current;
        failureEvidence = "";
        return true;
    }

    private static bool TryResolveBoundedXPathSegment(
        AutomationElement parent,
        XPathSegment segment,
        ITreeWalker walker,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken,
        out AutomationElement? resolved,
        out string failureEvidence)
    {
        resolved = null;
        failureEvidence = "uia_path_unavailable";
        if (segment.OneBasedIndex is <= 0)
        {
            traversalBudget.MarkIncomplete("uiaPathUnavailable");
            return false;
        }

        if (!traversalBudget.TryReadNode(
                () => walker.GetFirstChild(parent),
                "uiaPathUnavailable",
                cancellationToken,
                out var child,
                out var budgetExhausted))
        {
            failureEvidence = budgetExhausted
                ? "uia_path_budget_exhausted"
                : "uia_path_unavailable";
            return false;
        }

        var matchingChildren = 0;
        while (child is not null)
        {
            string childLabel;
            try
            {
                childLabel = GetXPathLabel(child);
            }
            catch
            {
                traversalBudget.MarkIncomplete("uiaPathUnavailable");
                return false;
            }

            if (string.Equals(childLabel, segment.TypeName, StringComparison.OrdinalIgnoreCase))
            {
                matchingChildren++;
                if (segment.OneBasedIndex == matchingChildren)
                {
                    resolved = child;
                    failureEvidence = "";
                    return true;
                }

                if (segment.OneBasedIndex is null)
                {
                    resolved = child;
                }
            }

            var currentChild = child;
            if (!traversalBudget.TryReadNode(
                    () => walker.GetNextSibling(currentChild),
                    "uiaPathUnavailable",
                    cancellationToken,
                    out child,
                    out budgetExhausted))
            {
                failureEvidence = budgetExhausted
                    ? "uia_path_budget_exhausted"
                    : "uia_path_unavailable";
                return false;
            }
        }

        if (segment.OneBasedIndex is null && matchingChildren == 1)
        {
            failureEvidence = "";
            return true;
        }

        traversalBudget.MarkIncomplete("uiaPathChanged");
        resolved = null;
        failureEvidence = "uia_path_resolution_changed";
        return false;
    }

    private CandidateRegistrationAttempt TryRegisterSelectedUiaMappingCandidate(
        Window window,
        ITreeWalker rawWalker,
        string sourceElementId,
        long windowHandle,
        int attachedProcessId,
        PreparedWpfUiaCandidate prepared,
        UiaMappingTraversalBudget traversalBudget,
        CancellationToken cancellationToken)
    {
        var candidate = prepared.Candidate;
        if (prepared.XPath is null)
        {
            return new CandidateRegistrationAttempt(
                Registration: null,
                FailureEvidence: "uia_path_unavailable",
                IncompleteReason: "uiaPathUnavailable");
        }

        if (candidate.RuntimeId is not { Length: > 0 } expectedRuntimeId)
        {
            return new CandidateRegistrationAttempt(
                Registration: null,
                FailureEvidence: "runtime_identity_unavailable",
                IncompleteReason: null);
        }

        if (!TryResolveBoundedXPath(
                window,
                prepared.XPath,
                rawWalker,
                traversalBudget,
                cancellationToken,
                out var resolved,
                out var pathFailureEvidence))
        {
            return new CandidateRegistrationAttempt(
                Registration: null,
                FailureEvidence: pathFailureEvidence,
                IncompleteReason: traversalBudget.IncompleteReason ?? "uiaPathUnavailable");
        }

        try
        {
            var processId = TryGetUiaProcessId(resolved!);
            if (processId is null)
            {
                return new CandidateRegistrationAttempt(
                    Registration: null,
                    FailureEvidence: "process_identity_unavailable",
                    IncompleteReason: "processIdentityUnavailable");
            }

            if (!IsUiaMappingProcessInScope(processId, attachedProcessId))
            {
                return new CandidateRegistrationAttempt(
                    Registration: null,
                    FailureEvidence: "process_identity_outside_scope",
                    IncompleteReason: "processIdentityChanged");
            }

            var actualRuntimeId = TryGetRuntimeId(resolved!);
            if (actualRuntimeId is null ||
                !actualRuntimeId.SequenceEqual(expectedRuntimeId))
            {
                return new CandidateRegistrationAttempt(
                    Registration: null,
                    FailureEvidence: "runtime_identity_unverifiable",
                    IncompleteReason: null);
            }

            if (!_elementHandles.TryRegisterUiaKeeping(
                    sourceElementId,
                    windowHandle,
                    prepared.XPath,
                    expectedRuntimeId,
                    candidate.ElementType,
                    candidate.AutomationId,
                    candidate.Name,
                    candidate.ClassName,
                    candidate.Bounds,
                    out var elementId))
            {
                return new CandidateRegistrationAttempt(
                    Registration: null,
                    FailureEvidence: "handle_capacity_insufficient",
                    IncompleteReason: null);
            }

            return new CandidateRegistrationAttempt(
                new RegisteredUiaCandidate(
                    resolved!,
                    elementId!,
                    prepared.Evidence.Concat(["runtime_identity_verified"]).ToArray()),
                FailureEvidence: null,
                IncompleteReason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new CandidateRegistrationAttempt(
                Registration: null,
                FailureEvidence: "runtime_identity_unverifiable",
                IncompleteReason: null);
        }
    }

    private static IReadOnlyList<string> ReplaceRuntimeEvidence(
        IReadOnlyList<string> evidence,
        string replacement) =>
        evidence
            .Where(item => !item.StartsWith("runtime_identity_", StringComparison.Ordinal))
            .Append(replacement)
            .ToArray();

    private static IReadOnlyList<string> ApplyRegistrationFailureToCandidateEvidence(
        IReadOnlyList<string> evidence,
        string failureEvidence) =>
        failureEvidence.StartsWith("runtime_identity_", StringComparison.Ordinal)
            ? ReplaceRuntimeEvidence(evidence, failureEvidence)
            : evidence.Concat([failureEvidence]).Distinct(StringComparer.Ordinal).ToArray();

    internal static bool AreWpfAndUiaTypesCompatible(
        string? wpfType,
        string? uiaControlType) =>
        ElementMappingScoring.AreWpfAndUiaTypesCompatible(wpfType, uiaControlType);

    private async Task<UiaWpfMappingResult> MapUiaOriginToWpfAsync(
        Window window,
        UiaLocatorIdentity source,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (GetAutoBackendRoute(window) == AutoBackendRoute.Uia)
        {
            return new UiaWpfMappingResult(
                Wpf: null,
                CreateNonWpfWindowMappingDiagnostics("window_framework_not_wpf"));
        }

        AgentClient? client;
        try
        {
            client = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = PreferTargetStateFailure(CreateAutoWpfFallbackFailure(ex));
            return new UiaWpfMappingResult(null, CreateUnavailableWpfMappingDiagnostics(failure));
        }

        if (client is null)
        {
            var failure = GetWpfBackendFailure() ?? new FailureInfo(
                Code: FailureDiagnostics.Codes.AgentConnectionFailed,
                Stage: FailureDiagnostics.Stages.PipeConnection,
                Detail: "The WPF mapping backend is unavailable.")
            {
                Retryable = true,
                RecoveryActions = [FailureDiagnostics.Recovery.Retry]
            };
            return new UiaWpfMappingResult(null, CreateUnavailableWpfMappingDiagnostics(failure));
        }

        if (!AgentSupportsCapability(client, AgentProtocolCapabilities.MapUiaToWpf))
        {
            var failure = new FailureInfo(
                Code: "agent_capability_unavailable",
                Stage: FailureDiagnostics.Stages.Protocol,
                Detail: "UIA-to-WPF mapping requires the current WPF agent.")
            {
                Retryable = false,
                RecoveryActions =
                [
                    FailureDiagnostics.Recovery.RestartTarget,
                    FailureDiagnostics.Recovery.Reattach
                ]
            };
            return new UiaWpfMappingResult(null, CreateUnavailableWpfMappingDiagnostics(failure));
        }

        try
        {
            var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();
            var response = await client.CallAsync<MapUiaToWpfAgentResponse>(
                AgentProtocolCapabilities.MapUiaToWpf,
                new MapUiaToWpfAgentRequest(
                    hwnd,
                    new UiaMappingSource(
                        source.ControlType,
                        source.AutomationId,
                        source.Name,
                        source.ClassName,
                        source.Bounds),
                    maxNodes),
                cancellationToken).ConfigureAwait(false);

            if (response.SelectedElement is not { } selected)
            {
                return new UiaWpfMappingResult(null, response.Mapping);
            }

            if (string.IsNullOrWhiteSpace(selected.ElementIdWpf))
            {
                var failure = FailureDiagnostics.ProtocolFailure();
                return new UiaWpfMappingResult(null, CreateUnavailableWpfMappingDiagnostics(failure));
            }

            var publicElementId = _elementHandles.RegisterWpf(
                hwnd,
                selected.XPath,
                selected.ElementIdWpf,
                selected.Type,
                selected.AutomationId,
                selected.Name,
                selected.ClassName,
                selected.Bounds);
            var identity = new WpfLocatorIdentity(
                selected.Type,
                selected.AutomationId,
                selected.Name,
                selected.ClassName,
                selected.XPath,
                publicElementId)
            {
                Bounds = selected.Bounds
            };
            return new UiaWpfMappingResult(
                identity,
                response.Mapping with
                {
                    SelectedElementId = publicElementId,
                    SelectedXPath = selected.XPath
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsPerWindowAutoWpfMiss(ex))
        {
            return new UiaWpfMappingResult(
                Wpf: null,
                CreateNonWpfWindowMappingDiagnostics("wpf_window_not_found"));
        }
        catch (Exception ex)
        {
            var failure = PreferTargetStateFailure(CreateAutoWpfFallbackFailure(ex));
            if (ShouldRecordAutoAgentFailure(ex, client.IsConnected))
            {
                SetAutoAgentFailure(failure);
            }

            return new UiaWpfMappingResult(null, CreateUnavailableWpfMappingDiagnostics(failure));
        }
    }

    internal static WpfMappingDiagnostics CreateNonWpfWindowMappingDiagnostics(string evidence) =>
        new(
            Available: true,
            Method: FrameworkClassificationMappingMethod,
            Candidates: [],
            ReturnedCandidates: 0,
            TotalCandidates: 0,
            ScannedNodes: 0,
            ScanComplete: true,
            Truncated: false)
        {
            Status = ElementMappingStatus.Unmapped,
            Evidence = ["scan_complete", evidence]
        };

    internal static WpfMappingDiagnostics CreateUnavailableWpfMappingDiagnostics(FailureInfo failure) =>
        new(
            Available: false,
            Method: UiaWpfMappingMethod,
            Candidates: [],
            ReturnedCandidates: 0,
            TotalCandidates: 0,
            ScannedNodes: 0,
            ScanComplete: false,
            Truncated: false)
        {
            Evidence = ["mapping_backend_unavailable"],
            Failure = failure
        };
}
