using System.Security.Cryptography;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    private readonly ElementHandleStore _elementHandles = new();

    public Task<ResolveElementResponse> ResolveElementAsync(
        InspectionBackend backend,
        ElementLocator locator,
        long? windowHandle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);

        return backend switch
        {
            InspectionBackend.Uia => ResolveUiaElementAsync(locator, windowHandle, cancellationToken),
            InspectionBackend.Wpf => ResolveWpfElementAsync(locator, windowHandle, cancellationToken),
            InspectionBackend.Auto => ResolveUiaElementAsync(locator, windowHandle, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unsupported backend.")
        };
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
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, locator, controlWalker, rawWalker);
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
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();

        var element = await ResolveWpfElementRefAsync(locator, hwnd, cancellationToken).ConfigureAwait(false);
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

    private async Task<ElementRef> ResolveWpfElementRefAsync(ElementLocator locator, long windowHandle, CancellationToken cancellationToken)
    {
        var request = new ResolveWpfElementRequest(
            WindowHandle: windowHandle,
            Locator: locator,
            RootXPath: null,
            VisibleOnly: true,
            MaxNodes: 2000,
            ReturnFields: FindReturnFields.Standard);

        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallAsync<ElementRef>("wpf/resolve_element", request, cancellationToken).ConfigureAwait(false);
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
