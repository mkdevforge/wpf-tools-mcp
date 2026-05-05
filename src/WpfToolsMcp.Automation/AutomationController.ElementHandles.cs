using System.Diagnostics;
using System.Security.Cryptography;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private readonly ElementHandleStore _elementHandles = new();

    private sealed record ResolvedWpfLocatorTarget(
        string ElementId,
        ElementHandle Handle);

    public async Task<ResolveElementResponse> ResolveElementAsync(
        InspectionBackend backend,
        ElementLocator locator,
        long? windowHandle = null,
        int timeoutMs = 5000,
        int pollIntervalMs = 100,
        int stableMs = 0,
        bool visibleOnly = true,
        bool includeOffViewport = true,
        bool interactiveOnly = false,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        CancellationToken cancellationToken = default,
        bool autoInject = false)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var trace = BeginTraceSpan("resolve_element");
        try
        {
            timeoutMs = Math.Clamp(timeoutMs, 0, 60_000);
            pollIntervalMs = Math.Clamp(pollIntervalMs, 25, 2000);
            stableMs = Math.Clamp(stableMs, 0, 5000);

            var effectiveBackend = backend;
            if (backend == InspectionBackend.Auto)
            {
                if (autoInject)
                {
                    var autoClient = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
                    effectiveBackend = autoClient is not null ? InspectionBackend.Wpf : InspectionBackend.Uia;
                }
                else
                {
                    effectiveBackend = IsAgentConnected ? InspectionBackend.Wpf : InspectionBackend.Uia;
                }
            }

            var response = await (effectiveBackend switch
            {
                InspectionBackend.Uia => ResolveUiaElementAsync(
                    locator,
                    windowHandle,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    cancellationToken),
                InspectionBackend.Wpf => ResolveWpfElementAsync(
                    locator,
                    windowHandle,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unsupported backend.")
            }).ConfigureAwait(false);

            trace?.SetSummary($"{response.BackendUsed} {response.Element.Type} {response.Element.XPath}");
            return response;
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    public async Task<ReleaseElementResponse> ReleaseElementAsync(string elementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        var trace = BeginTraceSpan("release_element");
        try
        {
            var id = elementId.Trim();
            _elementHandles.TryGet(id, out var handle);
            var released = _elementHandles.Release(id);
            if (released &&
                handle?.Backend == InspectionBackend.Wpf &&
                !string.IsNullOrWhiteSpace(handle.WpfAgentElementId))
            {
                try
                {
                    var client = await EnsureAgentConnectedOrNullAsync(CancellationToken.None).ConfigureAwait(false);
                    if (client is not null)
                    {
                        _ = await client.CallAsync<ReleaseElementResponse>(
                            AgentMethods.ReleaseElement,
                            new ReleaseWpfElementRequest(handle.WpfAgentElementId),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Public handle release should not fail if the in-proc weak handle already disappeared.
                }
            }

            trace?.SetSummary($"released={released}");
            return new ReleaseElementResponse(released);
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    private async Task<ResolveElementResponse> ResolveUiaElementAsync(
        ElementLocator locator,
        long? windowHandle,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();

        var start = Stopwatch.GetTimestamp();

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        var element = timeoutMs > 0
            ? await ResolveUiaElementWithWaitAsync(
                window,
                locator,
                controlWalker,
                rawWalker,
                timeoutMs,
                pollIntervalMs,
                ActionKind.Inspect,
                visibleOnly,
                includeOffViewport,
                interactiveOnly,
                interactiveMode,
                cancellationToken).ConfigureAwait(false)
            : ResolveElement(window, locator, controlWalker, rawWalker, ActionKind.Inspect, visibleOnly, includeOffViewport, interactiveOnly, interactiveMode);

        if (stableMs > 0 && timeoutMs > 0)
        {
            var elapsedMs = (int)Math.Round(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                MidpointRounding.AwayFromZero);
            var remainingMs = Math.Max(0, timeoutMs - elapsedMs);
            if (remainingMs > 0)
            {
                await WaitForResolvedElementStateAsync(
                    element,
                    WaitForState.Stable,
                    remainingMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var xpath = ComputeXPath(window, element, rawWalker);

        var elementId = _elementHandles.RegisterUia(
            hwnd,
            xpath,
            TryGetRuntimeId(element),
            element.ControlType.ToString(),
            GetAutomationId(element),
            GetName(element),
            GetClassName(element));

        Rect? bounds = null;
        try
        {
            bounds = ToRect(element.BoundingRectangle);
        }
        catch
        {
        }

        var elementRef = new ElementRef(
            Type: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ClassName: GetClassName(element),
            Bounds: bounds,
            ElementId: elementId);

        return new ResolveElementResponse(InspectionBackend.Uia, elementRef, hwnd);
    }

    private async Task<ResolveElementResponse> ResolveWpfElementAsync(
        ElementLocator locator,
        long? windowHandle,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();

        var element = timeoutMs > 0
            ? await ResolveWpfElementRefWithWaitAsync(
                locator,
                hwnd,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                visibleOnly,
                includeOffViewport,
                interactiveOnly,
                interactiveMode,
                cancellationToken).ConfigureAwait(false)
            : await ResolveWpfElementRefAsync(
                locator,
                hwnd,
                visibleOnly,
                includeOffViewport,
                interactiveOnly,
                interactiveMode,
                cancellationToken).ConfigureAwait(false);

        var elementId = _elementHandles.RegisterWpf(
            hwnd,
            element.XPath,
            element.ElementIdWpf,
            element.Type,
            element.AutomationId,
            element.Name,
            element.ClassName,
            element.Bounds);

        var elementRef = element with { ElementId = elementId, ElementIdWpf = null };
        return new ResolveElementResponse(InspectionBackend.Wpf, elementRef, hwnd);
    }

    private async Task<ElementRef> ResolveWpfElementRefWithWaitAsync(
        ElementLocator locator,
        long windowHandle,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        Rect? lastBounds = null;
        long? stableStartTimestamp = null;
        var currentLocator = locator;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ElementRef element;
            try
            {
                element = await ResolveWpfElementRefAsync(
                    currentLocator,
                    windowHandle,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
            {
                lastBounds = null;
                stableStartTimestamp = null;

                var elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed.TotalMilliseconds >= timeoutMs)
                {
                    var hint = visibleOnly && !includeOffViewport
                        ? " Retry with includeOffViewport=true, visibleOnly=false for hidden elements, or call scroll_to_element first."
                        : "";
                    throw new InvalidOperationException($"timeout: element not found after {timeoutMs}ms.{hint}");
                }

                await Task.Delay(pollIntervalMs, cancellationToken);
                continue;
            }

            if (stableMs <= 0)
            {
                return element;
            }

            var (stable, _) = CheckStableBounds(element.Bounds, stableMs, ref lastBounds, ref stableStartTimestamp);
            if (stable)
            {
                return element;
            }

            currentLocator = new ElementLocator(XPath: element.XPath, PreferVisible: locator.PreferVisible, Strict: true);

            var totalElapsed = Stopwatch.GetElapsedTime(start);
            if (totalElapsed.TotalMilliseconds >= timeoutMs)
            {
                throw new InvalidOperationException($"timeout: element not stable after {timeoutMs}ms.");
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
    }

    private async Task<ElementRef> ResolveWpfElementRefAsync(
        ElementLocator locator,
        long windowHandle,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        var request = new ResolveWpfElementRequest(
            WindowHandle: windowHandle,
            Locator: locator,
            RootXPath: null,
            VisibleOnly: visibleOnly,
            IncludeOffViewport: includeOffViewport,
            InteractiveOnly: interactiveOnly,
            InteractiveMode: interactiveMode,
            MaxNodes: 8000,
            ReturnFields: FindReturnFields.Standard);

        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallAsync<ElementRef>(AgentMethods.ResolveElement, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedWpfLocatorTarget?> TryResolveWpfLocatorTargetForAutoAsync(
        Window window,
        ElementLocator locator,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        if (await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var windowHandle = window.Properties.NativeWindowHandle.Value.ToInt64();
        ElementRef element;
        try
        {
            element = timeoutMs > 0
                ? await ResolveWpfElementRefWithWaitAsync(
                    locator,
                    windowHandle,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    cancellationToken).ConfigureAwait(false)
                : await ResolveWpfElementRefAsync(
                    locator,
                    windowHandle,
                    visibleOnly,
                    includeOffViewport,
                    interactiveOnly,
                    interactiveMode,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsAutoWpfLocatorAmbiguous(ex))
        {
            throw new InvalidOperationException(CleanAutoWpfResolveMessage(ex));
        }
        catch (InvalidOperationException ex) when (IsAutoWpfLocatorMiss(ex))
        {
            return null;
        }

        var elementId = _elementHandles.RegisterWpf(
            windowHandle,
            element.XPath,
            element.ElementIdWpf,
            element.Type,
            element.AutomationId,
            element.Name,
            element.ClassName,
            element.Bounds);

        return new ResolvedWpfLocatorTarget(elementId, RequireHandle(elementId));
    }

    private static bool IsAutoWpfLocatorMiss(InvalidOperationException ex)
    {
        var message = ex.GetBaseException().Message ?? ex.Message ?? string.Empty;
        return message.Contains("wpf_resolve:not_found:", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout: element not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutoWpfLocatorAmbiguous(InvalidOperationException ex)
    {
        var message = ex.GetBaseException().Message ?? ex.Message ?? string.Empty;
        return message.Contains("wpf_resolve:ambiguous:", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanAutoWpfResolveMessage(InvalidOperationException ex)
    {
        var message = ex.GetBaseException().Message ?? ex.Message ?? string.Empty;
        var firstLine = message
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .FirstOrDefault() ?? message;

        const string prefix = "wpf_resolve:ambiguous:";
        var index = firstLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? firstLine[(index + prefix.Length)..].Trim()
            : firstLine.Trim();
    }

    private static ElementLocator CreateWpfHandleRecoveryLocator(ElementHandle handle)
    {
        var typeEquals = string.IsNullOrWhiteSpace(handle.Type) ? null : handle.Type;
        var automationId = string.IsNullOrWhiteSpace(handle.AutomationId) ? null : handle.AutomationId;
        var name = string.IsNullOrWhiteSpace(handle.Name) ? null : handle.Name;
        var className = string.IsNullOrWhiteSpace(handle.ClassName) ? null : handle.ClassName;

        if (automationId is not null || name is not null || className is not null)
        {
            return new ElementLocator(
                AutomationId: automationId,
                Name: name,
                ClassName: className,
                TypeEquals: typeEquals,
                Strict: true);
        }

        if (!string.IsNullOrWhiteSpace(handle.XPath))
        {
            return new ElementLocator(XPath: handle.XPath, Strict: true);
        }

        throw new InvalidOperationException("WPF element handle does not contain enough identity data to re-resolve.");
    }

    private async Task<string?> ResolveWpfRootXPathAsync(
        ElementLocator? root,
        long windowHandle,
        CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(root.XPath))
        {
            return root.XPath.Trim();
        }

        var resolved = await ResolveWpfElementRefAsync(
            root,
            windowHandle,
            visibleOnly: false,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            cancellationToken).ConfigureAwait(false);

        return resolved.XPath;
    }

    private static (bool Satisfied, string? FailureReason) CheckStableBounds(
        Rect? bounds,
        int stableMs,
        ref Rect? lastBounds,
        ref long? stableStartTimestamp)
    {
        if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            lastBounds = null;
            stableStartTimestamp = null;
            return (false, "invalid_bounds");
        }

        if (stableMs <= 0)
        {
            return (true, null);
        }

        if (lastBounds is null || bounds != lastBounds)
        {
            lastBounds = bounds;
            stableStartTimestamp = Stopwatch.GetTimestamp();
            return (false, "unstable");
        }

        stableStartTimestamp ??= Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(stableStartTimestamp.Value).TotalMilliseconds >= stableMs)
        {
            return (true, null);
        }

        return (false, "unstable");
    }

    private static bool IsWaitableWpfNotFound(InvalidOperationException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("wpf_resolve:not_found:", StringComparison.OrdinalIgnoreCase);
    }

    private ElementHandle RequireHandle(string elementId)
    {
        if (!_elementHandles.TryGet(elementId, out var handle))
        {
            throw new InvalidOperationException($"Unknown elementId '{elementId}'. Call resolve_element again.");
        }

        return handle;
    }

    private FindElementsResponse AttachWpfElementIds(FindElementsResponse response, long windowHandle)
    {
        if (response.Matches.Count == 0)
        {
            return response;
        }

        var matchesWithIds = response.Matches
            .Select(m =>
            {
                var elementId = _elementHandles.RegisterWpf(
                    windowHandle,
                    m.XPath,
                    m.ElementIdWpf,
                    m.Type,
                    m.AutomationId,
                    m.Name,
                    m.ClassName,
                    m.Bounds);

                return m with { ElementId = elementId, ElementIdWpf = null };
            })
            .ToArray();

        return response with { Matches = matchesWithIds };
    }

    private AutomationElement ResolveUiaElementById(
        Window window,
        ITreeWalker rawWalker,
        string elementId,
        out string xpathUsed)
    {
        var handle = RequireHandle(elementId);
        if (handle.Backend != InspectionBackend.Uia)
        {
            throw new InvalidOperationException($"elementId '{elementId}' is not a UIA handle.");
        }

        xpathUsed = handle.XPath;
        try
        {
            var resolved = TryResolveByXPath(window, new ElementLocator(XPath: handle.XPath), rawWalker)
                ?? throw new InvalidOperationException("Element not found.");

            if (handle.UiaRuntimeId is { Length: > 0 } storedRuntimeId)
            {
                var actual = TryGetRuntimeId(resolved);
                if (actual is not null && !actual.SequenceEqual(storedRuntimeId))
                {
                    // UIA runtime ids can legitimately change for templated/virtualized elements.
                    // Prefer "healing" the handle by updating the stored runtime id as long as the XPath still resolves.
                    _elementHandles.TryUpdateUiaRuntimeId(elementId, actual);
                }
            }

            return resolved;
        }
        catch (InvalidOperationException ex) when (!ex.Message.StartsWith("stale_element:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{elementId}'. Call resolve_element again.");
        }
    }

    private AutomationElement ResolveUiaElementByWpfHandle(
        Window window,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        string elementId,
        ElementHandle handle,
        out string xpathUsed)
    {
        var resolution = ResolveUiaElementByWpfHandleCore(
            window,
            controlWalker,
            rawWalker,
            elementId,
            handle,
            allowAmbiguous: false);
        xpathUsed = resolution.XPath;
        return resolution.Element;
    }

    private WpfUiaResolution ResolveUiaElementByWpfHandleForProperties(
        Window window,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        string elementId,
        ElementHandle handle) =>
        ResolveUiaElementByWpfHandleCore(
            window,
            controlWalker,
            rawWalker,
            elementId,
            handle,
            allowAmbiguous: true);

    private WpfUiaResolution ResolveUiaElementByWpfHandleCore(
        Window window,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        string elementId,
        ElementHandle handle,
        bool allowAmbiguous)
    {
        var ranked = new List<(AutomationElement Element, string XPath, int Score)>();

        foreach (var candidate in EnumerateSelfAndDescendantsDepthFirst(window, controlWalker))
        {
            var score = ScoreUiaCandidateForWpfHandle(candidate, handle);
            if (score <= 0)
            {
                continue;
            }

            var xpath = ComputeXPath(window, candidate, rawWalker);
            if (string.Equals(xpath, handle.XPath, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            ranked.Add((candidate, xpath, score));
        }

        if (ranked.Count == 0 && !string.IsNullOrWhiteSpace(handle.XPath))
        {
            try
            {
                var byXPath = TryResolveByXPath(window, new ElementLocator(XPath: handle.XPath), rawWalker);
                if (byXPath is not null)
                {
                    var xpath = ComputeXPath(window, byXPath, rawWalker);
                    return new WpfUiaResolution(byXPath, xpath, UiaMapping: null);
                }
            }
            catch
            {
            }
        }

        if (ranked.Count == 0)
        {
            throw new InvalidOperationException($"stale_element: not_found for '{elementId}'. Call resolve_element again.");
        }

        var ordered = ranked
            .OrderByDescending(c => c.Score)
            .ThenBy(c => GetXPathDepth(c.XPath))
            .ThenBy(c => c.XPath, StringComparer.Ordinal)
            .ToArray();

        var bestScore = ordered[0].Score;
        var ties = ordered.TakeWhile(c => c.Score == bestScore).ToArray();
        if (ties.Length > 1)
        {
            if (!allowAmbiguous)
            {
                throw new InvalidOperationException(
                    $"elementId '{elementId}' maps ambiguously to UIA properties. Call get_element_properties with a locator.");
            }

            var selected = ordered[0];
            return new WpfUiaResolution(
                selected.Element,
                selected.XPath,
                new UiaMappingDiagnostics(
                    Ambiguous: true,
                    SelectedXPath: selected.XPath,
                    Candidates: ties
                        .Select(candidate => new UiaMappingCandidate(
                            ElementType: candidate.Element.ControlType.ToString(),
                            AutomationId: GetAutomationId(candidate.Element),
                            Name: GetName(candidate.Element),
                            ClassName: GetClassName(candidate.Element),
                            Bounds: ToRect(candidate.Element.BoundingRectangle),
                            XPath: candidate.XPath,
                            Score: candidate.Score))
                        .ToArray()));
        }

        return new WpfUiaResolution(ordered[0].Element, ordered[0].XPath, UiaMapping: null);
    }

    private static int ScoreUiaCandidateForWpfHandle(AutomationElement element, ElementHandle handle)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(handle.AutomationId))
        {
            if (!string.Equals(GetAutomationId(element), handle.AutomationId, StringComparison.Ordinal))
            {
                return 0;
            }

            score += 100;
        }

        if (!string.IsNullOrWhiteSpace(handle.Name) &&
            string.Equals(GetName(element), handle.Name, StringComparison.Ordinal))
        {
            score += 30;
        }

        if (!string.IsNullOrWhiteSpace(handle.ClassName) &&
            string.Equals(GetClassName(element), handle.ClassName, StringComparison.Ordinal))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(handle.Type))
        {
            var expected = handle.Type.Trim();
            if (string.Equals(element.ControlType.ToString(), expected, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetXPathLabel(element), expected, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetClassName(element), expected, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
        }

        if (handle.Bounds is { } expectedBounds)
        {
            score += ScoreBoundsCandidate(ToRect(element.BoundingRectangle), expectedBounds);
        }

        return score;
    }

    private static int ScoreBoundsCandidate(Rect candidate, Rect expected)
    {
        if (!HasUsableBounds(candidate) || !HasUsableBounds(expected))
        {
            return 0;
        }

        var left = Math.Max(candidate.X, expected.X);
        var top = Math.Max(candidate.Y, expected.Y);
        var right = Math.Min(candidate.X + candidate.Width, expected.X + expected.Width);
        var bottom = Math.Min(candidate.Y + candidate.Height, expected.Y + expected.Height);

        if (right > left && bottom > top)
        {
            var intersection = (right - left) * (bottom - top);
            var candidateArea = candidate.Width * candidate.Height;
            var expectedArea = expected.Width * expected.Height;
            var union = candidateArea + expectedArea - intersection;
            var iou = union > 0 ? intersection / union : 0;
            var expectedCoverage = expectedArea > 0 ? intersection / expectedArea : 0;
            var candidateCoverage = candidateArea > 0 ? intersection / candidateArea : 0;

            if (iou >= 0.85)
            {
                return 140;
            }

            if (iou >= 0.6)
            {
                return 100;
            }

            if (expectedCoverage >= 0.9 && candidateCoverage >= 0.7)
            {
                return 80;
            }

            if (expectedCoverage >= 0.9 || candidateCoverage >= 0.9)
            {
                return 25;
            }
        }

        var candidateCenterX = candidate.X + candidate.Width / 2.0;
        var candidateCenterY = candidate.Y + candidate.Height / 2.0;
        var expectedCenterX = expected.X + expected.Width / 2.0;
        var expectedCenterY = expected.Y + expected.Height / 2.0;
        var distance = Math.Sqrt(
            Math.Pow(candidateCenterX - expectedCenterX, 2) +
            Math.Pow(candidateCenterY - expectedCenterY, 2));
        var widthSimilarity = Math.Min(candidate.Width, expected.Width) / Math.Max(candidate.Width, expected.Width);
        var heightSimilarity = Math.Min(candidate.Height, expected.Height) / Math.Max(candidate.Height, expected.Height);
        var sizeSimilarity = Math.Min(widthSimilarity, heightSimilarity);

        if (distance <= 4 && sizeSimilarity >= 0.8)
        {
            return 100;
        }

        if (distance <= 16 && sizeSimilarity >= 0.6)
        {
            return 60;
        }

        if (distance <= 48 && sizeSimilarity >= 0.4)
        {
            return 20;
        }

        return 0;
    }

    private static bool HasUsableBounds(Rect bounds) =>
        bounds.Width > 0 && bounds.Height > 0;

    private static int GetXPathDepth(string xpath) =>
        string.IsNullOrWhiteSpace(xpath) ? int.MaxValue : xpath.Count(c => c == '/');

    private sealed record WpfUiaResolution(
        AutomationElement Element,
        string XPath,
        UiaMappingDiagnostics? UiaMapping);

    private sealed record ElementHandle(
        InspectionBackend Backend,
        long WindowHandle,
        string XPath,
        string? WpfAgentElementId,
        int[]? UiaRuntimeId,
        string? Type,
        string? AutomationId,
        string? Name,
        string? ClassName,
        Rect? Bounds = null);

    private sealed class ElementHandleStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ElementHandle> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
        private readonly int _capacity;

        public ElementHandleStore()
        {
            _capacity = GetEnvInt("WPF_TOOLS_MCP_MAX_ELEMENT_HANDLES", defaultValue: 2000, minValue: 1, maxValue: 200_000);
        }

        public bool TryGet(string elementId, out ElementHandle handle)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(elementId, out handle!))
                {
                    Touch(elementId);
                    return true;
                }

                handle = null!;
                return false;
            }
        }

        public bool Release(string elementId)
        {
            lock (_sync)
            {
                if (!_entries.Remove(elementId))
                {
                    return false;
                }

                if (_lruNodes.Remove(elementId, out var node))
                {
                    _lru.Remove(node);
                }

                return true;
            }
        }

        public bool TryUpdateUiaRuntimeId(string elementId, int[] runtimeId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
            ArgumentNullException.ThrowIfNull(runtimeId);

            lock (_sync)
            {
                if (!_entries.TryGetValue(elementId, out var existing))
                {
                    return false;
                }

                if (existing.Backend != InspectionBackend.Uia)
                {
                    return false;
                }

                _entries[elementId] = existing with { UiaRuntimeId = runtimeId };
                Touch(elementId);
                return true;
            }
        }

        public bool TryUpdateWpfPath(string elementId, string xpath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
            ArgumentException.ThrowIfNullOrWhiteSpace(xpath);

            lock (_sync)
            {
                if (!_entries.TryGetValue(elementId, out var existing))
                {
                    return false;
                }

                if (existing.Backend != InspectionBackend.Wpf)
                {
                    return false;
                }

                _entries[elementId] = existing with { XPath = xpath };
                Touch(elementId);
                return true;
            }
        }

        public bool TryUpdateWpfResolution(string elementId, ElementRef element)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
            ArgumentNullException.ThrowIfNull(element);

            lock (_sync)
            {
                if (!_entries.TryGetValue(elementId, out var existing))
                {
                    return false;
                }

                if (existing.Backend != InspectionBackend.Wpf)
                {
                    return false;
                }

                _entries[elementId] = existing with
                {
                    XPath = element.XPath,
                    WpfAgentElementId = string.IsNullOrWhiteSpace(element.ElementIdWpf)
                        ? existing.WpfAgentElementId
                        : element.ElementIdWpf,
                    Type = string.IsNullOrWhiteSpace(element.Type) ? existing.Type : element.Type,
                    AutomationId = string.IsNullOrWhiteSpace(element.AutomationId) ? existing.AutomationId : element.AutomationId,
                    Name = string.IsNullOrWhiteSpace(element.Name) ? existing.Name : element.Name,
                    ClassName = string.IsNullOrWhiteSpace(element.ClassName) ? existing.ClassName : element.ClassName,
                    Bounds = element.Bounds ?? existing.Bounds
                };
                Touch(elementId);
                return true;
            }
        }

        public string RegisterUia(
            long windowHandle,
            string xpath,
            int[]? runtimeId,
            string type,
            string? automationId,
            string? name,
            string? className,
            Rect? bounds = null)
        {
            var handle = new ElementHandle(
                Backend: InspectionBackend.Uia,
                WindowHandle: windowHandle,
                XPath: xpath,
                WpfAgentElementId: null,
                UiaRuntimeId: runtimeId,
                Type: type,
                AutomationId: automationId,
                Name: name,
                ClassName: className,
                Bounds: bounds);

            return AddHandle("uia_", handle);
        }

        public string RegisterWpf(
            long windowHandle,
            string xpath,
            string? wpfAgentElementId,
            string type,
            string? automationId,
            string? name,
            string? className,
            Rect? bounds = null)
        {
            var handle = new ElementHandle(
                Backend: InspectionBackend.Wpf,
                WindowHandle: windowHandle,
                XPath: xpath,
                WpfAgentElementId: wpfAgentElementId,
                UiaRuntimeId: null,
                Type: type,
                AutomationId: automationId,
                Name: name,
                ClassName: className,
                Bounds: bounds);

            return AddHandle("wpf_", handle);
        }

        private string AddHandle(string prefix, ElementHandle handle)
        {
            lock (_sync)
            {
                EvictIfNeeded();

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var elementId = prefix + CreateRandomId();
                    if (_entries.ContainsKey(elementId))
                    {
                        continue;
                    }

                    _entries[elementId] = handle;
                    var node = _lru.AddFirst(elementId);
                    _lruNodes[elementId] = node;
                    return elementId;
                }

                throw new InvalidOperationException("Failed to allocate unique elementId.");
            }
        }

        private void Touch(string elementId)
        {
            if (_lruNodes.TryGetValue(elementId, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
        }

        private void EvictIfNeeded()
        {
            while (_entries.Count >= _capacity && _lru.Last is { } last)
            {
                var id = last.Value;
                _lru.RemoveLast();
                _lruNodes.Remove(id);
                _entries.Remove(id);
            }
        }

        private static string CreateRandomId()
        {
            Span<byte> bytes = stackalloc byte[12];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
