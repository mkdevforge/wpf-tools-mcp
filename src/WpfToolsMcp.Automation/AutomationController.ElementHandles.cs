using System.Diagnostics;
using System.Security.Cryptography;
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
            AutoBackendRoute? autoRoute = null;
            BackendFallbackInfo? fallback = null;
            if (backend == InspectionBackend.Auto)
            {
                var application = EnsureAttached();
                var automation = EnsureAutomation();
                var window = windowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                autoRoute = GetAutoBackendRoute(window);
                var wpfBackendAvailable = false;
                var attemptSequence = GetAutoAgentAttemptSequence();
                if (autoRoute != AutoBackendRoute.Uia)
                {
                    wpfBackendAvailable = autoInject
                        ? await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false) is not null
                        : IsAgentConnected;
                }

                effectiveBackend = SelectAutoBackend(autoRoute.Value, wpfBackendAvailable);
                if (effectiveBackend == InspectionBackend.Uia)
                {
                    var attempted = autoRoute != AutoBackendRoute.Uia &&
                                    autoInject &&
                                    GetAutoAgentAttemptSequence() != attemptSequence;
                    var failure = autoRoute == AutoBackendRoute.Uia
                        ? null
                        : GetWpfBackendFailure();
                    fallback = CreateWpfToUiaFallback(attempted, failure);
                }
            }

            ResolveElementResponse response;
            try
            {
                response = await ResolveElementWithBackendAsync(effectiveBackend).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (
                backend == InspectionBackend.Auto &&
                effectiveBackend == InspectionBackend.Wpf &&
                (IsPerWindowAutoWpfMiss(ex) ||
                 autoRoute == AutoBackendRoute.ProbeWpfThenUia && IsAutoWpfLocatorMiss(ex)))
            {
                response = await ResolveElementWithBackendAsync(InspectionBackend.Uia).ConfigureAwait(false);
                fallback = CreateWpfToUiaFallback(
                    attempted: true,
                    failure: ClassifyAutoWpfFallbackFailure(ex));
            }
            catch (Exception ex) when (
                backend == InspectionBackend.Auto &&
                effectiveBackend == InspectionBackend.Wpf &&
                ShouldFallbackFromAutoWpfResolveFailure(ex, IsAgentConnected))
            {
                response = await ResolveElementWithBackendAsync(InspectionBackend.Uia).ConfigureAwait(false);
                fallback = CreateWpfToUiaFallback(
                    attempted: true,
                    failure: ClassifyAutoWpfFallbackFailure(ex));
            }

            response = response with { Fallback = fallback };

            trace?.SetSummary($"{response.BackendUsed} {response.Element.Type} {response.Element.XPath}");
            return response;

            Task<ResolveElementResponse> ResolveElementWithBackendAsync(InspectionBackend selectedBackend) =>
                selectedBackend switch
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
                    _ => throw new ArgumentOutOfRangeException(nameof(selectedBackend), selectedBackend, "Unsupported backend.")
                };
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
            var release = _elementHandles.Release(id);
            if (!release.Released && IsRetiredElementId(id))
            {
                throw new InvalidOperationException(
                    $"stale_element: process_replaced for '{id}'. Call resolve_element again in the successor session.");
            }

            if (!string.IsNullOrWhiteSpace(release.WpfAgentElementIdToRelease))
            {
                try
                {
                    var client = await EnsureAgentConnectedOrNullAsync(CancellationToken.None).ConfigureAwait(false);
                    if (client is not null)
                    {
                        _ = await client.CallAsync<ReleaseElementResponse>(
                            "wpf/release_element",
                            new ReleaseWpfElementRequest(release.WpfAgentElementIdToRelease),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Public handle release should not fail if the in-proc weak handle already disappeared.
                }
            }

            trace?.SetSummary($"released={release.Released}");
            return new ReleaseElementResponse(release.Released);
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

        AutomationElement element;
        try
        {
            element = timeoutMs > 0
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
        }
        catch (UiaLocatorAmbiguousException ex)
        {
            throw BuildUiaAmbiguityException(window, rawWalker, hwnd, ex);
        }

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

        var elementRef = BuildElementRefUia(
            element,
            xpath,
            FindReturnFields.Standard,
            elementId,
            TryGetClientBoundsScreen(window, out var clientBounds) ? clientBounds : null);

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
                cancellationToken,
                detailedAmbiguity: true).ConfigureAwait(false)
            : await ResolveWpfElementRefDetailedAsync(
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
        CancellationToken cancellationToken,
        bool detailedAmbiguity = false)
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
                element = detailedAmbiguity
                    ? await ResolveWpfElementRefDetailedAsync(
                        currentLocator,
                        windowHandle,
                        visibleOnly,
                        includeOffViewport,
                        interactiveOnly,
                        interactiveMode,
                        cancellationToken).ConfigureAwait(false)
                    : await ResolveWpfElementRefAsync(
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
        return await client.CallAsync<ElementRef>("wpf/resolve_element", request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ElementRef> ResolveWpfElementRefDetailedAsync(
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
        if (!AgentSupportsCapability(client, AgentProtocolCapabilities.ResolveElementDetailed))
        {
            try
            {
                return await client.CallAsync<ElementRef>(
                    "wpf/resolve_element",
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AgentRemoteException ex) when (IsAutoWpfLocatorAmbiguous(ex))
            {
                throw CreateLegacyWpfAmbiguityException(ex, windowHandle);
            }
        }

        var response = await client.CallAsync<ResolveWpfElementDetailedResponse>(
            AgentProtocolCapabilities.ResolveElementDetailed,
            request,
            cancellationToken).ConfigureAwait(false);

        if (response.Ambiguity is not null)
        {
            throw new ElementResolutionAmbiguityException(
                AttachPublicWpfCandidateIds(response.Ambiguity, windowHandle));
        }

        return response.Element
            ?? throw new InvalidOperationException("wpf_resolve:invalid_response: Detailed resolution returned no element or ambiguity.");
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
        if (GetAutoBackendRoute(window) == AutoBackendRoute.Uia)
        {
            return null;
        }

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
            throw CreateLegacyWpfAmbiguityException(ex, windowHandle);
        }
        catch (InvalidOperationException ex) when (IsAutoWpfLocatorMiss(ex))
        {
            return null;
        }
        catch (InvalidOperationException ex) when (IsPerWindowAutoWpfMiss(ex))
        {
            return null;
        }
        catch (Exception ex) when (ShouldFallbackFromAutoWpfResolveFailure(ex, IsAgentConnected))
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
        var message = GetInternalFailureMessage(ex);
        return message.Contains("wpf_resolve:not_found:", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout: element not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutoWpfLocatorAmbiguous(InvalidOperationException ex)
    {
        var message = GetInternalFailureMessage(ex);
        return message.Contains("wpf_resolve:ambiguous:", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldFallbackFromAutoWpfResolveFailure(
        Exception exception,
        bool agentConnectionHealthy)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException or ElementResolutionAmbiguityException)
        {
            return false;
        }

        if (exception is InvalidOperationException invalidOperation &&
            (IsAutoWpfLocatorMiss(invalidOperation) || IsAutoWpfLocatorAmbiguous(invalidOperation)))
        {
            return false;
        }

        return exception is AgentRemoteException or System.Text.Json.JsonException or TimeoutException ||
               (!agentConnectionHealthy &&
                exception is IOException or InvalidOperationException);
    }

    internal static ElementResolutionAmbiguityException CreateLegacyWpfAmbiguityException(
        InvalidOperationException exception,
        long windowHandle)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var ambiguity = new ResolveElementAmbiguity(
            Code: "ambiguous_element",
            BackendUsed: InspectionBackend.Wpf,
            WindowHandleUsed: windowHandle,
            ReturnedCandidates: 0,
            DiscoveredCandidates: GetLegacyWpfAmbiguityCount(exception),
            Truncated: true,
            Candidates: [],
            TruncatedReason: "legacyAgent");
        return new ElementResolutionAmbiguityException(ambiguity);
    }

    private static int GetLegacyWpfAmbiguityCount(InvalidOperationException exception)
    {
        var message = GetInternalFailureMessage(exception);
        const string marker = "(found ";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var countStart = markerIndex + marker.Length;
            var countEnd = message.IndexOf(')', countStart);
            if (countEnd > countStart &&
                int.TryParse(message.AsSpan(countStart, countEnd - countStart), out var count) &&
                count >= 2)
            {
                return count;
            }
        }

        return 2;
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
        var message = GetInternalFailureMessage(ex);
        return message.Contains("wpf_resolve:not_found:", StringComparison.OrdinalIgnoreCase);
    }

    private ElementHandle RequireHandle(string elementId)
    {
        if (!_elementHandles.TryGet(elementId, out var handle))
        {
            if (IsRetiredElementId(elementId))
            {
                throw new InvalidOperationException(
                    $"stale_element: process_replaced for '{elementId}'. Call resolve_element again in the successor session.");
            }

            throw new InvalidOperationException($"stale_element: Unknown elementId '{elementId}'. Call resolve_element again.");
        }

        return handle;
    }

    private ElementResolutionAmbiguityException BuildUiaAmbiguityException(
        Window window,
        ITreeWalker rawWalker,
        long windowHandle,
        UiaLocatorAmbiguousException exception)
    {
        const int maxCandidates = 5;
        var candidates = new List<ResolveElementCandidate>(
            Math.Min(maxCandidates, exception.Candidates.Count));
        var candidateUnavailable = false;
        var viewportBounds = TryGetClientBoundsScreen(window, out var clientBounds) ? clientBounds : null;

        for (var index = 0; index < exception.Candidates.Count && index < maxCandidates; index++)
        {
            var element = exception.Candidates[index];
            try
            {
                var xpath = ComputeXPath(window, element, rawWalker);
                var elementId = _elementHandles.RegisterUia(
                    windowHandle,
                    xpath,
                    TryGetRuntimeId(element),
                    element.ControlType.ToString(),
                    GetAutomationId(element),
                    GetName(element),
                    GetClassName(element));
                candidates.Add(new ResolveElementCandidate(
                    index,
                    BuildElementRefUia(element, xpath, FindReturnFields.Standard, elementId, viewportBounds)));
            }
            catch
            {
                candidateUnavailable = true;
            }
        }

        var truncatedByLimit = exception.Candidates.Count > maxCandidates;
        var ambiguity = new ResolveElementAmbiguity(
            Code: "ambiguous_element",
            BackendUsed: InspectionBackend.Uia,
            WindowHandleUsed: windowHandle,
            ReturnedCandidates: candidates.Count,
            DiscoveredCandidates: exception.Candidates.Count,
            Truncated: truncatedByLimit || candidateUnavailable,
            Candidates: candidates,
            TruncatedReason: truncatedByLimit ? "maxCandidates" : candidateUnavailable ? "candidateUnavailable" : null);

        return new ElementResolutionAmbiguityException(ambiguity);
    }

    private ResolveElementAmbiguity AttachPublicWpfCandidateIds(
        ResolveElementAmbiguity ambiguity,
        long windowHandle)
    {
        var candidates = ambiguity.Candidates
            .Select(candidate =>
            {
                var element = candidate.Element;
                var elementId = _elementHandles.RegisterWpf(
                    windowHandle,
                    element.XPath,
                    element.ElementIdWpf,
                    element.Type,
                    element.AutomationId,
                    element.Name,
                    element.ClassName,
                    element.Bounds);

                return candidate with
                {
                    Element = element with { ElementId = elementId, ElementIdWpf = null }
                };
            })
            .ToArray();

        return ambiguity with
        {
            BackendUsed = InspectionBackend.Wpf,
            WindowHandleUsed = windowHandle,
            ReturnedCandidates = candidates.Length,
            Candidates = candidates
        };
    }

    private static FindElementsResponse StripElementIds(FindElementsResponse response)
    {
        if (response.Matches.Count == 0)
        {
            return response;
        }

        var matchesWithoutIds = response.Matches
            .Select(match => match with
            {
                ElementId = null,
                ElementIdUia = null,
                ElementIdWpf = null
            })
            .ToArray();

        return response with { Matches = matchesWithoutIds };
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
        out string xpathUsed,
        UiaHandleResolutionMode resolutionMode)
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
                if (actual is null || !actual.SequenceEqual(storedRuntimeId))
                {
                    if (resolutionMode == UiaHandleResolutionMode.RequireRegisteredIdentity)
                    {
                        throw new InvalidOperationException(
                            $"stale_element: identity_changed for '{elementId}'. Call resolve_element again.");
                    }
                }
            }
            else if (resolutionMode == UiaHandleResolutionMode.RequireRegisteredIdentity)
            {
                throw new InvalidOperationException(
                    $"stale_element: identity_unverifiable for '{elementId}'. Call resolve_element again.");
            }

            return resolved;
        }
        catch (InvalidOperationException ex) when (!ex.Message.StartsWith("stale_element:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{elementId}'. Call resolve_element again.");
        }
    }

    private enum UiaHandleResolutionMode
    {
        ObserveCurrentXPathOccupant,
        RequireRegisteredIdentity
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
            var mappingCandidates = ties
                .Take(MaximumUiaMappingCandidates)
                .Select(candidate => new UiaMappingCandidate(
                    ElementType: candidate.Element.ControlType.ToString(),
                    AutomationId: GetAutomationId(candidate.Element),
                    Name: GetName(candidate.Element),
                    ClassName: GetClassName(candidate.Element),
                    Bounds: ToRect(candidate.Element.BoundingRectangle),
                    XPath: candidate.XPath,
                    Score: candidate.Score))
                .ToArray();
            return new WpfUiaResolution(
                selected.Element,
                selected.XPath,
                new UiaMappingDiagnostics(
                    Ambiguous: true,
                    SelectedXPath: selected.XPath,
                    Candidates: mappingCandidates,
                    ReturnedCandidates: mappingCandidates.Length,
                    TotalCandidates: ties.Length,
                    Truncated: mappingCandidates.Length < ties.Length));
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

    internal sealed record ElementHandle(
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

    internal readonly record struct ElementHandleRelease(
        bool Released,
        string? WpfAgentElementIdToRelease);

    internal readonly record struct ElementHandleUpdate(
        bool Updated,
        string? WpfAgentElementIdToRelease);

    internal sealed class ElementHandleStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ElementHandle> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<WpfAgentHandleKey, int> _wpfAgentHandleReferenceCounts = new();
        private readonly int _capacity;

        public ElementHandleStore()
            : this(GetEnvInt("WPF_TOOLS_MCP_MAX_ELEMENT_HANDLES", defaultValue: 2000, minValue: 1, maxValue: 200_000))
        {
        }

        internal ElementHandleStore(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= 1.");
            }

            _capacity = capacity;
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

        public ElementHandleRelease Release(string elementId)
        {
            lock (_sync)
            {
                if (!TryRemoveEntry(elementId, out var handle, out var releaseWpfAgentElementId))
                {
                    return new ElementHandleRelease(Released: false, WpfAgentElementIdToRelease: null);
                }

                return new ElementHandleRelease(
                    Released: true,
                    WpfAgentElementIdToRelease: releaseWpfAgentElementId ? handle.WpfAgentElementId : null);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
                _lru.Clear();
                _lruNodes.Clear();
                _wpfAgentHandleReferenceCounts.Clear();
            }
        }

        public (IReadOnlyList<string> ElementIds, IReadOnlyList<long> WindowHandles) SnapshotIdentities()
        {
            lock (_sync)
            {
                return (
                    _entries.Keys.ToArray(),
                    _entries.Values.Select(entry => entry.WindowHandle).Distinct().ToArray());
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

        public ElementHandleUpdate TryUpdateWpfResolution(string elementId, ElementRef element)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
            ArgumentNullException.ThrowIfNull(element);

            lock (_sync)
            {
                if (!_entries.TryGetValue(elementId, out var existing))
                {
                    return new ElementHandleUpdate(Updated: false, WpfAgentElementIdToRelease: null);
                }

                if (existing.Backend != InspectionBackend.Wpf)
                {
                    return new ElementHandleUpdate(Updated: false, WpfAgentElementIdToRelease: null);
                }

                var updated = existing with
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

                string? wpfAgentElementIdToRelease = null;
                if (GetWpfAgentHandleKey(existing) != GetWpfAgentHandleKey(updated))
                {
                    if (RemoveWpfAgentHandleReference(existing))
                    {
                        wpfAgentElementIdToRelease = existing.WpfAgentElementId;
                    }

                    AddWpfAgentHandleReference(updated);
                }

                _entries[elementId] = updated;
                Touch(elementId);
                return new ElementHandleUpdate(
                    Updated: true,
                    WpfAgentElementIdToRelease: wpfAgentElementIdToRelease);
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

        public bool TryRegisterUiaKeeping(
            string requiredElementId,
            long windowHandle,
            string xpath,
            int[] runtimeId,
            string type,
            string? automationId,
            string? name,
            string? className,
            Rect? bounds,
            out string? elementId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requiredElementId);
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

            lock (_sync)
            {
                if (!_entries.ContainsKey(requiredElementId))
                {
                    elementId = null;
                    return false;
                }

                string? candidateId = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var generated = "uia_" + CreateRandomId();
                    if (!_entries.ContainsKey(generated))
                    {
                        candidateId = generated;
                        break;
                    }
                }

                if (candidateId is null)
                {
                    throw new InvalidOperationException("Failed to allocate unique elementId.");
                }

                while (_entries.Count >= _capacity)
                {
                    var eviction = _lru.Last;
                    while (eviction is not null &&
                           string.Equals(eviction.Value, requiredElementId, StringComparison.Ordinal))
                    {
                        eviction = eviction.Previous;
                    }

                    if (eviction is null)
                    {
                        elementId = null;
                        return false;
                    }

                    _ = TryRemoveEntry(eviction.Value, out _, out _);
                }

                _entries[candidateId] = handle;
                var node = _lru.AddFirst(candidateId);
                _lruNodes[candidateId] = node;
                elementId = candidateId;
                return true;
            }
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
                    AddWpfAgentHandleReference(handle);
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
                _ = TryRemoveEntry(id, out _, out _);
            }
        }

        private bool TryRemoveEntry(
            string elementId,
            out ElementHandle handle,
            out bool releaseWpfAgentElementId)
        {
            if (!_entries.Remove(elementId, out handle!))
            {
                releaseWpfAgentElementId = false;
                return false;
            }

            if (_lruNodes.Remove(elementId, out var node))
            {
                _lru.Remove(node);
            }

            releaseWpfAgentElementId = RemoveWpfAgentHandleReference(handle);
            return true;
        }

        private void AddWpfAgentHandleReference(ElementHandle handle)
        {
            if (GetWpfAgentHandleKey(handle) is not { } key)
            {
                return;
            }

            _wpfAgentHandleReferenceCounts.TryGetValue(key, out var count);
            _wpfAgentHandleReferenceCounts[key] = checked(count + 1);
        }

        private bool RemoveWpfAgentHandleReference(ElementHandle handle)
        {
            if (GetWpfAgentHandleKey(handle) is not { } key ||
                !_wpfAgentHandleReferenceCounts.TryGetValue(key, out var count))
            {
                return false;
            }

            if (count > 1)
            {
                _wpfAgentHandleReferenceCounts[key] = count - 1;
                return false;
            }

            _wpfAgentHandleReferenceCounts.Remove(key);
            return true;
        }

        private static WpfAgentHandleKey? GetWpfAgentHandleKey(ElementHandle handle)
        {
            return handle.Backend == InspectionBackend.Wpf &&
                   !string.IsNullOrWhiteSpace(handle.WpfAgentElementId)
                ? new WpfAgentHandleKey(handle.WindowHandle, handle.WpfAgentElementId.Trim())
                : null;
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

        private readonly record struct WpfAgentHandleKey(long WindowHandle, string ElementId);
    }
}
