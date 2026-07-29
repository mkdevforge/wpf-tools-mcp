using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private const int MaximumScreenshotCorrelationCandidates = 25;
    private const int MaximumScreenshotCorrelationNodes = 200_000;
    private const int MaximumScreenshotCorrelationAncestors = 20;

    private async Task<ScreenshotCorrelationResult> CorrelateScreenshotAsync(
        Window window,
        long windowHandle,
        Bitmap bitmap,
        Rect capturedBounds,
        ScreenshotCorrelationOptions options,
        ScreenshotCaptureContext captureContext,
        CancellationToken cancellationToken)
    {
        options = NormalizeScreenshotCorrelationOptions(options);
        var imageRegion = new Rect(options.X, options.Y, options.Width, options.Height);
        var screenRegion = ScreenshotCorrelationGeometry.MapImageRegionToScreen(
            imageRegion,
            bitmap.Width,
            bitmap.Height,
            capturedBounds);
        var screenPoint = imageRegion.Width == 1 && imageRegion.Height == 1
            ? ScreenshotCorrelationGeometry.GetCanonicalScreenPoint(screenRegion)
            : null;

        var maxCandidates = options.MaxCandidates;
        var maxNodes = options.MaxNodes;
        var maxAncestors = options.MaxAncestors;

        var backends = new List<ScreenshotCorrelationBackendResult>(2);
        switch (options.Backend)
        {
            case ScreenshotCorrelationBackend.Auto:
            {
                var client = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
                if (client is not null && AgentSupportsCapability(client, AgentProtocolCapabilities.CorrelateScreenshotRegion))
                {
                    backends.Add(await CorrelateScreenshotWpfAsync(
                        client,
                        windowHandle,
                        screenRegion,
                        screenPoint,
                        maxCandidates,
                        maxNodes,
                        options.IncludeAncestors,
                        maxAncestors,
                        cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    backends.Add(CorrelateScreenshotUia(
                        window,
                        windowHandle,
                        screenRegion,
                        screenPoint,
                        maxCandidates,
                        maxNodes,
                        options.IncludeAncestors,
                        maxAncestors,
                        captureContext.Viewport,
                        cancellationToken));
                }

                break;
            }
            case ScreenshotCorrelationBackend.Uia:
                backends.Add(CorrelateScreenshotUia(
                    window,
                    windowHandle,
                    screenRegion,
                    screenPoint,
                    maxCandidates,
                    maxNodes,
                    options.IncludeAncestors,
                    maxAncestors,
                    captureContext.Viewport,
                    cancellationToken));
                break;
            case ScreenshotCorrelationBackend.Wpf:
            {
                var client = await RequireScreenshotCorrelationAgentAsync(cancellationToken).ConfigureAwait(false);
                backends.Add(await CorrelateScreenshotWpfAsync(
                    client,
                    windowHandle,
                    screenRegion,
                    screenPoint,
                    maxCandidates,
                    maxNodes,
                    options.IncludeAncestors,
                    maxAncestors,
                    cancellationToken).ConfigureAwait(false));
                break;
            }
            case ScreenshotCorrelationBackend.Both:
            {
                backends.Add(CorrelateScreenshotUia(
                    window,
                    windowHandle,
                    screenRegion,
                    screenPoint,
                    maxCandidates,
                    maxNodes,
                    options.IncludeAncestors,
                    maxAncestors,
                    captureContext.Viewport,
                    cancellationToken));

                var client = await RequireScreenshotCorrelationAgentAsync(cancellationToken).ConfigureAwait(false);
                backends.Add(await CorrelateScreenshotWpfAsync(
                    client,
                    windowHandle,
                    screenRegion,
                    screenPoint,
                    maxCandidates,
                    maxNodes,
                    options.IncludeAncestors,
                    maxAncestors,
                    cancellationToken).ConfigureAwait(false));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Backend), options.Backend, "Unsupported correlation backend.");
        }

        IReadOnlyList<ScreenshotCorrelationAnnotation> annotations = [];
        if (options.Annotate)
        {
            (backends, annotations) = ApplyScreenshotCorrelationAnnotations(bitmap, capturedBounds, backends);
        }

        return new ScreenshotCorrelationResult(
            ImageRegion: imageRegion,
            ScreenRegionPhysicalPixels: screenRegion,
            Backends: backends,
            ReturnedCandidates: backends.Sum(result => result.ReturnedCandidates),
            DiscoveredCandidates: backends.Sum(result => result.DiscoveredCandidates),
            ScannedNodes: backends.Sum(result => result.ScannedNodes),
            Ambiguous: backends.Any(result => result.HasOverlaps),
            Annotations: annotations,
            CaptureContext: captureContext,
            ScreenPointPhysicalPixels: screenPoint);
    }

    private ScreenshotCorrelationBackendResult CorrelateScreenshotUia(
        Window window,
        long windowHandle,
        Rect screenRegion,
        ScreenshotCorrelationPoint? screenPoint,
        int maxCandidates,
        int maxNodes,
        bool includeAncestors,
        int maxAncestors,
        ViewportConditions viewport,
        CancellationToken cancellationToken)
    {
        var automation = EnsureAutomation();
        var walker = automation.TreeWalkerFactory.GetRawViewWalker();
        AutomationElement? directElement = null;

        if (screenPoint is not null)
        {
            try
            {
                var fromPoint = automation.FromPoint(new System.Drawing.Point(screenPoint.X, screenPoint.Y));
                if (fromPoint is not null &&
                    fromPoint.Properties.ProcessId.Value == EnsureAttached().ProcessId &&
                    IsElementWithinWindow(window, fromPoint, walker))
                {
                    directElement = fromPoint;
                }
            }
            catch
            {
                directElement = null;
            }
        }

        var nextNodeId = 0;
        var root = new UiaCorrelationNode(
            Id: nextNodeId++,
            Element: window,
            XPath: "/Window",
            Bounds: TryGetCorrelationBounds(window),
            Depth: 0,
            Order: 0,
            Parent: null,
            Direct: directElement is not null && AreSameElement(window, directElement));
        var stack = new Stack<UiaCorrelationNode>();
        stack.Push(root);

        var matches = new List<UiaCorrelationNode>();
        var scannedNodes = 0;
        var traversalOrder = 0;

        while (stack.Count > 0 && scannedNodes < maxNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            scannedNodes++;

            if (current.Bounds is { } bounds &&
                MatchesCorrelationRegion(bounds, screenRegion, screenPoint))
            {
                matches.Add(current);
            }

            var children = GetChildren(current.Element, walker).ToArray();
            if (children.Length == 0)
            {
                continue;
            }

            var labels = children.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = children.Length - 1; i >= 0; i--)
            {
                var child = children[i];
                var label = labels[i];
                runningIndexByLabel.TryGetValue(label, out var reverseIndex);
                reverseIndex++;
                runningIndexByLabel[label] = reverseIndex;
                var segment = countsByLabel[label] > 1
                    ? $"{label}[{countsByLabel[label] - reverseIndex + 1}]"
                    : label;

                stack.Push(new UiaCorrelationNode(
                    Id: nextNodeId++,
                    Element: child,
                    XPath: $"{current.XPath}/{segment}",
                    Bounds: TryGetCorrelationBounds(child),
                    Depth: current.Depth + 1,
                    Order: traversalOrder++,
                    Parent: current,
                    Direct: directElement is not null && AreSameElement(child, directElement)));
            }
        }

        var scanComplete = stack.Count == 0;
        var matchedAncestorIds = new HashSet<int>();
        foreach (var match in matches)
        {
            for (var parent = match.Parent; parent is not null; parent = parent.Parent)
            {
                matchedAncestorIds.Add(parent.Id);
            }
        }

        var candidates = matches
            .Where(match => match.Direct || !matchedAncestorIds.Contains(match.Id))
            .OrderByDescending(match => match.Direct)
            .ThenByDescending(match => screenPoint is not null ? 0L : IntersectionArea(match.Bounds!, screenRegion))
            .ThenBy(match => BoundsArea(match.Bounds!))
            .ThenByDescending(match => match.Depth)
            .ThenBy(match => match.Order)
            .ToArray();

        var returned = new List<ScreenshotCorrelationCandidate>(Math.Min(maxCandidates, candidates.Length));
        for (var i = 0; i < candidates.Length && i < maxCandidates; i++)
        {
            var match = candidates[i];
            var bounds = match.Bounds!;
            var intersection = ScreenshotCorrelationGeometry.Intersect(bounds, screenRegion)!;
            var elementId = _elementHandles.RegisterUia(
                windowHandle,
                match.XPath,
                TryGetRuntimeId(match.Element),
                match.Element.ControlType.ToString(),
                GetAutomationId(match.Element),
                GetName(match.Element),
                GetClassName(match.Element));
            var element = BuildElementRefUia(
                match.Element,
                match.XPath,
                FindReturnFields.Standard,
                elementId,
                viewport.ClientBoundsPhysicalPixels);

            IReadOnlyList<ElementRef>? ancestors = null;
            if (includeAncestors)
            {
                ancestors = BuildUiaCorrelationAncestors(
                    match,
                    windowHandle,
                    maxAncestors,
                    viewport.ClientBoundsPhysicalPixels);
            }

            returned.Add(new ScreenshotCorrelationCandidate(
                Index: i + 1,
                Backend: InspectionBackend.Uia,
                Element: element,
                MatchKind: match.Direct
                    ? ScreenshotCorrelationMatchKind.DirectHit
                    : ScreenshotCorrelationMatchKind.BoundsIntersection,
                IntersectionPhysicalPixels: intersection,
                Ancestors: ancestors));
        }

        var directHitIndex = returned.FirstOrDefault(candidate => candidate.MatchKind == ScreenshotCorrelationMatchKind.DirectHit)?.Index;
        var candidateLimitReached = candidates.Length > returned.Count;
        var truncated = !scanComplete || candidateLimitReached;
        var truncatedReason = !scanComplete
            ? "maxNodes"
            : candidateLimitReached
                ? "maxCandidates"
                : null;

        return new ScreenshotCorrelationBackendResult(
            Backend: InspectionBackend.Uia,
            Candidates: returned,
            ReturnedCandidates: returned.Count,
            DiscoveredCandidates: candidates.Length,
            ScannedNodes: scannedNodes,
            ScanComplete: scanComplete,
            Truncated: truncated,
            TruncatedReason: truncatedReason,
            DirectHitIndex: directHitIndex,
            HasOverlaps: HasOverlappingUiaCandidates(candidates, screenRegion, screenPoint));
    }

    private IReadOnlyList<ElementRef> BuildUiaCorrelationAncestors(
        UiaCorrelationNode candidate,
        long windowHandle,
        int maxAncestors,
        Rect viewportBounds)
    {
        if (maxAncestors == 0)
        {
            return [];
        }

        var results = new List<ElementRef>(maxAncestors);
        for (var current = candidate.Parent; current is not null && results.Count < maxAncestors; current = current.Parent)
        {
            var elementId = _elementHandles.RegisterUia(
                windowHandle,
                current.XPath,
                TryGetRuntimeId(current.Element),
                current.Element.ControlType.ToString(),
                GetAutomationId(current.Element),
                GetName(current.Element),
                GetClassName(current.Element));
            results.Add(BuildElementRefUia(
                current.Element,
                current.XPath,
                FindReturnFields.Standard,
                elementId,
                viewportBounds));
        }

        return results;
    }

    private async Task<ScreenshotCorrelationBackendResult> CorrelateScreenshotWpfAsync(
        AgentClient client,
        long windowHandle,
        Rect screenRegion,
        ScreenshotCorrelationPoint? screenPoint,
        int maxCandidates,
        int maxNodes,
        bool includeAncestors,
        int maxAncestors,
        CancellationToken cancellationToken)
    {
        var response = await CallCorrelateScreenshotWhenSupportedAsync(
            GetAgentCapabilities(client),
            () => client.CallAsync<CorrelateWpfScreenshotRegionResponse>(
                AgentProtocolCapabilities.CorrelateScreenshotRegion,
                new CorrelateWpfScreenshotRegionRequest(
                    ScreenRegionPhysicalPixels: screenRegion,
                    WindowHandle: windowHandle,
                    MaxCandidates: maxCandidates,
                    MaxNodes: maxNodes,
                    IncludeAncestors: includeAncestors,
                    MaxAncestors: maxAncestors,
                    ScreenPointPhysicalPixels: screenPoint),
                cancellationToken)).ConfigureAwait(false);

        var normalizedCandidates = response.Result.Candidates
            .Select(candidate => candidate with
            {
                Backend = InspectionBackend.Wpf,
                Element = NormalizeWpfCorrelationElement(windowHandle, candidate.Element),
                Ancestors = candidate.Ancestors?.Select(ancestor => NormalizeWpfCorrelationElement(windowHandle, ancestor)).ToArray()
            })
            .ToArray();

        return response.Result with
        {
            Backend = InspectionBackend.Wpf,
            Candidates = normalizedCandidates,
            ReturnedCandidates = normalizedCandidates.Length
        };
    }

    private ElementRef NormalizeWpfCorrelationElement(long windowHandle, ElementRef element)
    {
        var publicId = _elementHandles.RegisterWpf(
            windowHandle,
            element.XPath,
            element.ElementIdWpf,
            element.Type,
            element.AutomationId,
            element.Name,
            element.ClassName,
            element.Bounds);
        return element with { ElementId = publicId, ElementIdWpf = null };
    }

    private async Task<AgentClient> RequireScreenshotCorrelationAgentAsync(CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            throw new InvalidOperationException("WPF agent is not connected. Call inject_agent first.");
        }

        return client;
    }

    internal static Task<T> CallCorrelateScreenshotWhenSupportedAsync<T>(
        AgentCapabilitiesResponse? capabilities,
        Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return capabilities is not null &&
               capabilities.Capabilities.Contains(
                   AgentProtocolCapabilities.CorrelateScreenshotRegion,
                   StringComparer.Ordinal)
            ? call()
            : Task.FromException<T>(CreateScreenshotCorrelationCapabilityException());
    }

    internal static InvalidOperationException CreateScreenshotCorrelationCapabilityException() =>
        new(
            "agent_capability_unavailable: screenshot correlation requires the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal static ScreenshotCorrelationOptions NormalizeScreenshotCorrelationOptions(
        ScreenshotCorrelationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            MaxCandidates = Math.Clamp(options.MaxCandidates, 1, MaximumScreenshotCorrelationCandidates),
            MaxNodes = Math.Clamp(options.MaxNodes, 1, MaximumScreenshotCorrelationNodes),
            MaxAncestors = Math.Clamp(options.MaxAncestors, 0, MaximumScreenshotCorrelationAncestors)
        };
    }

    private static (List<ScreenshotCorrelationBackendResult> Backends, IReadOnlyList<ScreenshotCorrelationAnnotation> Annotations)
        ApplyScreenshotCorrelationAnnotations(
            Bitmap bitmap,
            Rect capturedBounds,
            IReadOnlyList<ScreenshotCorrelationBackendResult> backends)
    {
        var plans = new List<(InspectionBackend Backend, int Index, Rect Bounds, ScreenshotCorrelationAnnotation Annotation)>();
        foreach (var backend in backends)
        {
            foreach (var candidate in backend.Candidates)
            {
                if (candidate.Element.Bounds is { } bounds &&
                    ScreenshotCorrelationGeometry.MapScreenRegionToImage(
                        bounds,
                        bitmap.Width,
                        bitmap.Height,
                        capturedBounds) is { } imageBounds)
                {
                    var labelPrefix = candidate.Backend == InspectionBackend.Wpf ? "W" : "U";
                    var color = candidate.Backend == InspectionBackend.Wpf ? "#DC2626" : "#2563EB";
                    var annotation = new ScreenshotCorrelationAnnotation(
                        Index: candidate.Index,
                        Backend: candidate.Backend,
                        ImageBounds: imageBounds,
                        Label: $"{labelPrefix}{candidate.Index}",
                        Color: color);
                    plans.Add((candidate.Backend, candidate.Index, bounds, annotation));
                }
            }
        }

        var applied = new Dictionary<(InspectionBackend Backend, int Index), ScreenshotCorrelationAnnotation>();
        foreach (var plan in plans.AsEnumerable().Reverse())
        {
            try
            {
                AnnotateBitmap(
                    bitmap,
                    capturedBounds,
                    plan.Bounds,
                    plan.Annotation.Color,
                    thickness: 3,
                    plan.Annotation.Label);
                applied[(plan.Backend, plan.Index)] = plan.Annotation;
            }
            catch
            {
                // Annotation is supplemental evidence; the capture and correlation remain useful.
            }
        }

        var updatedBackends = backends
            .Select(backend => backend with
            {
                Candidates = backend.Candidates
                    .Select(candidate => candidate with
                    {
                        Annotation = applied.GetValueOrDefault((candidate.Backend, candidate.Index))
                    })
                    .ToArray()
            })
            .ToList();
        var annotations = plans
            .Select(plan => applied.GetValueOrDefault((plan.Backend, plan.Index)))
            .Where(annotation => annotation is not null)
            .Cast<ScreenshotCorrelationAnnotation>()
            .ToArray();

        return (updatedBackends, annotations);
    }

    private static ScreenshotObscurationInfo CaptureScreenshotObscuration(
        IntPtr targetWindow,
        Rect capturedBounds,
        ScreenshotCaptureMode captureModeUsed)
    {
        if (captureModeUsed == ScreenshotCaptureMode.PrintWindow)
        {
            return new ScreenshotObscurationInfo(
                ScreenshotObscurationState.NotApplicable,
                SampledPoints: 0,
                ObscuredPoints: 0,
                Reason: "printWindowCaptureIsIndependentOfDesktopOcclusion");
        }

        if (!OperatingSystem.IsWindows() || capturedBounds.Width <= 0 || capturedBounds.Height <= 0)
        {
            return new ScreenshotObscurationInfo(
                ScreenshotObscurationState.Unknown,
                SampledPoints: 0,
                ObscuredPoints: 0,
                Reason: "screenOcclusionSamplingUnavailable");
        }

        var targetRoot = GetTopLevelRootWindow(targetWindow);
        if (targetRoot == IntPtr.Zero)
        {
            return new ScreenshotObscurationInfo(
                ScreenshotObscurationState.Unknown,
                SampledPoints: 0,
                ObscuredPoints: 0,
                Reason: "targetRootWindowUnavailable");
        }

        var right = capturedBounds.X + capturedBounds.Width - 1;
        var bottom = capturedBounds.Y + capturedBounds.Height - 1;
        var points = new[]
        {
            new NativeMousePoint(capturedBounds.X + capturedBounds.Width / 2, capturedBounds.Y + capturedBounds.Height / 2),
            new NativeMousePoint(capturedBounds.X, capturedBounds.Y),
            new NativeMousePoint(right, capturedBounds.Y),
            new NativeMousePoint(capturedBounds.X, bottom),
            new NativeMousePoint(right, bottom)
        }
        .Distinct()
        .ToArray();

        var sampledRoots = new List<long?>(points.Length);
        foreach (var point in points)
        {
            var windowAtPoint = WindowFromPoint(point);
            if (windowAtPoint == IntPtr.Zero)
            {
                sampledRoots.Add(null);
                continue;
            }

            var root = GetTopLevelRootWindow(windowAtPoint);
            if (root == IntPtr.Zero)
            {
                sampledRoots.Add(null);
                continue;
            }

            sampledRoots.Add(root.ToInt64());
        }

        return ClassifyScreenshotObscurationSamples(targetRoot.ToInt64(), sampledRoots);
    }

    internal static ScreenshotObscurationInfo ClassifyScreenshotObscurationSamples(
        long targetRootWindowHandle,
        IReadOnlyList<long?> sampledRootWindowHandles)
    {
        ArgumentNullException.ThrowIfNull(sampledRootWindowHandles);
        if (targetRootWindowHandle == 0)
        {
            return new ScreenshotObscurationInfo(
                ScreenshotObscurationState.Unknown,
                SampledPoints: sampledRootWindowHandles.Count,
                ObscuredPoints: 0,
                Reason: "targetRootWindowUnavailable");
        }

        var unknownPoints = sampledRootWindowHandles.Count(handle => handle is null or 0);
        var obscuringHandles = sampledRootWindowHandles
            .Where(handle => handle is not null and not 0 && handle != targetRootWindowHandle)
            .Select(handle => handle!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var obscuredPoints = sampledRootWindowHandles.Count(
            handle => handle is not null and not 0 && handle != targetRootWindowHandle);

        if (obscuredPoints > 0)
        {
            return new ScreenshotObscurationInfo(
                ScreenshotObscurationState.PotentiallyObscured,
                SampledPoints: sampledRootWindowHandles.Count,
                ObscuredPoints: obscuredPoints,
                ObscuringWindowHandles: obscuringHandles,
                Reason: "screenCaptureSamplePointsResolvedToOtherWindows");
        }

        if (unknownPoints > 0)
        {
            return new ScreenshotObscurationInfo(
                ScreenshotObscurationState.Unknown,
                SampledPoints: sampledRootWindowHandles.Count,
                ObscuredPoints: 0,
                Reason: "oneOrMoreScreenSamplePointsCouldNotBeResolved");
        }

        return new ScreenshotObscurationInfo(
            ScreenshotObscurationState.ClearAtSamplePoints,
            SampledPoints: sampledRootWindowHandles.Count,
            ObscuredPoints: 0);
    }

    private static IntPtr GetTopLevelRootWindow(IntPtr windowHandle)
    {
        var root = GetAncestor(windowHandle, GetAncestorRoot);
        return root == IntPtr.Zero ? windowHandle : root;
    }

    private const uint GetAncestorRoot = 2;

    private static bool MatchesCorrelationRegion(
        Rect elementBounds,
        Rect query,
        ScreenshotCorrelationPoint? screenPoint) =>
        screenPoint is not null
            ? ScreenshotCorrelationGeometry.ContainsPoint(elementBounds, screenPoint.X, screenPoint.Y)
            : ScreenshotCorrelationGeometry.Intersect(elementBounds, query) is not null;

    private static Rect? TryGetCorrelationBounds(AutomationElement element)
    {
        try
        {
            var bounds = ToRect(element.BoundingRectangle);
            return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
        }
        catch
        {
            return null;
        }
    }

    private static long BoundsArea(Rect bounds) => Math.Max(1L, (long)bounds.Width * bounds.Height);

    private static long IntersectionArea(Rect bounds, Rect query)
    {
        var intersection = ScreenshotCorrelationGeometry.Intersect(bounds, query);
        return intersection is null ? 0 : (long)intersection.Width * intersection.Height;
    }

    private static bool HasOverlappingUiaCandidates(
        IReadOnlyList<UiaCorrelationNode> candidates,
        Rect query,
        ScreenshotCorrelationPoint? screenPoint)
    {
        if (screenPoint is not null)
        {
            return candidates.Count > 1;
        }

        return ScreenshotCorrelationOverlap.HasAnyOverlap(
            candidates
                .Select(candidate => candidate.Bounds is { } bounds
                    ? ScreenshotCorrelationGeometry.Intersect(bounds, query)
                    : null)
                .Where(intersection => intersection is not null)
                .Cast<Rect>());
    }

    private sealed record UiaCorrelationNode(
        int Id,
        AutomationElement Element,
        string XPath,
        Rect? Bounds,
        int Depth,
        int Order,
        UiaCorrelationNode? Parent,
        bool Direct);
}
