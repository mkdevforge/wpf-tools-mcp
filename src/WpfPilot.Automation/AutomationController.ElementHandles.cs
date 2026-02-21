using System.Diagnostics;
using System.Security.Cryptography;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    private readonly ElementHandleStore _elementHandles = new();

    public async Task<ResolveElementResponse> ResolveElementAsync(
        InspectionBackend backend,
        ElementLocator locator,
        long? windowHandle = null,
        int timeoutMs = 5000,
        int pollIntervalMs = 100,
        int stableMs = 0,
        bool visibleOnly = true,
        bool interactiveOnly = false,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var trace = BeginTraceSpan("resolve_element");
        try
        {
            timeoutMs = Math.Clamp(timeoutMs, 0, 60_000);
            pollIntervalMs = Math.Clamp(pollIntervalMs, 25, 2000);
            stableMs = Math.Clamp(stableMs, 0, 5000);

            var effectiveBackend = backend == InspectionBackend.Auto
                ? (IsAgentConnected ? InspectionBackend.Wpf : InspectionBackend.Uia)
                : backend;

            var response = await (effectiveBackend switch
            {
                InspectionBackend.Uia => ResolveUiaElementAsync(
                    locator,
                    windowHandle,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    visibleOnly,
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

    public Task<ReleaseElementResponse> ReleaseElementAsync(string elementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        var released = _elementHandles.Release(elementId.Trim());
        return Task.FromResult(new ReleaseElementResponse(released));
    }

    private async Task<ResolveElementResponse> ResolveUiaElementAsync(
        ElementLocator locator,
        long? windowHandle,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        bool visibleOnly,
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
                interactiveOnly,
                interactiveMode,
                cancellationToken).ConfigureAwait(false)
            : ResolveElement(window, locator, controlWalker, rawWalker, ActionKind.Inspect, visibleOnly, interactiveOnly, interactiveMode);

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

        var elementRef = new ElementRef(
            Type: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ClassName: null,
            Bounds: null,
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
                interactiveOnly,
                interactiveMode,
                cancellationToken).ConfigureAwait(false)
            : await ResolveWpfElementRefAsync(
                locator,
                hwnd,
                visibleOnly,
                interactiveOnly,
                interactiveMode,
                cancellationToken).ConfigureAwait(false);

        var elementId = _elementHandles.RegisterWpf(
            hwnd,
            element.XPath,
            element.Type,
            element.AutomationId,
            element.Name,
            element.ClassName);

        var elementRef = element with { ElementId = elementId };
        return new ResolveElementResponse(InspectionBackend.Wpf, elementRef, hwnd);
    }

    private async Task<ElementRef> ResolveWpfElementRefWithWaitAsync(
        ElementLocator locator,
        long windowHandle,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        bool visibleOnly,
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
                    throw new InvalidOperationException($"timeout: element not found after {timeoutMs}ms.");
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
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        var request = new ResolveWpfElementRequest(
            WindowHandle: windowHandle,
            Locator: locator,
            RootXPath: null,
            VisibleOnly: visibleOnly,
            InteractiveOnly: interactiveOnly,
            InteractiveMode: interactiveMode,
            MaxNodes: 2000,
            ReturnFields: FindReturnFields.Standard);

        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallAsync<ElementRef>("wpf/resolve_element", request, cancellationToken).ConfigureAwait(false);
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
                    m.Type,
                    m.AutomationId,
                    m.Name,
                    m.ClassName);

                return m with { ElementId = elementId };
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
                    throw new InvalidOperationException($"stale_element: runtimeId_mismatch for '{elementId}'. Call resolve_element again.");
                }
            }

            return resolved;
        }
        catch (InvalidOperationException ex) when (!ex.Message.StartsWith("stale_element:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{elementId}'. Call resolve_element again.");
        }
    }

    private sealed record ElementHandle(
        InspectionBackend Backend,
        long WindowHandle,
        string XPath,
        int[]? UiaRuntimeId,
        string? Type,
        string? AutomationId,
        string? Name,
        string? ClassName);

    private sealed class ElementHandleStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ElementHandle> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
        private readonly int _capacity;

        public ElementHandleStore()
        {
            _capacity = GetEnvInt("WPFPILOT_MAX_ELEMENT_HANDLES", defaultValue: 2000, minValue: 1, maxValue: 200_000);
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

        public string RegisterUia(
            long windowHandle,
            string xpath,
            int[]? runtimeId,
            string type,
            string? automationId,
            string? name,
            string? className)
        {
            var handle = new ElementHandle(
                Backend: InspectionBackend.Uia,
                WindowHandle: windowHandle,
                XPath: xpath,
                UiaRuntimeId: runtimeId,
                Type: type,
                AutomationId: automationId,
                Name: name,
                ClassName: className);

            return AddHandle("uia_", handle);
        }

        public string RegisterWpf(
            long windowHandle,
            string xpath,
            string type,
            string? automationId,
            string? name,
            string? className)
        {
            var handle = new ElementHandle(
                Backend: InspectionBackend.Wpf,
                WindowHandle: windowHandle,
                XPath: xpath,
                UiaRuntimeId: null,
                Type: type,
                AutomationId: automationId,
                Name: name,
                ClassName: className);

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
