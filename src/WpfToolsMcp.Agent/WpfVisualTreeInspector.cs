using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Snoop.Data.Tree;
using Snoop.Infrastructure.Helpers;
using Snoop.Infrastructure.SelectionHighlight;
using WpfToolsMcp.Contracts;
using ContractRect = WpfToolsMcp.Contracts.Rect;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    private static string? _activeHighlightOwnerId;
    private static IDisposable? _activeHighlight;
    private static DispatcherTimer? _highlightTimer;
    private static Brush? _savedHighlightBorderBrush;
    private static double? _savedHighlightBorderThickness;
    private static bool? _savedHighlightEnabled;
    private static readonly WpfElementHandleStore ElementHandles = new();

    private sealed class WpfElementHandleStore
    {
        private readonly object _sync = new();
        private readonly ConditionalWeakTable<DependencyObject, ElementOwnerHandles> _byObject = new();
        private readonly Dictionary<string, HandleRegistration> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _idsByOwner = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.Ordinal);
        private readonly int _capacity = GetHandleCapacity();

        public string Register(string ownerId, DependencyObject element)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            ArgumentNullException.ThrowIfNull(element);

            lock (_sync)
            {
                var ownerHandles = _byObject.GetOrCreateValue(element);
                if (ownerHandles.Ids.TryGetValue(ownerId, out var existingId) &&
                    _byId.TryGetValue(existingId, out var existing) &&
                    string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    Touch(existingId);
                    return existingId;
                }

                EvictIfNeeded();

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var id = "wpfobj_" + CreateRandomId();
                    if (_byId.ContainsKey(id))
                    {
                        continue;
                    }

                    ownerHandles.Ids[ownerId] = id;
                    _byId[id] = new HandleRegistration(ownerId, new WeakReference<DependencyObject>(element));
                    if (!_idsByOwner.TryGetValue(ownerId, out var ownerIds))
                    {
                        ownerIds = new HashSet<string>(StringComparer.Ordinal);
                        _idsByOwner[ownerId] = ownerIds;
                    }

                    ownerIds.Add(id);
                    _lruNodes[id] = _lru.AddFirst(id);
                    return id;
                }
            }

            throw new InvalidOperationException("Failed to allocate unique WPF element handle.");
        }

        public DependencyObject Resolve(string ownerId, long windowHandle, string elementId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            if (windowHandle == 0)
            {
                throw new ArgumentException("WindowHandle is required when resolving a WPF element handle.");
            }

            if (string.IsNullOrWhiteSpace(elementId))
            {
                throw new ArgumentException("elementId is required.");
            }

            var id = elementId.Trim();
            HandleRegistration registration;
            lock (_sync)
            {
                if (!_byId.TryGetValue(id, out registration) ||
                    !string.Equals(registration.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    throw AgentToolError.InvalidOperation(
                        "wpf_handle_stale",
                        $"wpf_handle_stale:not_found: '{id}'.");
                }

                Touch(id);
            }

            if (!registration.Reference.TryGetTarget(out var element))
            {
                Release(ownerId, id);
                throw AgentToolError.InvalidOperation(
                    "wpf_handle_stale",
                    $"wpf_handle_stale:collected: '{id}'.");
            }

            var window = GetContainingWindow(element);
            if (window is null)
            {
                Release(ownerId, id);
                throw AgentToolError.InvalidOperation(
                    "wpf_handle_stale",
                    $"wpf_handle_stale:detached: '{id}'.");
            }

            var actualHandle = new WindowInteropHelper(window).Handle;
            if (actualHandle == IntPtr.Zero || actualHandle.ToInt64() != windowHandle)
            {
                throw AgentToolError.InvalidOperation(
                    "wpf_handle_stale",
                    $"wpf_handle_stale:window_mismatch: '{id}' expected={windowHandle} actual={actualHandle.ToInt64()}.");
            }

            return element;
        }

        public bool Release(string ownerId, string elementId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
            if (string.IsNullOrWhiteSpace(elementId))
            {
                return false;
            }

            var id = elementId.Trim();
            lock (_sync)
            {
                return RemoveRegistration(id, ownerId);
            }
        }

        public void ReleaseOwner(string ownerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

            lock (_sync)
            {
                if (!_idsByOwner.TryGetValue(ownerId, out var ownerIds))
                {
                    return;
                }

                foreach (var id in ownerIds.ToArray())
                {
                    _ = RemoveRegistration(id, ownerId);
                }
            }
        }

        private void Touch(string id)
        {
            if (_lruNodes.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
        }

        private void EvictIfNeeded()
        {
            while (_byId.Count >= _capacity && _lru.Last is { } last)
            {
                if (!RemoveRegistration(last.Value, requiredOwnerId: null))
                {
                    _lru.RemoveLast();
                    _lruNodes.Remove(last.Value);
                }
            }
        }

        private bool RemoveRegistration(string id, string? requiredOwnerId)
        {
            if (!_byId.TryGetValue(id, out var registration) ||
                (requiredOwnerId is not null &&
                 !string.Equals(registration.OwnerId, requiredOwnerId, StringComparison.Ordinal)))
            {
                return false;
            }

            _byId.Remove(id);
            if (_lruNodes.Remove(id, out var node))
            {
                _lru.Remove(node);
            }

            if (_idsByOwner.TryGetValue(registration.OwnerId, out var ownerIds))
            {
                ownerIds.Remove(id);
                if (ownerIds.Count == 0)
                {
                    _idsByOwner.Remove(registration.OwnerId);
                }
            }

            if (registration.Reference.TryGetTarget(out var element) &&
                _byObject.TryGetValue(element, out var ownerHandles) &&
                ownerHandles.Ids.TryGetValue(registration.OwnerId, out var mappedId) &&
                string.Equals(mappedId, id, StringComparison.Ordinal))
            {
                ownerHandles.Ids.Remove(registration.OwnerId);
            }

            return true;
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

        private static int GetHandleCapacity()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_AGENT_MAX_WPF_HANDLES");
                if (int.TryParse(raw, out var value))
                {
                    return Math.Clamp(value, 1, 200_000);
                }
            }
            catch
            {
            }

            return 20_000;
        }

        private sealed class ElementOwnerHandles
        {
            public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);
        }

        private readonly record struct HandleRegistration(
            string OwnerId,
            WeakReference<DependencyObject> Reference);
    }

    private readonly record struct WpfTreeFieldSet(
        bool IncludeClassName,
        bool IncludeBounds,
        bool IncludeIsEnabled,
        bool IncludeVisibility,
        bool IncludeIsVisible,
        bool IncludeDataContextType)
    {
        private static readonly string[] KnownFields =
        [
            "className",
            "bounds",
            "isEnabled",
            "isOffscreen",
            "visibility",
            "isVisible",
            "dataContextType"
        ];

        public static WpfTreeFieldSet Resolve(TreePreset preset, IReadOnlyList<string>? fields)
        {
            if (fields is not null && fields.Count > 0)
            {
                var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        continue;
                    }

                    normalized.Add(field.Trim());
                }

                var unknown = normalized.Where(f => !KnownFields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0)
                {
                    throw new ArgumentException(
                        $"Unknown field(s): {string.Join(", ", unknown)}. Known fields: {string.Join(", ", KnownFields)}.");
                }

                return new WpfTreeFieldSet(
                    IncludeClassName: normalized.Contains("className"),
                    IncludeBounds: normalized.Contains("bounds"),
                    IncludeIsEnabled: normalized.Contains("isEnabled"),
                    IncludeVisibility: normalized.Contains("visibility"),
                    IncludeIsVisible: normalized.Contains("isVisible"),
                    IncludeDataContextType: normalized.Contains("dataContextType"));
            }

            return preset switch
            {
                TreePreset.Minimal => new WpfTreeFieldSet(false, false, false, false, false, false),
                TreePreset.Standard => new WpfTreeFieldSet(true, true, true, true, true, true),
                TreePreset.Debug => new WpfTreeFieldSet(true, true, true, true, true, true),
                _ => new WpfTreeFieldSet(false, false, false, false, false, false)
            };
        }
    }

    private sealed class WpfTreeBuildContext(
        VisualTreeService treeService,
        WpfTreeFieldSet fieldSet,
        int maxNodes,
        bool visibleOnly,
        bool includeOffViewport,
        ContractRect? viewportBounds,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        public VisualTreeService TreeService { get; } = treeService;
        public WpfTreeFieldSet FieldSet { get; } = fieldSet;
        public int MaxNodes { get; } = maxNodes;
        public bool VisibleOnly { get; } = visibleOnly;
        public bool IncludeOffViewport { get; } = includeOffViewport;
        public ContractRect? ViewportBounds { get; } = viewportBounds;
        public bool InteractiveOnly { get; } = interactiveOnly;
        public InteractiveMode InteractiveMode { get; } = interactiveMode;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public int ReturnedNodes { get; set; }
        public int ScannedNodes { get; set; }

        public bool Truncated { get; private set; }
        public string? TruncatedReason { get; private set; }

        public void MarkTruncated(string reason)
        {
            if (Truncated)
            {
                return;
            }

            Truncated = true;
            TruncatedReason = reason;
        }
    }

    private static readonly object DependencyPropertyCacheSync = new();
    private static readonly Dictionary<Type, DependencyProperty[]> DependencyPropertyCache = new();

    private static DependencyProperty[] GetDependencyPropertiesCached(Type type)
    {
        lock (DependencyPropertyCacheSync)
        {
            if (DependencyPropertyCache.TryGetValue(type, out var cached))
            {
                return cached;
            }
        }

        var set = new HashSet<DependencyProperty>();

        for (var current = type; current is not null; current = current.BaseType)
        {
            var fields = current.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(DependencyProperty))
                {
                    continue;
                }

                try
                {
                    if (field.GetValue(null) is DependencyProperty dp)
                    {
                        set.Add(dp);
                    }
                }
                catch
                {
                }
            }
        }

        var result = set.OrderBy(dp => dp.Name, StringComparer.Ordinal).ToArray();

        lock (DependencyPropertyCacheSync)
        {
            DependencyPropertyCache[type] = result;
        }

        return result;
    }

    private static TreeNode? BuildWpfTreeNode(
        DependencyObject element,
        string xpath,
        int depth,
        bool isRoot,
        WpfTreeBuildContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.ScannedNodes++;

        if (!isRoot && !ShouldIncludeWpfElement(element, context.VisibleOnly, context.IncludeOffViewport, context.ViewportBounds))
        {
            return null;
        }

        if (!isRoot && context.ReturnedNodes >= context.MaxNodes)
        {
            context.MarkTruncated("maxNodes");
            return null;
        }

        // Reserve a slot so maxNodes is enforced during recursion.
        context.ReturnedNodes++;

        var rawChildren = GetChildrenWpf(element, context.TreeService, context.VisibleOnly, context.IncludeOffViewport, context.ViewportBounds);
        var childrenCount = rawChildren.Length;

        var children = Array.Empty<TreeNode>();
        if (depth > 1 && childrenCount > 0)
        {
            if (context.ReturnedNodes < context.MaxNodes)
            {
                children = BuildWpfChildren(rawChildren, xpath, depth - 1, context);
            }
            else
            {
                context.MarkTruncated("maxNodes");
            }
        }

        var isInteractive = IsInteractiveWpf(element, context.InteractiveMode);
        if (!isRoot && context.InteractiveOnly && !isInteractive && childrenCount == 0)
        {
            context.ReturnedNodes--;
            return null;
        }

        string? className = null;
        ContractRect? bounds = null;
        bool? isEnabled = null;
        string? visibility = null;
        bool? isVisible = null;
        string? dataContextType = null;

        if (context.FieldSet.IncludeClassName)
        {
            className = element.GetType().FullName;
        }

        if (context.FieldSet.IncludeBounds)
        {
            bounds = GetBoundsWpf(element);
        }

        if (context.FieldSet.IncludeIsEnabled)
        {
            isEnabled = GetIsEnabledWpf(element);
        }

        if (context.FieldSet.IncludeVisibility)
        {
            visibility = GetVisibilityWpf(element);
        }

        if (context.FieldSet.IncludeIsVisible)
        {
            isVisible = IsVisibleWpf(element);
        }

        if (context.FieldSet.IncludeDataContextType)
        {
            dataContextType = GetDataContextType(element);
        }

        return new TreeNode(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ChildrenCount: childrenCount,
            Children: children,
            ClassName: className,
            Bounds: bounds,
            IsEnabled: isEnabled,
            IsOffscreen: null,
            Visibility: visibility,
            IsVisible: isVisible,
            DataContextType: dataContextType);
    }

    private static TreeNode[] BuildWpfChildren(
        DependencyObject[] rawChildren,
        string parentXPath,
        int remainingDepth,
        WpfTreeBuildContext context)
    {
        if (rawChildren.Length == 0)
        {
            return [];
        }

        var labels = rawChildren.Select(GetXPathLabel).ToArray();
        var countsByLabel = labels
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<TreeNode>(rawChildren.Length);

        for (var i = 0; i < rawChildren.Length; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (context.ReturnedNodes >= context.MaxNodes)
            {
                context.MarkTruncated("maxNodes");
                break;
            }

            var child = rawChildren[i];
            var label = labels[i];

            runningIndexByLabel.TryGetValue(label, out var currentIndex);
            currentIndex++;
            runningIndexByLabel[label] = currentIndex;

            var includeIndex = countsByLabel[label] > 1;
            var segment = includeIndex ? $"{label}[{currentIndex}]" : label;
            var childXPath = $"{parentXPath}/{segment}";

            var node = BuildWpfTreeNode(child, childXPath, remainingDepth, isRoot: false, context);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes.ToArray();
    }

    private static DependencyObject[] GetChildrenWpf(
        DependencyObject parent,
        VisualTreeService treeService,
        bool visibleOnly,
        bool includeOffViewport,
        ContractRect? viewportBounds)
    {
        var children = treeService.GetChildren(parent)
            .OfType<DependencyObject>()
            .ToArray();

        if (visibleOnly)
        {
            children = children.Where(c => ShouldIncludeWpfElement(c, visibleOnly: true, includeOffViewport, viewportBounds)).ToArray();
        }

        return children;
    }

    private static string GetXPathLabel(DependencyObject element) =>
        element is Window ? "Window" : element.GetType().Name;

    private sealed record XPathSegment(string TypeName, int? OneBasedIndex);

    private sealed class WpfLocatorAmbiguousException : InvalidOperationException
    {
        private const string ErrorPrefix = "wpf_resolve:ambiguous:";

        public WpfLocatorAmbiguousException(
            string message,
            IReadOnlyList<(DependencyObject Element, string XPath)> orderedCandidates)
            : base(message.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase)
                ? message
                : $"{ErrorPrefix} {message}")
        {
            OrderedCandidates = orderedCandidates.ToArray();
        }

        public IReadOnlyList<(DependencyObject Element, string XPath)> OrderedCandidates { get; }
    }

    private static XPathSegment ParseXPathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("XPath segment cannot be empty.");
        }

        var bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex < 0)
        {
            return new XPathSegment(segment, null);
        }

        var closingIndex = segment.IndexOf(']', bracketIndex + 1);
        if (closingIndex < 0)
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': missing closing ']'.");
        }

        if (closingIndex != segment.Length - 1)
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': unexpected characters after ']'.");
        }

        var typeName = segment[..bracketIndex];
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': missing type name.");
        }

        var indexText = segment[(bracketIndex + 1)..closingIndex];
        if (!int.TryParse(indexText, out var oneBasedIndex))
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': index is not a number.");
        }

        return new XPathSegment(typeName, oneBasedIndex);
    }

    private static DependencyObject ResolveByXPath(
        VisualTreeService treeService,
        Window window,
        string xpath,
        bool visibleOnly,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeXPath(xpath);
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseXPathSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            return window;
        }

        var current = (DependencyObject)window;
        var startIndex = 0;
        if (string.Equals(segments[0].TypeName, "Window", StringComparison.OrdinalIgnoreCase))
        {
            startIndex = 1;
        }

        for (var i = startIndex; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segment = segments[i];
            var rawChildren = GetChildrenWpf(current, treeService, visibleOnly, includeOffViewport: true, viewportBounds: null);
            var matching = rawChildren
                .Where(c => string.Equals(GetXPathLabel(c), segment.TypeName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matching.Length == 0)
            {
                throw new InvalidOperationException(
                    $"XPath segment '{segment.TypeName}' did not match any children under '{normalized}'.");
            }

            if (segment.OneBasedIndex is int index)
            {
                if (index <= 0)
                {
                    throw new ArgumentException($"Invalid XPath segment '{segment.TypeName}[{index}]': index must be >= 1.");
                }

                if (index > matching.Length)
                {
                    throw new InvalidOperationException(
                        $"XPath segment '{segment.TypeName}[{index}]' is out of range (found {matching.Length}).");
                }

                current = matching[index - 1];
                continue;
            }

            if (matching.Length > 1)
            {
                throw new InvalidOperationException(
                    $"XPath segment '{segment.TypeName}' is ambiguous (found {matching.Length}). "
                    + "Add a one-based '[n]' index to that XPath segment.");
            }

            current = matching[0];
        }

        return current;
    }

    private static string NormalizeXPath(string xpath)
    {
        var trimmed = (xpath ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "/Window";
        }

        if (trimmed == "/")
        {
            return "/Window";
        }

        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            trimmed = "/Window/" + trimmed;
        }

        while (trimmed.EndsWith("/", StringComparison.Ordinal) && trimmed.Length > 1)
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Equals("/Window", StringComparison.OrdinalIgnoreCase))
        {
            return "/Window";
        }

        return trimmed;
    }

    public static Dispatcher ResolveObservationDispatcher(long? windowHandle)
    {
        if (windowHandle is long requestedHwnd && requestedHwnd != 0)
        {
            return ResolveHwndSource(requestedHwnd).Dispatcher;
        }

        return Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Application.Current.Dispatcher is not available. Is the target a WPF app?");
    }

    private static HwndSource ResolveHwndSource(long requestedHwnd)
    {
        var source = HwndSource.FromHwnd(new IntPtr(requestedHwnd));
        return source
            ?? throw AgentToolError.InvalidOperation(
                "wpf_window_not_found",
                $"wpf_window_not_found: HWND {requestedHwnd} is not owned by a WPF HwndSource.");
    }

    private static Window ResolveWindow(long? windowHandle)
    {
        if (windowHandle is long requestedHwnd && requestedHwnd != 0)
        {
            var source = ResolveHwndSource(requestedHwnd);
            if (!source.Dispatcher.CheckAccess())
            {
                throw AgentToolError.InvalidOperation(
                    "wpf_window_dispatcher_required",
                    "wpf_window_dispatcher_required");
            }

            return source.RootVisual as Window
                ?? throw AgentToolError.InvalidOperation(
                    "wpf_window_not_found",
                    $"wpf_window_not_found: HWND {requestedHwnd} does not have a WPF Window root.");
        }

        var application = Application.Current
            ?? throw new InvalidOperationException("Application.Current is null. Is the target a WPF application?");

        if (application.MainWindow is not null)
        {
            return application.MainWindow;
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsVisible)
            {
                return window;
            }
        }

        throw new InvalidOperationException("No WPF windows found to inspect.");
    }

    private static long GetWindowHandle(Window window) =>
        new WindowInteropHelper(window).Handle.ToInt64();

    private static Window? GetContainingWindow(DependencyObject element)
    {
        if (element is Window window)
        {
            return window;
        }

        if (element is FrameworkElement fe)
        {
            var directWindow = Window.GetWindow(fe);
            if (directWindow is not null)
            {
                return directWindow;
            }
        }

        DependencyObject? current = element;
        var safety = 0;
        while (current is not null && safety++ < 2048)
        {
            if (current is Window currentWindow)
            {
                return currentWindow;
            }

            if (current is FrameworkElement currentFe)
            {
                var currentFeWindow = Window.GetWindow(currentFe);
                if (currentFeWindow is not null)
                {
                    return currentFeWindow;
                }
            }

            DependencyObject? parent = null;
            try
            {
                if (current is Visual or Visual3D)
                {
                    parent = VisualTreeHelper.GetParent(current);
                }
            }
            catch
            {
                parent = null;
            }

            if (parent is null)
            {
                try
                {
                    parent = current switch
                    {
                        FrameworkContentElement fce => fce.Parent,
                        FrameworkElement frameworkElement => frameworkElement.Parent,
                        _ => LogicalTreeHelper.GetParent(current)
                    };
                }
                catch
                {
                    parent = null;
                }
            }

            current = parent;
        }

        return null;
    }

    private static bool IsVisibleWpf(DependencyObject element)
    {
        try
        {
            return element switch
            {
                UIElement ue => ue.IsVisible,
                _ => true
            };
        }
        catch
        {
            return false;
        }
    }

    private static string? GetVisibilityWpf(DependencyObject element)
    {
        try
        {
            return element switch
            {
                UIElement ue => ue.Visibility.ToString(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool? GetIsEnabledWpf(DependencyObject element)
    {
        try
        {
            return element switch
            {
                UIElement ue => ue.IsEnabled,
                ContentElement ce => ce.IsEnabled,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static ContractRect? GetBoundsWpf(DependencyObject element)
    {
        try
        {
            if (element is not UIElement ue)
            {
                return null;
            }

            var point = ue.PointToScreen(new Point(0, 0));
            var size = ue.RenderSize;
            var widthDip = size.Width;
            var heightDip = size.Height;

            // Fall back to ActualWidth/ActualHeight when available.
            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                widthDip = fe.ActualWidth;
                heightDip = fe.ActualHeight;
            }

            var dpiScaleX = 1d;
            var dpiScaleY = 1d;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(ue);
                dpiScaleX = dpi.DpiScaleX;
                dpiScaleY = dpi.DpiScaleY;
            }
            catch
            {
            }

            var width = widthDip * dpiScaleX;
            var height = heightDip * dpiScaleY;

            return new ContractRect(
                X: (int)Math.Round(point.X),
                Y: (int)Math.Round(point.Y),
                Width: (int)Math.Round(width),
                Height: (int)Math.Round(height));
        }
        catch
        {
            return null;
        }
    }

    private static ContractRect? TryGetClientBoundsScreen(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            if (!GetClientRect(hwnd, out var rect))
            {
                return null;
            }

            var clientTopLeft = new POINT(0, 0);
            if (!ClientToScreen(hwnd, ref clientTopLeft))
            {
                return null;
            }

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return null;
            }

            return new ContractRect(clientTopLeft.X, clientTopLeft.Y, rect.Width, rect.Height);
        }
        catch
        {
            return null;
        }
    }

    private static bool RectIntersects(ContractRect a, ContractRect b)
    {
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
        {
            return false;
        }

        var ax2 = (long)a.X + a.Width;
        var ay2 = (long)a.Y + a.Height;
        var bx2 = (long)b.X + b.Width;
        var by2 = (long)b.Y + b.Height;

        return a.X < bx2 && ax2 > b.X && a.Y < by2 && ay2 > b.Y;
    }

    private static bool IsInViewportWpf(DependencyObject element, ContractRect viewportBounds)
    {
        var bounds = GetBoundsWpf(element);
        return bounds is not null && RectIntersects(bounds, viewportBounds);
    }

    private static bool ShouldIncludeWpfElement(
        DependencyObject element,
        bool visibleOnly,
        bool includeOffViewport,
        ContractRect? viewportBounds)
    {
        if (!visibleOnly)
        {
            return true;
        }

        if (!IsVisibleWpf(element))
        {
            return false;
        }

        if (includeOffViewport || viewportBounds is null)
        {
            return true;
        }

        return IsInViewportWpf(element, viewportBounds);
    }

    private static bool? TryGetIsVisibleWpf(DependencyObject element)
    {
        try
        {
            return element is UIElement uiElement ? uiElement.IsVisible : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetIsOffscreenWpf(
        DependencyObject element,
        ContractRect? bounds,
        bool? isVisible)
    {
        if (isVisible == false)
        {
            return true;
        }

        if (bounds is null)
        {
            return null;
        }

        var window = GetContainingWindow(element);
        var viewportBounds = window is null ? null : TryGetClientBoundsScreen(window);
        return viewportBounds is null ? null : !RectIntersects(bounds, viewportBounds);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    private static string? GetName(DependencyObject target)
    {
        if (target is FrameworkElement fe && !string.IsNullOrWhiteSpace(fe.Name))
        {
            return fe.Name;
        }

        if (target is FrameworkContentElement fce && !string.IsNullOrWhiteSpace(fce.Name))
        {
            return fce.Name;
        }

        try
        {
            var automationName = AutomationProperties.GetName(target);
            return string.IsNullOrWhiteSpace(automationName) ? null : automationName;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetNameSearchValues(DependencyObject target)
    {
        return DistinctNonEmpty(GetNameSearchValuesCore(target));
    }

    private static IEnumerable<string?> GetNameSearchValuesCore(DependencyObject target)
    {
        yield return GetName(target);

        string? automationName = null;
        try
        {
            automationName = AutomationProperties.GetName(target);
        }
        catch
        {
        }
        yield return automationName;

        switch (target)
        {
            case TextBlock textBlock:
                yield return textBlock.Text;
                break;
            case TextBox textBox:
                yield return textBox.Text;
                break;
            case HeaderedContentControl headered:
                yield return headered.Header as string;
                yield return headered.Content as string;
                break;
            case ContentControl contentControl:
                yield return contentControl.Content as string;
                break;
        }

        var peer = TryCreateAutomationPeer(target);
        if (peer is not null)
        {
            string? peerName = null;
            try
            {
                peerName = peer.GetName();
            }
            catch
            {
            }
            yield return peerName;
        }
    }

    private static IEnumerable<string> DistinctNonEmpty(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static bool NameMatches(DependencyObject element, string expected, StringComparison comparison) =>
        GetNameSearchValues(element).Any(value => string.Equals(value, expected, comparison));

    private static bool NameContains(DependencyObject element, string expected) =>
        GetNameSearchValues(element).Any(value => value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);

    private static string? GetAutomationId(DependencyObject target)
    {
        try
        {
            var automationId = AutomationProperties.GetAutomationId(target);
            return string.IsNullOrWhiteSpace(automationId) ? null : automationId;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDataContextType(DependencyObject target)
    {
        try
        {
            var dataContext = target switch
            {
                FrameworkElement fe => fe.DataContext,
                FrameworkContentElement fce => fce.DataContext,
                _ => null
            };

            return dataContext?.GetType().FullName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInteractiveWpf(DependencyObject element, InteractiveMode mode)
    {
        var enabled = GetIsEnabledWpf(element);
        if (enabled is false)
        {
            return false;
        }

        if (mode == InteractiveMode.Patterns)
        {
            return IsInteractiveWpfByPatterns(element);
        }

        return IsInteractiveWpfByHeuristic(element);
    }

    private static bool IsInteractiveWpfByPatterns(DependencyObject element)
    {
        try
        {
            if (element is UIElement ue)
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(ue);
                if (peer is null)
                {
                    return false;
                }

                return peer.GetPattern(PatternInterface.Invoke) is not null
                       || peer.GetPattern(PatternInterface.Selection) is not null
                       || peer.GetPattern(PatternInterface.SelectionItem) is not null
                       || peer.GetPattern(PatternInterface.Toggle) is not null
                       || peer.GetPattern(PatternInterface.Value) is not null
                       || peer.GetPattern(PatternInterface.RangeValue) is not null
                       || peer.GetPattern(PatternInterface.Scroll) is not null
                       || peer.GetPattern(PatternInterface.ScrollItem) is not null;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsInteractiveWpfByHeuristic(DependencyObject element)
    {
        if (element is ButtonBase)
        {
            return true;
        }

        if (element is RangeBase)
        {
            return true;
        }

        if (element is ToggleButton)
        {
            return true;
        }

        if (element is TextBoxBase)
        {
            return true;
        }

        if (element is System.Windows.Controls.Primitives.Selector)
        {
            return true;
        }

        if (element is System.Windows.Controls.Control control)
        {
            return control.Focusable || control.IsTabStop;
        }

        return false;
    }

    private static bool IsQueryMatchWpf(DependencyObject element, FindElementsQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.TypeEquals))
        {
            var typeName = element.GetType().Name;
            var fullName = element.GetType().FullName;

            if (!string.Equals(typeName, query.TypeEquals, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullName, query.TypeEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.AutomationIdEquals))
        {
            var id = GetAutomationId(element);
            if (!string.Equals(id, query.AutomationIdEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.AutomationIdContains))
        {
            var id = GetAutomationId(element) ?? string.Empty;
            if (id.IndexOf(query.AutomationIdContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.NameEquals))
        {
            if (!NameMatches(element, query.NameEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.NameContains))
        {
            if (!NameContains(element, query.NameContains))
            {
                return false;
            }
        }

        return true;
    }

    private static ElementRef BuildElementRefWpf(
        string ownerId,
        DependencyObject element,
        string xpath,
        FindReturnFields returnFields,
        bool includeElementId = true)
    {
        var elementIdWpf = includeElementId ? ElementHandles.Register(ownerId, element) : null;

        if (returnFields == FindReturnFields.Standard)
        {
            var bounds = GetBoundsWpf(element);
            var isVisible = TryGetIsVisibleWpf(element);
            return new ElementRef(
                Type: element.GetType().Name,
                AutomationId: GetAutomationId(element),
                Name: GetName(element),
                XPath: xpath,
                ClassName: element.GetType().FullName,
                Bounds: bounds,
                ElementIdWpf: elementIdWpf)
            {
                IsVisible = isVisible,
                IsOffscreen = TryGetIsOffscreenWpf(element, bounds, isVisible)
            };
        }

        return new ElementRef(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ElementIdWpf: elementIdWpf);
    }

    public static GetVisualTreeResponse GetVisualTree(
        string ownerId,
        GetWpfVisualTreeRequestV2 request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var depth = request.Depth <= 0 ? 1 : request.Depth;
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 5000);

        var window = ResolveWindow(request.WindowHandle);
        var viewportBounds = request.VisibleOnly && !request.IncludeOffViewport ? TryGetClientBoundsScreen(window) : null;
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, request.VisibleOnly, cancellationToken);
        }

        var fieldSet = WpfTreeFieldSet.Resolve(request.Preset, request.Fields);
        var context = new WpfTreeBuildContext(
            treeService,
            fieldSet,
            maxNodes,
            request.VisibleOnly,
            request.IncludeOffViewport,
            viewportBounds,
            request.InteractiveOnly,
            request.InteractiveMode,
            cancellationToken);

        var rootNode = BuildWpfTreeNode(rootObject, rootXPath, depth, isRoot: true, context)
            ?? throw new InvalidOperationException("Failed to build WPF tree root.");

        return new GetVisualTreeResponse(
            BackendUsed: InspectionBackend.Wpf,
            Root: rootNode,
            ReturnedNodes: context.ReturnedNodes,
            ScannedNodes: context.ScannedNodes,
            Truncated: context.Truncated,
            TruncatedReason: context.TruncatedReason,
            Warnings: null)
        {
            WindowHandleUsed = GetWindowHandle(window)
        };
    }

    public static FindElementsResponse FindElements(
        string ownerId,
        FindElementsWpfRequest request,
        CancellationToken cancellationToken)
    {
        var query = request.Query;
        if (query is null ||
            (string.IsNullOrWhiteSpace(query.AutomationIdEquals) &&
             string.IsNullOrWhiteSpace(query.AutomationIdContains) &&
             string.IsNullOrWhiteSpace(query.NameEquals) &&
             string.IsNullOrWhiteSpace(query.NameContains) &&
             string.IsNullOrWhiteSpace(query.TypeEquals)))
        {
            throw new ArgumentException("find_elements requires a non-empty query.");
        }

        var maxResults = Math.Clamp(request.MaxResults, 1, 5000);
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);

        var window = ResolveWindow(request.WindowHandle);
        var viewportBounds = request.VisibleOnly && !request.IncludeOffViewport ? TryGetClientBoundsScreen(window) : null;
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, request.VisibleOnly, cancellationToken);
        }

        var matches = new List<ElementRef>();
        var discoveredMatches = 0;
        var scannedNodes = 0;
        var truncated = false;
        string? truncatedReason = null;

        var stack = new Stack<(DependencyObject Element, string XPath)>();
        stack.Push((rootObject, rootXPath));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                truncated = true;
                truncatedReason = "maxNodes";
                break;
            }

            var (current, currentXPath) = stack.Pop();
            scannedNodes++;

            if (!ReferenceEquals(current, rootObject) && !ShouldIncludeWpfElement(current, request.VisibleOnly, request.IncludeOffViewport, viewportBounds))
            {
                continue;
            }

            if (IsQueryMatchWpf(current, query) &&
                (!request.InteractiveOnly || IsInteractiveWpf(current, request.InteractiveMode)))
            {
                discoveredMatches++;
                if (matches.Count < maxResults)
                {
                    matches.Add(BuildElementRefWpf(
                        ownerId,
                        current,
                        currentXPath,
                        request.ReturnFields,
                        request.IncludeElementIds));
                }
                else
                {
                    truncated = true;
                    truncatedReason = "maxResults";
                    break;
                }
            }

            var rawChildren = GetChildrenWpf(current, treeService, request.VisibleOnly, request.IncludeOffViewport, viewportBounds);
            if (rawChildren.Length == 0)
            {
                continue;
            }

            var labels = rawChildren.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = rawChildren.Length - 1; i >= 0; i--)
            {
                var child = rawChildren[i];
                var label = labels[i];

                runningIndexByLabel.TryGetValue(label, out var currentIndex);
                currentIndex++;
                runningIndexByLabel[label] = currentIndex;

                var includeIndex = countsByLabel[label] > 1;
                var segment = label;

                // Note: we iterate backwards; adjust index to keep XPath stable.
                if (includeIndex)
                {
                    var oneBasedForwardIndex = countsByLabel[label] - currentIndex + 1;
                    segment = $"{label}[{oneBasedForwardIndex}]";
                }

                var childXPath = $"{currentXPath}/{segment}";
                stack.Push((child, childXPath));
            }
        }

        return new FindElementsResponse(
            BackendUsed: InspectionBackend.Wpf,
            Matches: matches,
            ReturnedMatches: matches.Count,
            ScannedNodes: scannedNodes,
            Truncated: truncated,
            TruncatedReason: truncatedReason,
            Warnings: null)
        {
            WindowHandleUsed = GetWindowHandle(window),
            DiscoveredMatches = discoveredMatches
        };
    }

    private static IEnumerable<(DependencyObject Element, string XPath)> EnumerateDescendantsWithXPath(
        DependencyObject root,
        string rootXPath,
        VisualTreeService treeService,
        bool visibleOnly,
        bool includeOffViewport,
        ContractRect? viewportBounds,
        int maxNodes,
        CancellationToken cancellationToken,
        Action? onNodeScanned = null)
    {
        var stack = new Stack<(DependencyObject Element, string XPath)>();
        stack.Push((root, rootXPath));

        var scannedNodes = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                throw new InvalidOperationException($"Search exceeded maxNodes={maxNodes}. Increase MaxNodes.");
            }

            var (current, currentXPath) = stack.Pop();
            scannedNodes++;
            onNodeScanned?.Invoke();

            if (!ReferenceEquals(current, root) && !ShouldIncludeWpfElement(current, visibleOnly, includeOffViewport, viewportBounds))
            {
                continue;
            }

            yield return (current, currentXPath);

            var rawChildren = GetChildrenWpf(current, treeService, visibleOnly, includeOffViewport, viewportBounds);
            if (rawChildren.Length == 0)
            {
                continue;
            }

            var labels = rawChildren.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = rawChildren.Length - 1; i >= 0; i--)
            {
                var child = rawChildren[i];
                var label = labels[i];

                runningIndexByLabel.TryGetValue(label, out var currentIndex);
                currentIndex++;
                runningIndexByLabel[label] = currentIndex;

                var includeIndex = countsByLabel[label] > 1;
                var segment = label;

                // Note: we iterate backwards; adjust index to keep XPath stable.
                if (includeIndex)
                {
                    var oneBasedForwardIndex = countsByLabel[label] - currentIndex + 1;
                    segment = $"{label}[{oneBasedForwardIndex}]";
                }

                var childXPath = $"{currentXPath}/{segment}";
                stack.Push((child, childXPath));
            }
        }
    }

    private static (DependencyObject Element, string XPath) SelectMatchForLocator(
        IReadOnlyList<(DependencyObject Element, string XPath)> matches,
        ElementLocator locator)
    {
        if (matches.Count == 0)
        {
            throw AgentToolError.InvalidOperation(
                "wpf_resolve:not_found",
                "wpf_resolve:not_found: Locator did not match any elements.");
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var ordered = OrderMatchesForLocator(matches, locator);

        if (locator.Index is int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            if (index >= ordered.Count)
            {
                throw AgentToolError.InvalidOperation(
                    "wpf_resolve:not_found",
                    $"wpf_resolve:not_found: Locator index {index} is out of range (found {ordered.Count}).");
            }

            return ordered[index];
        }

        if (locator.Strict)
        {
            throw AgentToolError.Mark(
                new WpfLocatorAmbiguousException(
                    $"Locator is ambiguous (found {matches.Count}). Provide 'index' to disambiguate.",
                    ordered),
                "wpf_resolve:ambiguous");
        }

        return ordered[0];
    }

    private static IReadOnlyList<(DependencyObject Element, string XPath)> OrderMatchesForLocator(
        IReadOnlyList<(DependencyObject Element, string XPath)> matches,
        ElementLocator locator)
    {
        if (matches.Count <= 1)
        {
            return matches;
        }

        var list = matches.ToList();
        list.Sort((a, b) =>
        {
            var cmp = locator.PreferVisible ? GetVisibleRank(a.Element).CompareTo(GetVisibleRank(b.Element)) : 0;
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = GetEnabledRank(a.Element).CompareTo(GetEnabledRank(b.Element));
            if (cmp != 0)
            {
                return cmp;
            }

            var ba = GetBoundsWpf(a.Element);
            var bb = GetBoundsWpf(b.Element);
            cmp = (ba?.Y ?? int.MaxValue).CompareTo(bb?.Y ?? int.MaxValue);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = (ba?.X ?? int.MaxValue).CompareTo(bb?.X ?? int.MaxValue);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(GetAutomationId(a.Element), GetAutomationId(b.Element), StringComparison.Ordinal);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(GetName(a.Element), GetName(b.Element), StringComparison.Ordinal);
            return cmp != 0
                ? cmp
                : string.Compare(a.XPath, b.XPath, StringComparison.Ordinal);
        });
        return list;
    }

    private static int GetVisibleRank(DependencyObject element)
    {
        try
        {
            return IsVisibleWpf(element) ? 0 : 1;
        }
        catch
        {
            return 2;
        }
    }

    private static int GetEnabledRank(DependencyObject element)
    {
        var enabled = GetIsEnabledWpf(element);
        return enabled switch
        {
            true => 0,
            false => 1,
            _ => 2
        };
    }

    private static (DependencyObject Element, string XPath) ResolveLocatorOrThrow(
        Window window,
        VisualTreeService treeService,
        DependencyObject rootObject,
        string rootXPath,
        ElementLocator locator,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (IsEmptyLocator(locator))
        {
            throw new ArgumentException(
                "Locator must specify at least one of: xpath, automationId, automationIdContains, name, nameContains, className, classNameContains, typeEquals, controlTypeEquals, index.");
        }

        if (!string.IsNullOrWhiteSpace(locator.XPath))
        {
            if (locator.Index is not null)
            {
                throw new ArgumentException("index cannot be used with xpath.", nameof(locator));
            }

            var normalized = NormalizeXPath(locator.XPath);
            try
            {
                var element = ResolveByXPath(treeService, window, normalized, visibleOnly, cancellationToken);
                var mismatch = DescribeXPathFilterMismatchWpf(element, locator);
                if (mismatch is not null)
                {
                    throw new InvalidOperationException(mismatch);
                }

                if (interactiveOnly && !IsInteractiveWpf(element, interactiveMode))
                {
                    throw new InvalidOperationException("Locator did not match any element.");
                }

                if (visibleOnly && !includeOffViewport)
                {
                    var viewportBounds = TryGetClientBoundsScreen(window);
                    if (viewportBounds is not null && !IsInViewportWpf(element, viewportBounds))
                    {
                        throw new InvalidOperationException(
                            "Element is outside the current viewport (visibleOnly=true). Retry with includeOffViewport=true or call scroll_to_element.");
                    }
                }
                return (element, normalized);
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
                {
                    throw AgentToolError.InvalidOperation(
                        "wpf_resolve:ambiguous",
                        $"wpf_resolve:ambiguous: {ex.Message}");
                }

                throw AgentToolError.InvalidOperation(
                    "wpf_resolve:not_found",
                    $"wpf_resolve:not_found: {ex.Message}");
            }
        }

        var viewport = visibleOnly && !includeOffViewport ? TryGetClientBoundsScreen(window) : null;
        var descendants = EnumerateDescendantsWithXPath(rootObject, rootXPath, treeService, visibleOnly, includeOffViewport, viewport, maxNodes, cancellationToken)
            .Skip(1)
            .Where(e => !interactiveOnly || IsInteractiveWpf(e.Element, interactiveMode))
            .ToArray();

        if (IsIndexOnlyLocator(locator))
        {
            var index = locator.Index!.Value;
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            if (index >= descendants.Length)
            {
                throw AgentToolError.InvalidOperation(
                    "wpf_resolve:not_found",
                    $"wpf_resolve:not_found: Locator index {index} is out of range (found {descendants.Length}).");
            }

            return descendants[index];
        }

        var matches = descendants
            .Where(e => MatchesLocatorWpf(e.Element, locator))
            .ToArray();

        return SelectMatchForLocator(matches, locator);
    }

    private static (DependencyObject Element, string XPath) ResolveTargetElement(
        string ownerId,
        Window window,
        VisualTreeService treeService,
        DependencyObject rootObject,
        string rootXPath,
        ElementLocator? locator,
        string? elementId,
        long? windowHandle,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        int maxNodes,
        CancellationToken cancellationToken,
        string targetName = "elementId|locator")
    {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                $"invalid_request: provide exactly one of {targetName}.");
        }

        if (hasElementId)
        {
            if (windowHandle is not long hwnd || hwnd == 0)
            {
                throw AgentToolError.InvalidArgument(
                    "invalid_request",
                    "invalid_request: windowHandle is required with elementId.");
            }

            var element = ElementHandles.Resolve(ownerId, hwnd, elementId!.Trim());
            var chain = BuildXPathChainForElement(
                treeService,
                window,
                element,
                visibleOnly: false,
                maxNodes,
                cancellationToken);

            var resolved = chain.Count > 0 && ReferenceEquals(chain[^1].Element, element)
                ? chain[^1]
                : throw AgentToolError.InvalidOperation(
                    "wpf_handle_stale",
                    $"wpf_handle_stale:detached: '{elementId!.Trim()}'.");

            if (visibleOnly && !ShouldIncludeWpfElement(resolved.Element, visibleOnly, includeOffViewport, TryGetClientBoundsScreen(window)))
            {
                throw AgentToolError.InvalidOperation(
                    "wpf_handle_stale",
                    "wpf_handle_stale:not_visible: element is not visible under the requested filters.");
            }

            return resolved;
        }

        return ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject,
            rootXPath,
            locator!,
            visibleOnly,
            includeOffViewport,
            interactiveOnly,
            interactiveMode,
            maxNodes,
            cancellationToken);
    }

    private static bool IsIndexOnlyLocator(ElementLocator locator)
    {
        return locator.Index is not null
               && string.IsNullOrWhiteSpace(locator.AutomationId)
               && string.IsNullOrWhiteSpace(locator.AutomationIdContains)
               && string.IsNullOrWhiteSpace(locator.Name)
               && string.IsNullOrWhiteSpace(locator.NameContains)
               && string.IsNullOrWhiteSpace(locator.ClassName)
               && string.IsNullOrWhiteSpace(locator.ClassNameContains)
               && string.IsNullOrWhiteSpace(locator.TypeEquals)
               && string.IsNullOrWhiteSpace(locator.ControlTypeEquals)
               && string.IsNullOrWhiteSpace(locator.XPath);
    }

    private static bool IsEmptyLocator(ElementLocator locator)
    {
        return string.IsNullOrWhiteSpace(locator.AutomationId)
               && string.IsNullOrWhiteSpace(locator.AutomationIdContains)
               && string.IsNullOrWhiteSpace(locator.Name)
               && string.IsNullOrWhiteSpace(locator.NameContains)
               && string.IsNullOrWhiteSpace(locator.ClassName)
               && string.IsNullOrWhiteSpace(locator.ClassNameContains)
               && string.IsNullOrWhiteSpace(locator.TypeEquals)
               && string.IsNullOrWhiteSpace(locator.ControlTypeEquals)
               && string.IsNullOrWhiteSpace(locator.XPath)
               && locator.Index is null;
    }

    private static string? DescribeXPathFilterMismatchWpf(DependencyObject element, ElementLocator locator)
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            var actual = GetAutomationId(element);
            if (!string.Equals(actual, locator.AutomationId, StringComparison.Ordinal))
            {
                errors.Add($"automationId expected '{locator.AutomationId}' actual '{actual ?? ""}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.AutomationIdContains))
        {
            var expected = locator.AutomationIdContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetAutomationId(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    errors.Add($"automationIdContains expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.Name))
        {
            var actual = GetName(element);
            if (!string.Equals(actual, locator.Name, StringComparison.Ordinal))
            {
                errors.Add($"name expected '{locator.Name}' actual '{actual ?? ""}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.NameContains))
        {
            var expected = locator.NameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetName(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    errors.Add($"nameContains expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName))
        {
            var actual = element.GetType().FullName;
            if (!string.Equals(actual, locator.ClassName, StringComparison.Ordinal))
            {
                errors.Add($"className expected '{locator.ClassName}' actual '{actual ?? ""}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassNameContains))
        {
            var expected = locator.ClassNameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = element.GetType().FullName ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    errors.Add($"classNameContains expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ControlTypeEquals))
        {
            var expected = locator.ControlTypeEquals.Trim();
            if (expected.Length > 0)
            {
                var actual = element.GetType().Name;
                var fullName = element.GetType().FullName;
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"controlTypeEquals expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.TypeEquals))
        {
            var expected = locator.TypeEquals.Trim();
            if (expected.Length > 0)
            {
                var actual = element.GetType().Name;
                var fullName = element.GetType().FullName;
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"typeEquals expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (errors.Count == 0)
        {
            return null;
        }

        return $"wpf_resolve:xpath_resolved_but_filters_mismatch: {string.Join("; ", errors)}";
    }

    private static bool MatchesLocatorWpf(DependencyObject element, ElementLocator locator)
    {
        if (!string.IsNullOrWhiteSpace(locator.AutomationId) &&
            !string.Equals(GetAutomationId(element), locator.AutomationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.AutomationIdContains))
        {
            var expected = locator.AutomationIdContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetAutomationId(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.Name) &&
            !NameMatches(element, locator.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.NameContains))
        {
            var expected = locator.NameContains.Trim();
            if (expected.Length > 0)
            {
                if (!NameContains(element, expected))
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName) &&
            !string.Equals(element.GetType().FullName, locator.ClassName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassNameContains))
        {
            var expected = locator.ClassNameContains.Trim();
            if (expected.Length > 0)
            {
                var fullName = element.GetType().FullName ?? "";
                if (fullName.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ControlTypeEquals))
        {
            var expected = locator.ControlTypeEquals.Trim();
            if (expected.Length > 0)
            {
                var typeName = element.GetType().Name;
                var fullName = element.GetType().FullName;
                if (!string.Equals(typeName, expected, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.TypeEquals))
        {
            var expected = locator.TypeEquals.Trim();
            if (expected.Length > 0)
            {
                var typeName = element.GetType().Name;
                var fullName = element.GetType().FullName;
                if (!string.Equals(typeName, expected, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static GetPathToElementResponse GetPath(
        string ownerId,
        GetWpfPathRequest request,
        CancellationToken cancellationToken)
    {
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, request.VisibleOnly, cancellationToken);
        }

        var resolved = ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject,
            rootXPath,
            request.Locator,
            request.ElementId,
            request.WindowHandle,
            request.VisibleOnly,
            request.IncludeOffViewport,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);
        return new GetPathToElementResponse(InspectionBackend.Wpf, resolved.XPath)
        {
            WindowHandleUsed = GetWindowHandle(window)
        };
    }

    public static ElementRef ResolveElement(
        string ownerId,
        ResolveWpfElementRequest request,
        CancellationToken cancellationToken)
    {
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        return ResolveElementOrThrow(ownerId, request, window, treeService, maxNodes, cancellationToken);
    }

    public static ResolveWpfElementDetailedResponse ResolveElementDetailed(
        string ownerId,
        ResolveWpfElementRequest request,
        CancellationToken cancellationToken)
    {
        const int maxCandidates = 5;

        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
        var window = ResolveWindow(request.WindowHandle);
        var resolvedWindowHandle = new WindowInteropHelper(window).Handle.ToInt64();
        var windowHandleUsed = resolvedWindowHandle != 0
            ? resolvedWindowHandle
            : request.WindowHandle.GetValueOrDefault();
        using var treeService = new VisualTreeService();

        try
        {
            var element = ResolveElementOrThrow(ownerId, request, window, treeService, maxNodes, cancellationToken);
            return new ResolveWpfElementDetailedResponse(Element: element, Ambiguity: null);
        }
        catch (WpfLocatorAmbiguousException ex)
        {
            var candidates = ex.OrderedCandidates
                .Take(maxCandidates)
                .Select((candidate, index) => new ResolveElementCandidate(
                    Index: index,
                    Element: BuildElementRefWpf(
                        ownerId,
                        candidate.Element,
                        candidate.XPath,
                        FindReturnFields.Standard)))
                .ToArray();
            var truncated = candidates.Length < ex.OrderedCandidates.Count;

            return new ResolveWpfElementDetailedResponse(
                Element: null,
                Ambiguity: new ResolveElementAmbiguity(
                    Code: "ambiguous_element",
                    BackendUsed: InspectionBackend.Wpf,
                    WindowHandleUsed: windowHandleUsed,
                    ReturnedCandidates: candidates.Length,
                    DiscoveredCandidates: ex.OrderedCandidates.Count,
                    Truncated: truncated,
                    Candidates: candidates,
                    TruncatedReason: truncated ? "maxCandidates" : null));
        }
    }

    private static ElementRef ResolveElementOrThrow(
        string ownerId,
        ResolveWpfElementRequest request,
        Window window,
        VisualTreeService treeService,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, request.VisibleOnly, cancellationToken);
        }

        var resolved = ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject,
            rootXPath,
            request.Locator,
            request.ElementId,
            request.WindowHandle,
            request.VisibleOnly,
            request.IncludeOffViewport,
            request.InteractiveOnly,
            request.InteractiveMode,
            maxNodes,
            cancellationToken);

        return BuildElementRefWpf(ownerId, resolved.Element, resolved.XPath, request.ReturnFields);
    }

    public static SetValueResponse SetValue(
        string ownerId,
        SetWpfValueRequest request,
        CancellationToken cancellationToken)
    {
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
        var hasText = request.Text is not null;
        var hasNumericValue = request.Value.HasValue;
        if (hasText == hasNumericValue)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: set_value requires exactly one of text OR value.");
        }

        if (hasNumericValue && request.TextMode != TextEntryMode.Replace)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: set_value textMode is only valid with text input.");
        }

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
            request.VisibleOnly,
            request.IncludeOffViewport,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var element = resolved.Element;
        if (GetIsEnabledWpf(element) is false)
        {
            throw AgentToolError.InvalidOperation(
                "element_disabled",
                $"element_disabled: set_value target WPF type '{element.GetType().Name}' is disabled.");
        }

        var textValue = hasText
            ? request.Text!
            : request.Value!.Value.ToString(CultureInfo.InvariantCulture);

        if (TrySetWpfTextTargetValue(element, textValue, hasText, request.TextMode) is { } textResponse)
        {
            return textResponse;
        }

        switch (element)
        {
            case RangeBase rangeBase when hasNumericValue:
                var value = request.Value!.Value;
                if (value < rangeBase.Minimum || value > rangeBase.Maximum)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        $"value {value.ToString(CultureInfo.InvariantCulture)} is outside range [{rangeBase.Minimum.ToString(CultureInfo.InvariantCulture)}, {rangeBase.Maximum.ToString(CultureInfo.InvariantCulture)}] for WPF type '{element.GetType().Name}'.");
                }

                rangeBase.Value = value;
                return new SetValueResponse(Set: true, MethodUsed: "wpf_rangeBaseValue");
        }

        var supported = hasText
            ? "TextBox.Text, PasswordBox.Password, or editable ComboBox.Text"
            : "TextBox.Text, PasswordBox.Password, editable ComboBox.Text, or RangeBase.Value";
        var modeDescription = hasText && request.TextMode != TextEntryMode.Replace
            ? $" for text mode '{request.TextMode}'"
            : string.Empty;
        throw AgentToolError.InvalidOperation(
            "set_value_unsupported_wpf_target",
            $"set_value_unsupported_wpf_target: WPF type '{element.GetType().Name}' does not expose a supported value target for this input{modeDescription}. Supported WPF targets: {supported}.");
    }

    internal static SetValueResponse? TrySetWpfTextValue(
        DependencyObject element,
        string textValue,
        TextEntryMode textMode)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(textValue);

        return element switch
        {
            TextBox textBox => SetTextBoxValue(textBox, textValue, textMode),
            PasswordBox passwordBox => SetPasswordBoxValue(passwordBox, textValue, textMode),
            ComboBox { IsEditable: true } comboBox => SetEditableComboBoxValue(comboBox, textValue, textMode),
            _ => null
        };
    }

    internal static SetValueResponse? TrySetWpfTextTargetValue(
        DependencyObject element,
        string textValue,
        bool isTextInput,
        TextEntryMode requestedTextMode) =>
        TrySetWpfTextValue(
            element,
            textValue,
            isTextInput ? requestedTextMode : TextEntryMode.Replace);

    private static SetValueResponse SetTextBoxValue(
        TextBox textBox,
        string textValue,
        TextEntryMode textMode)
    {
        switch (textMode)
        {
            case TextEntryMode.Replace:
                textBox.Text = textValue;
                return new SetValueResponse(Set: true, MethodUsed: "wpf_textBoxText");
            case TextEntryMode.Append:
                textBox.AppendText(textValue);
                return new SetValueResponse(Set: true, MethodUsed: "wpf_textBoxAppendText");
            case TextEntryMode.AtSelection:
                ReplaceTextBoxSelection(textBox, textValue);
                return new SetValueResponse(Set: true, MethodUsed: "wpf_textBoxSelectedText");
            default:
                throw new ArgumentOutOfRangeException(nameof(textMode), textMode, "Unknown text entry mode.");
        }
    }

    private static SetValueResponse? SetPasswordBoxValue(
        PasswordBox passwordBox,
        string textValue,
        TextEntryMode textMode)
    {
        switch (textMode)
        {
            case TextEntryMode.Replace:
                passwordBox.Password = textValue;
                return new SetValueResponse(Set: true, MethodUsed: "wpf_passwordBoxPassword");
            case TextEntryMode.Append:
                passwordBox.Password += textValue;
                return new SetValueResponse(Set: true, MethodUsed: "wpf_passwordBoxPasswordAppend");
            case TextEntryMode.AtSelection:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(textMode), textMode, "Unknown text entry mode.");
        }
    }

    private static SetValueResponse? SetEditableComboBoxValue(
        ComboBox comboBox,
        string textValue,
        TextEntryMode textMode)
    {
        switch (textMode)
        {
            case TextEntryMode.Replace:
                comboBox.Text = textValue;
                return new SetValueResponse(Set: true, MethodUsed: "wpf_comboBoxText");
            case TextEntryMode.Append:
                comboBox.Text += textValue;
                return new SetValueResponse(Set: true, MethodUsed: "wpf_comboBoxTextAppend");
            case TextEntryMode.AtSelection:
                _ = comboBox.ApplyTemplate();
                if (comboBox.Template?.FindName("PART_EditableTextBox", comboBox) is not TextBox editableTextBox)
                {
                    return null;
                }

                ReplaceTextBoxSelection(editableTextBox, textValue);
                return new SetValueResponse(Set: true, MethodUsed: "wpf_comboBoxSelectedText");
            default:
                throw new ArgumentOutOfRangeException(nameof(textMode), textMode, "Unknown text entry mode.");
        }
    }

    private static void ReplaceTextBoxSelection(TextBox textBox, string textValue)
    {
        var selectionStart = textBox.SelectionStart;
        textBox.SelectedText = textValue;
        textBox.Select(selectionStart + textValue.Length, 0);
    }

    public static FocusWpfElementResponse FocusElement(
        string ownerId,
        FocusWpfElementRequest request,
        CancellationToken cancellationToken)
    {
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
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
            request.VisibleOnly,
            request.IncludeOffViewport,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var element = resolved.Element;
        if (GetIsEnabledWpf(element) is false)
        {
            throw AgentToolError.InvalidOperation(
                "element_disabled",
                $"element_disabled: focus target WPF type '{element.GetType().Name}' is disabled.");
        }

        return FocusWpfElement(element);
    }

    internal static FocusWpfElementResponse FocusWpfElement(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is not IInputElement inputElement || !IsKeyboardFocusableWpf(element))
        {
            throw AgentToolError.InvalidOperation(
                "focus_unsupported_wpf_target",
                $"focus_unsupported_wpf_target: WPF type '{element.GetType().Name}' is not keyboard focusable.");
        }

        var focusedBefore = Keyboard.FocusedElement;
        try
        {
            _ = Keyboard.Focus(inputElement);
        }
        catch (Exception ex)
        {
            throw AgentToolError.InvalidOperation(
                "focus_failed_wpf_target",
                $"focus_failed_wpf_target: WPF type '{element.GetType().Name}' rejected keyboard focus.",
                ex);
        }

        var focusedAfter = Keyboard.FocusedElement;
        if (!ReferenceEquals(focusedAfter, inputElement) && !HasKeyboardFocusWithinWpf(element))
        {
            throw AgentToolError.InvalidOperation(
                "focus_failed_wpf_target",
                $"focus_failed_wpf_target: WPF type '{element.GetType().Name}' did not receive keyboard focus.");
        }

        return new FocusWpfElementResponse(
            Focused: true,
            KeyboardFocusChanged: !ReferenceEquals(focusedBefore, focusedAfter),
            MethodUsed: "wpf_keyboardFocus");
    }

    private static bool IsKeyboardFocusableWpf(DependencyObject element) => element switch
    {
        UIElement uiElement => uiElement.Focusable,
        ContentElement contentElement => contentElement.Focusable,
        UIElement3D uiElement3D => uiElement3D.Focusable,
        _ => false
    };

    private static bool HasKeyboardFocusWithinWpf(DependencyObject element) => element switch
    {
        UIElement uiElement => uiElement.IsKeyboardFocusWithin,
        ContentElement contentElement => contentElement.IsKeyboardFocusWithin,
        UIElement3D uiElement3D => uiElement3D.IsKeyboardFocusWithin,
        _ => false
    };

    public static InvokeResponse Invoke(
        string ownerId,
        InvokeWpfRequest request,
        CancellationToken cancellationToken)
    {
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
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
            request.VisibleOnly,
            request.IncludeOffViewport,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var element = resolved.Element;
        if (GetIsEnabledWpf(element) is false)
        {
            throw AgentToolError.InvalidOperation(
                "element_disabled",
                $"element_disabled: invoke target WPF type '{element.GetType().Name}' is disabled.");
        }

        var peer = TryCreateAutomationPeer(element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invokeProvider)
        {
            invokeProvider.Invoke();
            return new InvokeResponse(Invoked: true, MethodUsed: "wpf_invoke");
        }

        if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggleProvider)
        {
            toggleProvider.Toggle();
            return new InvokeResponse(Invoked: true, MethodUsed: "wpf_toggle");
        }

        if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selectionItemProvider)
        {
            selectionItemProvider.Select();
            return new InvokeResponse(Invoked: true, MethodUsed: "wpf_selectionItem");
        }

        throw AgentToolError.InvalidOperation(
            "invoke_unsupported_wpf_target",
            $"invoke_unsupported_wpf_target: WPF type '{element.GetType().Name}' does not expose Invoke, Toggle, or SelectionItem automation patterns.");
    }

    public static BringIntoViewWpfResponse BringIntoView(
        string ownerId,
        BringIntoViewWpfRequest request,
        CancellationToken cancellationToken)
    {
        if (request.WindowHandle == 0)
        {
            throw new ArgumentException("WindowHandle is required.");
        }

        var hasXPath = !string.IsNullOrWhiteSpace(request.XPath);
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasXPath == hasElementId)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: provide exactly one of elementId|xpath.");
        }

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        DependencyObject element;
        try
        {
            element = hasElementId
                ? ElementHandles.Resolve(ownerId, request.WindowHandle, request.ElementId!.Trim())
                : ResolveByXPath(treeService, window, NormalizeXPath(request.XPath!), visibleOnly: false, cancellationToken);
        }
        catch (Exception ex)
        {
            return new BringIntoViewWpfResponse(
                BroughtIntoView: false,
                Bounds: null,
                Reason: "not_found: " + ex.Message);
        }

        try
        {
            switch (element)
            {
                case FrameworkElement fe:
                    fe.BringIntoView();
                    break;
                case FrameworkContentElement fce:
                    fce.BringIntoView();
                    break;
                default:
                    return new BringIntoViewWpfResponse(
                        BroughtIntoView: false,
                        Bounds: GetBoundsWpf(element),
                        Reason: "not_supported");
            }

            try
            {
                window.UpdateLayout();
            }
            catch
            {
            }

            return new BringIntoViewWpfResponse(
                BroughtIntoView: true,
                Bounds: GetBoundsWpf(element));
        }
        catch (Exception ex)
        {
            return new BringIntoViewWpfResponse(
                BroughtIntoView: false,
                Bounds: GetBoundsWpf(element),
                Reason: ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static ReleaseElementResponse ReleaseElement(string ownerId, ReleaseWpfElementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ReleaseElementResponse(ElementHandles.Release(ownerId, request.ElementId));
    }

    public static void ReleaseOwnerResources(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ElementHandles.ReleaseOwner(ownerId);
        ClearHighlight(ownerId);
    }

    public static HighlightWpfElementResponse HighlightElement(
        string ownerId,
        HighlightWpfElementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var maxNodes = 2000;

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, visibleOnly: true, cancellationToken);
        }

        var resolved = ResolveTargetElement(
            ownerId,
            window,
            treeService,
            rootObject,
            rootXPath,
            request.Locator,
            request.ElementId,
            request.WindowHandle,
            visibleOnly: true,
            includeOffViewport: false,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        ClearHighlight();

        var thickness = Math.Clamp(request.Thickness, 1, 20);
        var durationMs = Math.Clamp(request.DurationMs, 1, 60_000);

        if (!TryParseSolidColorBrush(request.Color, out var borderBrush))
        {
            return new HighlightWpfElementResponse(Highlighted: false, Reason: "invalid_color");
        }

        var options = SelectionHighlightOptions.Default;

        _savedHighlightBorderBrush = options.BorderBrush;
        _savedHighlightBorderThickness = options.BorderThickness;
        _savedHighlightEnabled = options.HighlightSelectedItem;

        options.HighlightSelectedItem = true;
        options.BorderThickness = thickness;
        options.BorderBrush = borderBrush;

        var highlight = SelectionHighlightFactory.CreateAndAttachSelectionHighlight(resolved.Element);
        if (highlight is null)
        {
            RestoreHighlightOptions();
            return new HighlightWpfElementResponse(Highlighted: false, Reason: "not_supported");
        }

        _activeHighlightOwnerId = ownerId;
        _activeHighlight = highlight;

        _highlightTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(durationMs)
        };
        _highlightTimer.Tick += (_, _) => ClearHighlight(ownerId);
        _highlightTimer.Start();

        return new HighlightWpfElementResponse(Highlighted: true);
    }

    private static void ClearHighlight(string? ownerId = null)
    {
        if (ownerId is not null &&
            !string.Equals(_activeHighlightOwnerId, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (_highlightTimer is not null)
            {
                _highlightTimer.Stop();
                _highlightTimer = null;
            }
        }
        catch
        {
        }

        try
        {
            _activeHighlight?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _activeHighlightOwnerId = null;
            _activeHighlight = null;
        }

        RestoreHighlightOptions();
    }

    private static void RestoreHighlightOptions()
    {
        var thickness = _savedHighlightBorderThickness;
        var enabled = _savedHighlightEnabled;
        var brush = _savedHighlightBorderBrush;

        try
        {
            if (thickness is null || enabled is null)
            {
                return;
            }

            var options = SelectionHighlightOptions.Default;
            try
            {
                options.BorderThickness = thickness.Value;
                options.HighlightSelectedItem = enabled.Value;
            }
            catch
            {
            }

            try
            {
                options.BorderBrush = brush!;
            }
            catch
            {
            }
        }
        catch
        {
        }
        finally
        {
            _savedHighlightBorderBrush = null;
            _savedHighlightBorderThickness = null;
            _savedHighlightEnabled = null;
        }
    }

    private static bool TryParseSolidColorBrush(string? value, out SolidColorBrush brush)
    {
        brush = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var converted = ColorConverter.ConvertFromString(value.Trim());
            if (converted is not Color color)
            {
                return false;
            }

            brush = new SolidColorBrush(color);
            brush.Freeze();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static PickWpfElementAtPointResponse PickElementAtPoint(
        string ownerId,
        PickWpfElementAtPointRequest request,
        CancellationToken cancellationToken)
    {
        var maxAncestors = Math.Clamp(request.MaxAncestors, 0, 50);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var hit = PickWpfDependencyObjectAtPoint(window, request.X, request.Y);
        hit = PromotePickedWpfElement(hit, window);
        if (ReferenceEquals(hit, window) && !request.ReturnRootOnMiss)
        {
            throw AgentToolError.InvalidOperation(
                "no_hit_at_point",
                $"no_hit_at_point: x={request.X} y={request.Y}.");
        }

        var chain = BuildXPathChainForElement(treeService, window, hit, visibleOnly: true, maxNodes: 200_000, cancellationToken);
        var (pickedElement, pickedXPath) = chain[^1];

        var elementRef = BuildElementRefWpf(ownerId, pickedElement, pickedXPath, request.ReturnFields);

        IReadOnlyList<ElementRef>? ancestors = null;
        if (request.IncludeAncestors)
        {
            var candidateAncestors = chain.Take(Math.Max(0, chain.Count - 1)).ToArray();
            if (maxAncestors > 0 && candidateAncestors.Length > maxAncestors)
            {
                candidateAncestors = candidateAncestors[^maxAncestors..];
            }

            ancestors = candidateAncestors
                .Select(a => BuildElementRefWpf(ownerId, a.Element, a.XPath, FindReturnFields.Minimal))
                .ToArray();
        }

        return new PickWpfElementAtPointResponse(elementRef, ancestors);
    }

    private static DependencyObject PickWpfDependencyObjectAtPoint(Window window, int x, int y)
    {
        var screenPoint = new Point(x, y);
        Point clientPoint;
        try
        {
            clientPoint = window.PointFromScreen(screenPoint);
        }
        catch
        {
            return window;
        }

        try
        {
            var inputHit = window.InputHitTest(clientPoint);
            if (inputHit is DependencyObject dependencyObject)
            {
                return dependencyObject;
            }
        }
        catch
        {
        }

        try
        {
            var hit = VisualTreeHelper.HitTest(window, clientPoint);
            if (hit?.VisualHit is DependencyObject visualHit)
            {
                return visualHit;
            }
        }
        catch
        {
        }

        return window;
    }

    private static DependencyObject PromotePickedWpfElement(DependencyObject element, Window window)
    {
        if (element is FrameworkElement or Window)
        {
            return element;
        }

        // InputHitTest can return content elements (e.g., Hyperlink / Run). Promote to a stable FrameworkElement.
        if (element is ContentElement)
        {
            DependencyObject? current = element;
            var safety = 0;

            while (current is not null && current is not FrameworkElement && current is not Window)
            {
                if (safety++ > 2048)
                {
                    break;
                }

                DependencyObject? parent = current switch
                {
                    FrameworkContentElement fce => fce.Parent,
                    _ => LogicalTreeHelper.GetParent(current)
                };

                if (parent is null)
                {
                    break;
                }

                current = parent;
            }

            return current is FrameworkElement or Window ? current : window;
        }

        DependencyObject? visualCurrent = element;
        var visualSafety = 0;

        while (visualCurrent is not null && visualCurrent is not FrameworkElement && visualCurrent is not Window)
        {
            if (visualSafety++ > 2048)
            {
                break;
            }

            DependencyObject? parent;
            try
            {
                parent = VisualTreeHelper.GetParent(visualCurrent);
            }
            catch
            {
                parent = null;
            }

            if (parent is null)
            {
                break;
            }

            visualCurrent = parent;
        }

        return visualCurrent is FrameworkElement or Window ? visualCurrent : window;
    }

    private static List<(DependencyObject Element, string XPath)> BuildXPathChainForElement(
        VisualTreeService treeService,
        Window window,
        DependencyObject element,
        bool visibleOnly,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (ReferenceEquals(element, window))
        {
            return [(window, "/Window")];
        }

        if (TryBuildXPathChainUpwards(treeService, window, element, visibleOnly, out var chain))
        {
            return chain;
        }

        var root = (DependencyObject)window;
        var rootXPath = "/Window";

        string? xpath = null;
        foreach (var item in EnumerateDescendantsWithXPath(root, rootXPath, treeService, visibleOnly, includeOffViewport: true, viewportBounds: null, maxNodes, cancellationToken))
        {
            if (ReferenceEquals(item.Element, element))
            {
                xpath = item.XPath;
                break;
            }
        }

        if (xpath is null)
        {
            return [(window, "/Window")];
        }

        var segments = xpath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        var chainFromRoot = new List<(DependencyObject Element, string XPath)> { (window, "/Window") };
        var currentPath = "/Window";

        for (var i = 1; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentPath += "/" + segments[i];
            var resolved = ResolveByXPath(treeService, window, currentPath, visibleOnly, cancellationToken);
            chainFromRoot.Add((resolved, currentPath));
        }

        return chainFromRoot;
    }

    private static bool TryBuildXPathChainUpwards(
        VisualTreeService treeService,
        Window window,
        DependencyObject element,
        bool visibleOnly,
        out List<(DependencyObject Element, string XPath)> chain)
    {
        chain = [];

        var segmentsLeafFirst = new List<string>();
        var elementsLeafFirst = new List<DependencyObject>();
        DependencyObject? current = element;

        var safety = 0;
        while (current is not null && !ReferenceEquals(current, window))
        {
            if (safety++ > 2048)
            {
                return false;
            }

            var parent = VisualTreeHelper.GetParent(current);
            if (parent is null)
            {
                return false;
            }

            var rawChildren = GetChildrenWpf(parent, treeService, visibleOnly, includeOffViewport: true, viewportBounds: null);
            var label = GetXPathLabel(current);

            var matching = 0;
            var index = 0;
            var found = false;

            foreach (var child in rawChildren)
            {
                if (!string.Equals(GetXPathLabel(child), label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matching++;
                if (ReferenceEquals(child, current))
                {
                    index = matching;
                    found = true;
                }
            }

            if (!found || matching <= 0)
            {
                return false;
            }

            var segment = matching > 1 ? $"{label}[{index}]" : label;
            segmentsLeafFirst.Add(segment);
            elementsLeafFirst.Add(current);
            current = parent;
        }

        segmentsLeafFirst.Reverse();
        elementsLeafFirst.Reverse();

        chain = new List<(DependencyObject Element, string XPath)>(elementsLeafFirst.Count + 1)
        {
            (window, "/Window")
        };

        var xpath = "/Window";
        for (var i = 0; i < segmentsLeafFirst.Count; i++)
        {
            xpath += "/" + segmentsLeafFirst[i];
            chain.Add((elementsLeafFirst[i], xpath));
        }

        return true;
    }

    private static string DescribeBindingSource(Binding binding)
    {
        if (binding.Source is not null)
        {
            return binding.Source.GetType().FullName ?? binding.Source.GetType().Name;
        }

        if (!string.IsNullOrWhiteSpace(binding.ElementName))
        {
            return $"ElementName={binding.ElementName}";
        }

        if (binding.RelativeSource is not null)
        {
            return $"RelativeSource={binding.RelativeSource.Mode}";
        }

        return "DataContext";
    }

    private static string TruncateString(string value, int maxLength)
    {
        return TruncateString(value, maxLength, out _);
    }

    private static string TruncateString(string value, int maxLength, out bool truncated)
    {
        maxLength = Math.Max(0, maxLength);
        truncated = value.Length > maxLength;
        if (!truncated)
        {
            return value;
        }

        if (maxLength == 0)
        {
            return string.Empty;
        }

        if (maxLength <= 3)
        {
            return new string('.', maxLength);
        }

        var contentLength = maxLength - 3;
        if (contentLength > 0 && char.IsHighSurrogate(value[contentLength - 1]))
        {
            contentLength--;
        }

        return value[..contentLength] + "...";
    }

    private static string FormatValueForBinding(object? value, string valueFormat, int maxStringLength)
    {
        if (value is null)
        {
            return "null";
        }

        if (ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return "{UnsetValue}";
        }

        if (string.Equals(valueFormat, "type", StringComparison.OrdinalIgnoreCase))
        {
            return value.GetType().FullName ?? value.GetType().Name;
        }

        try
        {
            var text = value is string s ? s : value.ToString() ?? string.Empty;
            return TruncateString(text, maxStringLength);
        }
        catch
        {
            return value.GetType().FullName ?? value.GetType().Name;
        }
    }

    private static string? GetValueSourceWpf(DependencyObject element, DependencyProperty property)
    {
        try
        {
            var source = DependencyPropertyHelper.GetValueSource(element, property);
            var flags = new List<string>();

            if (source.IsExpression)
            {
                flags.Add("Expression");
            }

            if (source.IsAnimated)
            {
                flags.Add("Animated");
            }

            if (source.IsCoerced)
            {
                flags.Add("Coerced");
            }

            var baseSource = source.BaseValueSource.ToString();
            if (flags.Count == 0)
            {
                return baseSource;
            }

            return baseSource + " (" + string.Join(", ", flags) + ")";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetValidationErrorMessage(DependencyObject element, BindingExpressionBase expression)
    {
        try
        {
            var errors = System.Windows.Controls.Validation.GetErrors(element);
            if (errors is null || errors.Count == 0)
            {
                return null;
            }

            foreach (var error in errors)
            {
                if (ReferenceEquals(error.BindingInError, expression))
                {
                    return FormatValidationErrorMessage(error, maxValueLength: 2000);
                }
            }

            if (expression.HasError)
            {
                var first = errors[0];
                return FormatValidationErrorMessage(first, maxValueLength: 2000);
            }
        }
        catch
        {
        }

        return null;
    }

    public static GetBindingInfoResponse GetBindingInfo(
        string ownerId,
        GetBindingInfoRequest request,
        CancellationToken cancellationToken) =>
        GetBindingInfo(ownerId, request, cancellationToken, visibleOnly: true);

    private static GetBindingInfoResponse GetBindingInfo(
        string ownerId,
        GetBindingInfoRequest request,
        CancellationToken cancellationToken,
        bool visibleOnly,
        int maxNodes = 200_000)
    {
        var maxProperties = Math.Clamp(request.MaxProperties, 1, 50_000);
        var valueFormat = string.IsNullOrWhiteSpace(request.ValueFormat) ? "string" : request.ValueFormat;
        maxNodes = Math.Clamp(maxNodes, 1, 200_000);

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
            visibleOnly,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = BuildElementRefWpf(ownerId, element, xpath, FindReturnFields.Standard);

        var properties = new HashSet<DependencyProperty>(GetDependencyPropertiesCached(element.GetType()));
        var propertyInspectionComplete = true;
        try
        {
            var enumerator = element.GetLocalValueEnumerator();
            while (enumerator.MoveNext())
            {
                properties.Add(enumerator.Current.Property);
            }
        }
        catch
        {
            propertyInspectionComplete = false;
        }

        var orderedProperties = properties.OrderBy(dp => dp.Name, StringComparer.Ordinal).ToArray();

        var bindings = new List<BindingInfo>();
        var discoveredBindings = 0;
        var scannedProperties = 0;

        const int maxValueLength = 2000;

        foreach (var property in orderedProperties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedProperties++;

            BindingExpressionBase? expression = null;
            var expressionInspected = true;
            try
            {
                expression = BindingOperations.GetBindingExpressionBase(element, property);
            }
            catch
            {
                expressionInspected = false;
                propertyInspectionComplete = false;
            }

            if (!expressionInspected)
            {
                continue;
            }

            if (expression is null)
            {
                if (!request.IncludeUnbound)
                {
                    continue;
                }

                discoveredBindings++;
                if (bindings.Count >= maxProperties)
                {
                    continue;
                }

                string? currentValue = null;
                try
                {
                    currentValue = FormatValueForBinding(element.GetValue(property), valueFormat, maxValueLength);
                }
                catch
                {
                }

                bindings.Add(new BindingInfo(
                    TargetProperty: property.Name,
                    BindingKind: "Unbound",
                    Status: "Unbound",
                    CurrentValue: currentValue,
                    ValueSource: GetValueSourceWpf(element, property)));

                continue;
            }

            discoveredBindings++;
            if (bindings.Count >= maxProperties)
            {
                continue;
            }

            var bindingKind = expression switch
            {
                BindingExpression => "Binding",
                MultiBindingExpression => "MultiBinding",
                PriorityBindingExpression => "PriorityBinding",
                _ => expression.GetType().Name
            };

            string? path = null;
            string? source = null;
            string? mode = null;
            string? updateSourceTrigger = null;
            string? converter = null;

            try
            {
                if (expression is BindingExpression be)
                {
                    var binding = be.ParentBinding;
                    path = binding.Path?.Path;
                    source = DescribeBindingSource(binding);
                    mode = binding.Mode.ToString();
                    updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
                    converter = binding.Converter?.GetType().FullName;
                }
                else if (expression is MultiBindingExpression mbe)
                {
                    var binding = mbe.ParentMultiBinding;
                    source = "MultiBinding";
                    mode = binding.Mode.ToString();
                    updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
                    converter = binding.Converter?.GetType().FullName;
                }
                else if (expression is PriorityBindingExpression pbe)
                {
                    source = "PriorityBinding";
                }
            }
            catch
            {
            }

            var status = "Unknown";
            try
            {
                status = expression.Status.ToString();
            }
            catch
            {
            }

            string? errorMessage = null;
            try
            {
                errorMessage = TryGetValidationErrorMessage(element, expression);
            }
            catch
            {
            }

            string? currentBoundValue = null;
            try
            {
                currentBoundValue = FormatValueForBinding(element.GetValue(property), valueFormat, maxValueLength);
            }
            catch
            {
            }

            bindings.Add(new BindingInfo(
                TargetProperty: property.Name,
                BindingKind: bindingKind,
                Path: path,
                Source: source,
                Mode: mode,
                UpdateSourceTrigger: updateSourceTrigger,
                Status: status,
                ErrorMessage: errorMessage,
                CurrentValue: currentBoundValue,
                ValueSource: GetValueSourceWpf(element, property),
                Converter: converter));
        }

        var truncatedReasons = new List<string>(2);
        if (discoveredBindings > bindings.Count)
        {
            truncatedReasons.Add("maxProperties");
        }

        if (!propertyInspectionComplete)
        {
            truncatedReasons.Add("propertyInspectionUnavailable");
        }

        return new GetBindingInfoResponse(
            Element: elementRef,
            Bindings: bindings,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault())
        {
            WindowHandleUsed = GetWindowHandle(window),
            ReturnedBindings = bindings.Count,
            DiscoveredBindings = discoveredBindings,
            ScannedProperties = scannedProperties,
            ScanComplete = propertyInspectionComplete,
            TruncatedReasons = truncatedReasons.Count > 0 ? truncatedReasons : null
        };
    }

    private readonly record struct BindingErrorDiscovery(
        int DiscoveredErrors,
        bool InspectionComplete);

    private static BindingErrorDiscovery CollectBindingErrorsForElement(
        DependencyObject element,
        string elementXPath,
        List<BindingErrorInfo> errors,
        int maxErrors,
        CancellationToken cancellationToken)
    {
        LocalValueEnumerator localValues;
        try
        {
            localValues = element.GetLocalValueEnumerator();
        }
        catch
        {
            return new BindingErrorDiscovery(0, InspectionComplete: false);
        }

        var discoveredErrors = 0;
        var inspectionComplete = true;

        while (localValues.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var property = localValues.Current.Property;
            BindingExpressionBase? expression = null;
            var expressionInspected = true;
            try
            {
                expression = BindingOperations.GetBindingExpressionBase(element, property);
            }
            catch
            {
                expressionInspected = false;
                inspectionComplete = false;
            }

            if (!expressionInspected)
            {
                continue;
            }

            if (expression is null)
            {
                continue;
            }

            var shouldReport = false;
            try
            {
                shouldReport = expression.HasError || expression.Status != BindingStatus.Active;
            }
            catch
            {
                inspectionComplete = false;
                continue;
            }

            if (!shouldReport)
            {
                continue;
            }

            discoveredErrors++;
            if (errors.Count >= maxErrors)
            {
                continue;
            }

            string? path = null;
            try
            {
                if (expression is BindingExpression be)
                {
                    path = be.ParentBinding.Path?.Path;
                }
            }
            catch
            {
            }

            string? errorMessage = null;
            try
            {
                errorMessage = TryGetValidationErrorMessage(element, expression);
            }
            catch
            {
            }

            var status = "Unknown";
            try
            {
                status = expression.Status.ToString();
            }
            catch
            {
            }

            errors.Add(new BindingErrorInfo(
                ElementXPath: elementXPath,
                ElementType: element.GetType().Name,
                ElementName: GetName(element),
                AutomationId: GetAutomationId(element),
                TargetProperty: property.Name,
                Path: path,
                ErrorMessage: errorMessage,
                Status: status));
        }

        return new BindingErrorDiscovery(discoveredErrors, inspectionComplete);
    }

    public static GetBindingErrorsResponse GetBindingErrors(
        string ownerId,
        GetBindingErrorsRequest request,
        CancellationToken cancellationToken)
    {
        var depth = request.Depth <= 0 ? 1 : request.Depth;
        var maxErrors = Math.Clamp(request.MaxErrors, 1, 5000);
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
        const bool visibleOnly = true;

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, visibleOnly, cancellationToken);
        }

        var errors = new List<BindingErrorInfo>();
        var scannedNodes = 0;
        var discoveredErrors = 0;
        var nodeScanComplete = true;
        var propertyInspectionComplete = true;

        var stack = new Stack<(DependencyObject Element, string XPath, int RemainingDepth)>();
        stack.Push((rootObject, rootXPath, depth));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                nodeScanComplete = false;
                break;
            }

            var (current, currentXPath, remainingDepth) = stack.Pop();
            scannedNodes++;

            if (!ReferenceEquals(current, rootObject) && visibleOnly && !IsVisibleWpf(current))
            {
                continue;
            }

            var discovery = CollectBindingErrorsForElement(
                current,
                currentXPath,
                errors,
                maxErrors,
                cancellationToken);
            discoveredErrors += discovery.DiscoveredErrors;
            propertyInspectionComplete &= discovery.InspectionComplete;

            if (remainingDepth <= 1)
            {
                continue;
            }

            var rawChildren = GetChildrenWpf(current, treeService, visibleOnly, includeOffViewport: true, viewportBounds: null);
            if (rawChildren.Length == 0)
            {
                continue;
            }

            var labels = rawChildren.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = rawChildren.Length - 1; i >= 0; i--)
            {
                var child = rawChildren[i];
                var label = labels[i];

                runningIndexByLabel.TryGetValue(label, out var currentIndex);
                currentIndex++;
                runningIndexByLabel[label] = currentIndex;

                var includeIndex = countsByLabel[label] > 1;
                var segment = label;

                // Note: we iterate backwards; adjust index to keep XPath stable.
                if (includeIndex)
                {
                    var oneBasedForwardIndex = countsByLabel[label] - currentIndex + 1;
                    segment = $"{label}[{oneBasedForwardIndex}]";
                }

                var childXPath = $"{currentXPath}/{segment}";
                stack.Push((child, childXPath, remainingDepth - 1));
            }
        }

        var truncatedReasons = new List<string>(3);
        if (discoveredErrors > errors.Count)
        {
            truncatedReasons.Add("maxErrors");
        }

        if (!nodeScanComplete)
        {
            truncatedReasons.Add("maxNodes");
        }

        if (!propertyInspectionComplete)
        {
            truncatedReasons.Add("propertyInspectionUnavailable");
        }

        return new GetBindingErrorsResponse(
            Errors: errors,
            ScannedNodes: scannedNodes,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault())
        {
            WindowHandleUsed = GetWindowHandle(window),
            ReturnedErrors = errors.Count,
            DiscoveredErrors = discoveredErrors,
            ScanComplete = nodeScanComplete && propertyInspectionComplete,
            TruncatedReasons = truncatedReasons.Count > 0 ? truncatedReasons : null
        };
    }

    public static GetUiaCoverageReportResponse GetUiaCoverageReport(
        string ownerId,
        GetUiaCoverageReportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
        var maxFindings = Math.Clamp(request.MaxFindings, 1, 5000);
        var visibleOnly = request.VisibleOnly;
        var includeOffViewport = request.IncludeOffViewport;
        var interactiveOnly = request.InteractiveOnly;
        var interactiveMode = request.InteractiveMode;

        var window = ResolveWindow(request.WindowHandle);
        var viewportBounds = visibleOnly && !includeOffViewport ? TryGetClientBoundsScreen(window) : null;
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, visibleOnly, cancellationToken);
        }

        var findings = new List<UiaCoverageFinding>();
        var warnings = new List<string>();
        var discoveredIssueCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var scannedNodes = 0;
        var consideredNodes = 0;
        var discoveredFindings = 0;
        var scanComplete = true;

        try
        {
            foreach (var (element, xpath) in EnumerateDescendantsWithXPath(
                         root: rootObject,
                         rootXPath: rootXPath,
                         treeService: treeService,
                         visibleOnly: visibleOnly,
                         includeOffViewport: includeOffViewport,
                         viewportBounds: viewportBounds,
                         maxNodes: maxNodes,
                         cancellationToken: cancellationToken,
                         onNodeScanned: () => scannedNodes++))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isInteractive = IsInteractiveWpf(element, interactiveMode);
                if (interactiveOnly && !isInteractive)
                {
                    continue;
                }

                consideredNodes++;

                var elementRef = BuildElementRefWpf(ownerId, element, xpath, FindReturnFields.Standard);

                var elementFindings = AnalyzeCoverageForElement(element, elementRef, isInteractive);
                foreach (var finding in elementFindings)
                {
                    discoveredFindings++;
                    discoveredIssueCounts.TryGetValue(finding.IssueCode, out var issueCount);
                    discoveredIssueCounts[finding.IssueCode] = issueCount + 1;
                    if (findings.Count < maxFindings)
                    {
                        findings.Add(finding);
                    }
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Search exceeded maxNodes=", StringComparison.OrdinalIgnoreCase))
        {
            scanComplete = false;
            warnings.Add(ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var ordered = findings
            .OrderByDescending(f => GetSeverityRank(f.Severity))
            .ThenBy(f => f.Element.XPath, StringComparer.Ordinal)
            .ToArray();

        var issueCounts = ordered
            .GroupBy(f => f.IssueCode, StringComparer.Ordinal)
            .Select(g => new UiaCoverageIssueCount(g.Key, g.Count()))
            .OrderByDescending(i => i.Count)
            .ThenBy(i => i.IssueCode, StringComparer.Ordinal)
            .ToArray();

        var orderedDiscoveredIssueCounts = discoveredIssueCounts
            .Select(pair => new UiaCoverageIssueCount(pair.Key, pair.Value))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.IssueCode, StringComparer.Ordinal)
            .ToArray();

        var truncatedReasons = new List<string>(2);
        if (discoveredFindings > ordered.Length)
        {
            truncatedReasons.Add("maxFindings");
        }

        if (!scanComplete)
        {
            truncatedReasons.Add("maxNodes");
        }

        var summary = new UiaCoverageSummary(
            ScannedNodes: scannedNodes,
            ConsideredNodes: consideredNodes,
            FindingsCount: ordered.Length,
            IssueCounts: issueCounts,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault())
        {
            ReturnedFindings = ordered.Length,
            DiscoveredFindings = discoveredFindings,
            ScanComplete = scanComplete,
            DiscoveredIssueCounts = orderedDiscoveredIssueCounts,
            TruncatedReasons = truncatedReasons.Count > 0 ? truncatedReasons : null
        };

        return new GetUiaCoverageReportResponse(
            Summary: summary,
            Findings: ordered,
            Warnings: warnings.Count > 0 ? warnings : null)
        {
            WindowHandleUsed = GetWindowHandle(window)
        };
    }

    private static IReadOnlyList<UiaCoverageFinding> AnalyzeCoverageForElement(DependencyObject element, ElementRef elementRef, bool isInteractive)
    {
        var findings = new List<UiaCoverageFinding>();

        var bounds = elementRef.Bounds;
        if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            findings.Add(new UiaCoverageFinding(
                IssueCode: "empty_bounds",
                Severity: "info",
                Element: elementRef,
                Details: ["Element has empty or unavailable bounds."],
                Suggestions:
                [
                    "Ensure the element is loaded and visible before interacting.",
                    "If this is a custom control, verify Measure/Arrange produce a non-zero size."
                ]));
        }

        var peer = TryCreateAutomationPeer(element);
        if (peer is null)
        {
            // Only UIElement / ContentElement can participate in WPF automation peers.
            if (element is UIElement or ContentElement)
            {
                var severity = isInteractive && IsLikelyInteractiveCoverageType(element) ? "error" : "info";
                findings.Add(new UiaCoverageFinding(
                    IssueCode: "no_automation_peer",
                    Severity: severity,
                    Element: elementRef,
                    Details: ["No AutomationPeer was created for this element."],
                    Suggestions:
                    [
                        "Implement OnCreateAutomationPeer() for custom controls.",
                        "Use built-in WPF controls when possible (they expose UIA patterns automatically).",
                        "Set AutomationProperties.Name and/or AutomationProperties.AutomationId to improve discoverability."
                    ]));
            }

            return findings;
        }

        var peerName = "";
        try
        {
            peerName = peer.GetName() ?? "";
        }
        catch
        {
        }

        var hasPeerName = !string.IsNullOrWhiteSpace(peerName);

        var automationName = "";
        try
        {
            automationName = AutomationProperties.GetName(element) ?? "";
        }
        catch
        {
        }

        var hasAutomationPropertiesName = !string.IsNullOrWhiteSpace(automationName);

        if (isInteractive && !HasAnyActionablePattern(peer))
        {
            var severity = IsLikelyInteractiveCoverageElement(element, peer) ? "warning" : "info";
            findings.Add(new UiaCoverageFinding(
                IssueCode: "no_actionable_patterns",
                Severity: severity,
                Element: elementRef,
                Details: hasPeerName
                    ?
                    [
                        "AutomationPeer exists but exposes no common interaction patterns (Invoke/Value/RangeValue/Toggle/Selection/Scroll).",
                        "peer_name_present"
                    ]
                    :
                    [
                        "AutomationPeer exists but exposes no common interaction patterns (Invoke/Value/RangeValue/Toggle/Selection/Scroll).",
                        "peer_name_empty"
                    ],
                Suggestions:
                [
                    "If this control is interactive, implement UIA patterns by overriding AutomationPeer.GetPattern().",
                    "If this is meant to be clickable, expose InvokePattern via a suitable AutomationPeer.",
                    "If this is an input control, expose ValuePattern or RangeValuePattern as appropriate."
                ]));
        }

        var accessibleNameFinding = CreateMissingAccessibleNameFinding(
            elementRef,
            isInteractive,
            isInteractive &&
            !hasPeerName &&
            !hasAutomationPropertiesName &&
            IsLikelyInteractiveCoverageElement(element, peer),
            hasPeerName ? peerName : null,
            hasAutomationPropertiesName ? automationName : null);
        if (accessibleNameFinding is not null)
        {
            findings.Add(accessibleNameFinding);
        }

        return findings;
    }

    internal static UiaCoverageFinding? CreateMissingAccessibleNameFinding(
        ElementRef element,
        bool isInteractive,
        bool isLikelyInteractive,
        string? peerName,
        string? automationPropertiesName)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!isInteractive ||
            !string.IsNullOrWhiteSpace(peerName) ||
            !string.IsNullOrWhiteSpace(automationPropertiesName))
        {
            return null;
        }

        var hasAutomationId = !string.IsNullOrWhiteSpace(element.AutomationId);
        var automationIdState = hasAutomationId ? "automation_id_present" : "automation_id_empty";
        IReadOnlyList<string> suggestions = hasAutomationId
            ?
            [
                "Set AutomationProperties.Name to an accessible, user-facing label.",
                "Keep AutomationProperties.AutomationId as a stable automation identifier; it does not provide an accessible name."
            ]
            :
            [
                "Set AutomationProperties.Name to an accessible, user-facing label.",
                "Optionally set AutomationProperties.AutomationId to a stable identifier for automation."
            ];
        return new UiaCoverageFinding(
            IssueCode: "missing_accessible_name",
            Severity: isLikelyInteractive ? "warning" : "info",
            Element: element,
            Details:
            [
                "Element has no accessible name (UIA Name / AutomationProperties.Name).",
                "peer_name_empty",
                "automation_properties_name_empty",
                automationIdState
            ],
            Suggestions: suggestions);
    }

    private static bool IsLikelyInteractiveCoverageElement(DependencyObject element, AutomationPeer peer)
    {
        if (IsLikelyInteractiveCoverageType(element))
        {
            return true;
        }

        try
        {
            var controlType = peer.GetAutomationControlType();
            return controlType is AutomationControlType.Button
                or AutomationControlType.CheckBox
                or AutomationControlType.ComboBox
                or AutomationControlType.Edit
                or AutomationControlType.Hyperlink
                or AutomationControlType.List
                or AutomationControlType.ListItem
                or AutomationControlType.Menu
                or AutomationControlType.MenuBar
                or AutomationControlType.MenuItem
                or AutomationControlType.RadioButton
                or AutomationControlType.ScrollBar
                or AutomationControlType.Slider
                or AutomationControlType.Spinner
                or AutomationControlType.SplitButton
                or AutomationControlType.Tab
                or AutomationControlType.TabItem
                or AutomationControlType.Tree
                or AutomationControlType.TreeItem;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyInteractiveCoverageType(DependencyObject element)
    {
        return element is ButtonBase
            or ToggleButton
            or TextBoxBase
            or Selector
            or RangeBase
            or ScrollBar
            or MenuItem
            or ListBoxItem
            or ComboBoxItem
            or TabItem
            or TreeViewItem
            or Thumb;
    }

    private static AutomationPeer? TryCreateAutomationPeer(DependencyObject element)
    {
        try
        {
            return element switch
            {
                UIElement ue => UIElementAutomationPeer.CreatePeerForElement(ue),
                ContentElement ce => ContentElementAutomationPeer.CreatePeerForElement(ce),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAnyActionablePattern(AutomationPeer peer)
    {
        try
        {
            return peer.GetPattern(PatternInterface.Invoke) is not null
                   || peer.GetPattern(PatternInterface.Selection) is not null
                   || peer.GetPattern(PatternInterface.SelectionItem) is not null
                   || peer.GetPattern(PatternInterface.Toggle) is not null
                   || peer.GetPattern(PatternInterface.Value) is not null
                   || peer.GetPattern(PatternInterface.RangeValue) is not null
                   || peer.GetPattern(PatternInterface.Scroll) is not null
                   || peer.GetPattern(PatternInterface.ScrollItem) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static int GetSeverityRank(string severity) =>
        severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? 3 :
        severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? 2 :
        severity.Equals("info", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private sealed record DataContextSerializationOptions(
        int MaxDepth,
        int MaxPropertiesPerObject,
        int MaxStringLength,
        bool IncludeNulls,
        bool IncludeFrameworkProperties,
        HashSet<string>? PropertyAllowList);

    private sealed class DataContextTruncationTracker
    {
        private bool _maxDepth;
        private bool _maxPropertiesPerObject;
        private bool _maxStringLength;

        public bool Truncated => _maxDepth || _maxPropertiesPerObject || _maxStringLength;

        public void MarkMaxDepth() => _maxDepth = true;

        public void MarkMaxPropertiesPerObject() => _maxPropertiesPerObject = true;

        public void MarkMaxStringLength() => _maxStringLength = true;

        public IReadOnlyList<string>? GetReasons()
        {
            var reasons = new List<string>(3);
            if (_maxDepth)
            {
                reasons.Add("maxDepth");
            }

            if (_maxPropertiesPerObject)
            {
                reasons.Add("maxPropertiesPerObject");
            }

            if (_maxStringLength)
            {
                reasons.Add("maxStringLength");
            }

            return reasons.Count == 0 ? null : reasons;
        }
    }

    private static IReadOnlyList<string> NormalizeDataContextPropertyPaths(
        IReadOnlyList<string>? requestedPaths,
        int maxDepth)
    {
        if (requestedPaths is null || requestedPaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (requestedPaths.Count > DataContextPropertyPathLimits.MaxPaths)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                $"invalid_request: propertyPaths supports at most {DataContextPropertyPathLimits.MaxPaths} paths.",
                nameof(GetDataContextRequest.PropertyPaths));
        }

        var maxSegments = Math.Clamp(maxDepth, 1, DataContextPropertyPathLimits.MaxSegments);
        var normalized = new List<string>(requestedPaths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requestedPath in requestedPaths)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                throw AgentToolError.InvalidArgument(
                    "invalid_request",
                    "invalid_request: propertyPaths cannot contain blank values.",
                    nameof(GetDataContextRequest.PropertyPaths));
            }

            var path = requestedPath.Trim();
            if (path.Length > DataContextPropertyPathLimits.MaxPathLength || !IsDottedIdentifierPath(path))
            {
                throw AgentToolError.InvalidArgument(
                    "invalid_request",
                    $"invalid_request: property path '{path}' must contain at most {DataContextPropertyPathLimits.MaxSegments} dotted identifiers and {DataContextPropertyPathLimits.MaxPathLength} characters.",
                    nameof(GetDataContextRequest.PropertyPaths));
            }

            if (path.Split('.').Length > maxSegments)
            {
                throw AgentToolError.InvalidArgument(
                    "invalid_request",
                    $"invalid_request: property path '{path}' exceeds maxDepth={maxDepth}.",
                    nameof(GetDataContextRequest.PropertyPaths));
            }

            if (seen.Add(path))
            {
                normalized.Add(path);
            }
        }

        for (var left = 0; left < normalized.Count; left++)
        {
            for (var right = left + 1; right < normalized.Count; right++)
            {
                if (normalized[left].StartsWith(normalized[right] + ".", StringComparison.Ordinal) ||
                    normalized[right].StartsWith(normalized[left] + ".", StringComparison.Ordinal))
                {
                    throw AgentToolError.InvalidArgument(
                        "invalid_request",
                        $"invalid_request: propertyPaths cannot contain overlapping paths '{normalized[left]}' and '{normalized[right]}'.",
                        nameof(GetDataContextRequest.PropertyPaths));
                }
            }
        }

        return normalized;
    }

    private static JsonObject BuildDataContextPropertyPathProjection(
        object dataContext,
        IReadOnlyList<string> propertyPaths,
        DataContextSerializationOptions options,
        DataContextTruncationTracker truncation,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var projection = new JsonObject();
        var prefixCache = new Dictionary<string, DataContextPropertyPathPrefix>(StringComparer.Ordinal);
        foreach (var path in propertyPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = path.Split('.');
            object? current = dataContext;
            var resolved = true;
            var prefix = "";

            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prefix = prefix.Length == 0 ? segment : $"{prefix}.{segment}";
                if (prefixCache.TryGetValue(prefix, out var cached))
                {
                    if (!cached.Resolved)
                    {
                        warnings.Add($"{cached.WarningCode}: '{path}' {cached.WarningDetail}");
                        resolved = false;
                        break;
                    }

                    current = cached.Value;
                    continue;
                }

                if (current is null)
                {
                    var failure = new DataContextPropertyPathPrefix(
                        Resolved: false,
                        Value: null,
                        WarningCode: "property_path_null",
                        WarningDetail: $"could not continue at '{segment}'.");
                    prefixCache[prefix] = failure;
                    warnings.Add($"{failure.WarningCode}: '{path}' {failure.WarningDetail}");
                    resolved = false;
                    break;
                }

                PropertyInfo? property;
                try
                {
                    property = current.GetType()
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(candidate =>
                            candidate.CanRead &&
                            candidate.GetMethod?.IsPublic == true &&
                            candidate.GetIndexParameters().Length == 0 &&
                            string.Equals(candidate.Name, segment, StringComparison.Ordinal));
                }
                catch (Exception ex)
                {
                    var failure = new DataContextPropertyPathPrefix(
                        Resolved: false,
                        Value: null,
                        WarningCode: "property_path_error",
                        WarningDetail: $"metadata read failed with {ex.GetType().Name}.");
                    prefixCache[prefix] = failure;
                    warnings.Add($"{failure.WarningCode}: '{path}' {failure.WarningDetail}");
                    resolved = false;
                    break;
                }

                if (property is null)
                {
                    var failure = new DataContextPropertyPathPrefix(
                        Resolved: false,
                        Value: null,
                        WarningCode: "property_path_not_found",
                        WarningDetail: $"stopped at '{segment}'.");
                    prefixCache[prefix] = failure;
                    warnings.Add($"{failure.WarningCode}: '{path}' {failure.WarningDetail}");
                    resolved = false;
                    break;
                }

                try
                {
                    current = property.GetValue(current);
                }
                catch (Exception ex)
                {
                    var failure = ex is TargetInvocationException { InnerException: not null }
                        ? ex.InnerException
                        : ex;
                    var failedPrefix = new DataContextPropertyPathPrefix(
                        Resolved: false,
                        Value: null,
                        WarningCode: "property_path_error",
                        WarningDetail: $"getter failed with {failure.GetType().Name}.");
                    prefixCache[prefix] = failedPrefix;
                    warnings.Add($"{failedPrefix.WarningCode}: '{path}' {failedPrefix.WarningDetail}");
                    resolved = false;
                    break;
                }

                prefixCache[prefix] = new DataContextPropertyPathPrefix(
                    Resolved: true,
                    Value: current,
                    WarningCode: null,
                    WarningDetail: null);
            }

            if (!resolved)
            {
                continue;
            }

            var value = SerializeDataContextValueSummary(
                current,
                options,
                remainingDepth: 0,
                new HashSet<object>(ReferenceEqualityComparer.Instance),
                truncation,
                warnings,
                applyAllowList: false,
                cancellationToken);
            InsertDataContextPropertyPath(projection, segments, value);
        }

        return projection;
    }

    private sealed record DataContextPropertyPathPrefix(
        bool Resolved,
        object? Value,
        string? WarningCode,
        string? WarningDetail);

    private static void InsertDataContextPropertyPath(JsonObject projection, string[] segments, JsonNode? value)
    {
        var current = projection;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
    }

    private static JsonValue SerializeDoubleJson(double value)
    {
        if (double.IsNaN(value))
        {
            return JsonValue.Create("{NaN}")!;
        }

        if (double.IsPositiveInfinity(value))
        {
            return JsonValue.Create("{Infinity}")!;
        }

        if (double.IsNegativeInfinity(value))
        {
            return JsonValue.Create("{-Infinity}")!;
        }

        return JsonValue.Create(value)!;
    }

    private static JsonValue SerializeFloatJson(float value)
    {
        if (float.IsNaN(value))
        {
            return JsonValue.Create("{NaN}")!;
        }

        if (float.IsPositiveInfinity(value))
        {
            return JsonValue.Create("{Infinity}")!;
        }

        if (float.IsNegativeInfinity(value))
        {
            return JsonValue.Create("{-Infinity}")!;
        }

        return JsonValue.Create(value)!;
    }

    private static string FormatDataContextTextBestEffort(
        object value,
        string nullResultFallback,
        string warningTarget,
        int maxStringLength,
        DataContextTruncationTracker truncation,
        List<string> warnings)
    {
        string text;
        try
        {
            text = value.ToString() ?? nullResultFallback;
        }
        catch (Exception ex)
        {
            var valueType = value.GetType();
            text = valueType.FullName ?? valueType.Name;
            warnings.Add($"{warningTarget}: {ex.GetType().Name}");
        }

        var bounded = TruncateString(text, maxStringLength, out var wasTruncated);
        if (wasTruncated)
        {
            truncation.MarkMaxStringLength();
        }

        return bounded;
    }

    private static JsonNode? SerializeDataContextValueFull(
        object? value,
        DataContextSerializationOptions options,
        int remainingDepth,
        HashSet<object> visited,
        DataContextTruncationTracker truncation,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (value is null)
        {
            return null;
        }

        if (ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return JsonValue.Create("{UnsetValue}");
        }

        if (value is string s)
        {
            var bounded = TruncateString(s, options.MaxStringLength, out var wasTruncated);
            if (wasTruncated)
            {
                truncation.MarkMaxStringLength();
            }

            return JsonValue.Create(bounded);
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
        {
            return JsonValue.Create(value);
        }

        if (value is double d)
        {
            return SerializeDoubleJson(d);
        }

        if (value is float f)
        {
            return SerializeFloatJson(f);
        }

        if (value is char ch)
        {
            return JsonValue.Create(ch.ToString());
        }

        if (value is Guid guid)
        {
            return JsonValue.Create(guid.ToString());
        }

        if (value is DateTime dt)
        {
            return JsonValue.Create(dt.ToString("O"));
        }

        if (value is DateTimeOffset dto)
        {
            return JsonValue.Create(dto.ToString("O"));
        }

        if (value is TimeSpan ts)
        {
            return JsonValue.Create(ts.ToString());
        }

        if (value is Enum en)
        {
            return JsonValue.Create(en.ToString());
        }

        if (remainingDepth <= 0)
        {
            truncation.MarkMaxDepth();
            var type = value.GetType();
            return JsonValue.Create(type.FullName ?? type.Name);
        }

        if (!visited.Add(value))
        {
            return JsonValue.Create("<cycle>");
        }

        if (value is IDictionary dictionary)
        {
            var obj = new JsonObject();
            var dictCount = 0;

            foreach (DictionaryEntry entry in dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.Value is null && !options.IncludeNulls)
                {
                    continue;
                }

                if (dictCount >= options.MaxPropertiesPerObject)
                {
                    truncation.MarkMaxPropertiesPerObject();
                    break;
                }

                var key = entry.Key is null
                    ? "null"
                    : FormatDataContextTextBestEffort(
                        value: entry.Key,
                        nullResultFallback: "null",
                        warningTarget: "DataContext dictionary key ToString",
                        maxStringLength: options.MaxStringLength,
                        truncation: truncation,
                        warnings: warnings);
                var node = SerializeDataContextValueFull(
                    entry.Value,
                    options,
                    remainingDepth - 1,
                    visited,
                    truncation,
                    warnings,
                    cancellationToken);

                obj[key] = node;
                dictCount++;
            }

            return obj;
        }

        if (value is IEnumerable enumerable)
        {
            var array = new JsonArray();
            var itemCount = 0;

            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (itemCount >= options.MaxPropertiesPerObject)
                {
                    truncation.MarkMaxPropertiesPerObject();
                    break;
                }

                var node = SerializeDataContextValueFull(
                    item,
                    options,
                    remainingDepth - 1,
                    visited,
                    truncation,
                    warnings,
                    cancellationToken);
                if (node is null && !options.IncludeNulls)
                {
                    array.Add(null);
                }
                else
                {
                    array.Add(node);
                }

                itemCount++;
            }

            return array;
        }

        var valueType = value.GetType();
        var properties = valueType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        var json = new JsonObject();

        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                if (json.Count >= options.MaxPropertiesPerObject)
                {
                    truncation.MarkMaxPropertiesPerObject();
                    break;
                }

                json[property.Name] = JsonValue.Create("<error>");
                continue;
            }

            if (propertyValue is null && !options.IncludeNulls)
            {
                continue;
            }

            if (json.Count >= options.MaxPropertiesPerObject)
            {
                truncation.MarkMaxPropertiesPerObject();
                break;
            }

            var node = SerializeDataContextValueFull(
                propertyValue,
                options,
                remainingDepth - 1,
                visited,
                truncation,
                warnings,
                cancellationToken);

            json[property.Name] = node;
        }

        return json;
    }

    private static JsonNode? SerializeDataContextValueSummary(
        object? value,
        DataContextSerializationOptions options,
        int remainingDepth,
        HashSet<object> visited,
        DataContextTruncationTracker truncation,
        List<string> warnings,
        bool applyAllowList,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (value is null)
        {
            return null;
        }

        if (ReferenceEquals(value, DependencyProperty.UnsetValue))
        {
            return JsonValue.Create("{UnsetValue}");
        }

        if (value is string s)
        {
            var bounded = TruncateString(s, options.MaxStringLength, out var wasTruncated);
            if (wasTruncated)
            {
                truncation.MarkMaxStringLength();
            }

            return JsonValue.Create(bounded);
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
        {
            return JsonValue.Create(value);
        }

        if (value is double d)
        {
            return SerializeDoubleJson(d);
        }

        if (value is float f)
        {
            return SerializeFloatJson(f);
        }

        if (value is char ch)
        {
            return JsonValue.Create(ch.ToString());
        }

        if (value is Guid guid)
        {
            return JsonValue.Create(guid.ToString());
        }

        if (value is DateTime dt)
        {
            return JsonValue.Create(dt.ToString("O"));
        }

        if (value is DateTimeOffset dto)
        {
            return JsonValue.Create(dto.ToString("O"));
        }

        if (value is TimeSpan ts)
        {
            return JsonValue.Create(ts.ToString());
        }

        if (value is Enum en)
        {
            return JsonValue.Create(en.ToString());
        }

        var valueType = value.GetType();
        if (!options.IncludeFrameworkProperties && IsFrameworkType(valueType))
        {
            return JsonValue.Create(valueType.FullName ?? valueType.Name);
        }

        if (remainingDepth <= 0)
        {
            truncation.MarkMaxDepth();
            return JsonValue.Create(valueType.FullName ?? valueType.Name);
        }

        if (!visited.Add(value))
        {
            return JsonValue.Create("<cycle>");
        }

        if (value is IDictionary dictionary)
        {
            var obj = new JsonObject();
            var count = 0;

            foreach (DictionaryEntry entry in dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.Value is null && !options.IncludeNulls)
                {
                    continue;
                }

                if (count >= options.MaxPropertiesPerObject)
                {
                    truncation.MarkMaxPropertiesPerObject();
                    break;
                }

                var key = entry.Key is null
                    ? "null"
                    : FormatDataContextTextBestEffort(
                        value: entry.Key,
                        nullResultFallback: "null",
                        warningTarget: "DataContext dictionary key ToString",
                        maxStringLength: options.MaxStringLength,
                        truncation: truncation,
                        warnings: warnings);
                var node = SerializeDataContextValueSummary(
                    entry.Value,
                    options,
                    remainingDepth - 1,
                    visited,
                    truncation,
                    warnings,
                    applyAllowList: false,
                    cancellationToken);

                obj[key] = node;
                count++;
            }

            return obj;
        }

        if (value is IEnumerable enumerable)
        {
            var array = new JsonArray();
            var count = 0;

            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (count >= options.MaxPropertiesPerObject)
                {
                    truncation.MarkMaxPropertiesPerObject();
                    break;
                }

                var node = SerializeDataContextValueSummary(
                    item,
                    options,
                    remainingDepth - 1,
                    visited,
                    truncation,
                    warnings,
                    applyAllowList: false,
                    cancellationToken);
                if (node is null && !options.IncludeNulls)
                {
                    array.Add(null);
                }
                else
                {
                    array.Add(node);
                }

                count++;
            }

            return array;
        }

        HashSet<string>? allowList = applyAllowList ? options.PropertyAllowList : null;
        var properties = valueType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => allowList is null || allowList.Contains(p.Name))
            .Where(p => allowList is not null || IsScalarType(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        if (allowList is null && properties.Length == 0)
        {
            return JsonValue.Create(valueType.FullName ?? valueType.Name);
        }

        var json = new JsonObject();
        var propCount = 0;

        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception ex)
            {
                if (propCount >= options.MaxPropertiesPerObject)
                {
                    truncation.MarkMaxPropertiesPerObject();
                    break;
                }

                warnings.Add($"{valueType.Name}.{property.Name}: {ex.GetType().Name}");
                json[property.Name] = JsonValue.Create("<error>");
                propCount++;
                continue;
            }

            if (propertyValue is null)
            {
                if (options.IncludeNulls)
                {
                    if (propCount >= options.MaxPropertiesPerObject)
                    {
                        truncation.MarkMaxPropertiesPerObject();
                        break;
                    }

                    json[property.Name] = null;
                    propCount++;
                }

                continue;
            }

            if (propCount >= options.MaxPropertiesPerObject)
            {
                truncation.MarkMaxPropertiesPerObject();
                break;
            }

            if (IsScalarType(propertyValue.GetType()))
            {
                json[property.Name] = SerializeDataContextValueSummary(
                    propertyValue,
                    options,
                    remainingDepth - 1,
                    visited,
                    truncation,
                    warnings,
                    applyAllowList: false,
                    cancellationToken);
                propCount++;
                continue;
            }

            // Summary mode: don't explode object graphs. Represent complex values by type name unless explicitly allowed by recursion depth.
            json[property.Name] = JsonValue.Create(propertyValue.GetType().FullName ?? propertyValue.GetType().Name);
            propCount++;
        }

        return json;
    }

    private static bool IsScalarType(Type type)
    {
        if (type.IsEnum || type.IsPrimitive)
        {
            return true;
        }

        if (type == typeof(string) ||
            type == typeof(Guid) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) ||
            type == typeof(decimal))
        {
            return true;
        }

        var underlying = Nullable.GetUnderlyingType(type);
        return underlying is not null && IsScalarType(underlying);
    }

    private static bool IsFrameworkType(Type type)
    {
        if (typeof(DependencyObject).IsAssignableFrom(type))
        {
            return true;
        }

        var fullName = type.FullName;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        return fullName.StartsWith("System.Windows.", StringComparison.Ordinal) ||
               fullName.StartsWith("System.Windows.Threading.", StringComparison.Ordinal) ||
               fullName.StartsWith("System.Windows.Media.", StringComparison.Ordinal);
    }

    public static GetDataContextResponse GetDataContext(
        string ownerId,
        GetDataContextRequest request,
        CancellationToken cancellationToken) =>
        GetDataContext(ownerId, request, cancellationToken, visibleOnly: true);

    private static GetDataContextResponse GetDataContext(
        string ownerId,
        GetDataContextRequest request,
        CancellationToken cancellationToken,
        bool visibleOnly,
        int maxNodes = 200_000)
    {
        var maxDepth = Math.Clamp(request.MaxDepth, 0, 25);
        var maxPropertiesPerObject = Math.Clamp(request.MaxPropertiesPerObject, 1, 5000);
        var maxStringLength = Math.Clamp(request.MaxStringLength, 0, 200_000);
        maxNodes = Math.Clamp(maxNodes, 1, 200_000);
        var propertyPaths = NormalizeDataContextPropertyPaths(request.PropertyPaths, maxDepth);
        if (propertyPaths.Count > 0 && request.Mode == DataContextMode.Full)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: propertyPaths cannot be combined with mode=Full.",
                nameof(request.PropertyPaths));
        }

        if (propertyPaths.Count > 0 && request.PropertyAllowList is { Count: > 0 })
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: propertyPaths cannot be combined with propertyAllowList.",
                nameof(request.PropertyPaths));
        }

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
            visibleOnly,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var elementRef = BuildElementRefWpf(
            ownerId,
            resolved.Element,
            resolved.XPath,
            FindReturnFields.Standard);
        var windowHandleUsed = GetWindowHandle(window);

        object? dataContext = null;
        try
        {
            dataContext = resolved.Element switch
            {
                FrameworkElement fe => fe.DataContext,
                FrameworkContentElement fce => fce.DataContext,
                _ => null
            };
        }
        catch
        {
        }

        if (dataContext is null)
        {
            return new GetDataContextResponse(DataContextType: null, Data: null)
            {
                Element = elementRef,
                WindowHandleUsed = windowHandleUsed
            };
        }

        var dataContextType = dataContext.GetType().FullName ?? dataContext.GetType().Name;
        var truncation = new DataContextTruncationTracker();
        var warnings = new List<string>();
        var summary = FormatDataContextTextBestEffort(
            value: dataContext,
            nullResultFallback: "",
            warningTarget: "DataContext.ToString",
            maxStringLength: maxStringLength,
            truncation: truncation,
            warnings: warnings);

        if (propertyPaths.Count > 0)
        {
            var options = new DataContextSerializationOptions(
                MaxDepth: 0,
                MaxPropertiesPerObject: maxPropertiesPerObject,
                MaxStringLength: maxStringLength,
                IncludeNulls: true,
                IncludeFrameworkProperties: false,
                PropertyAllowList: null);
            var projection = BuildDataContextPropertyPathProjection(
                dataContext,
                propertyPaths,
                options,
                truncation,
                warnings,
                cancellationToken);
            var truncatedReasons = truncation.GetReasons();

            return new GetDataContextResponse(
                DataContextType: dataContextType,
                Data: projection,
                Summary: summary,
                Truncated: truncation.Truncated,
                Warnings: warnings.Count > 0 ? warnings : null)
            {
                Element = elementRef,
                WindowHandleUsed = windowHandleUsed,
                TruncatedReasons = truncatedReasons
            };
        }

        if (request.Mode == DataContextMode.Full)
        {
            var options = new DataContextSerializationOptions(
                MaxDepth: maxDepth,
                MaxPropertiesPerObject: maxPropertiesPerObject,
                MaxStringLength: maxStringLength,
                IncludeNulls: request.IncludeNulls,
                IncludeFrameworkProperties: true,
                PropertyAllowList: null);

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var data = SerializeDataContextValueFull(
                dataContext,
                options,
                maxDepth,
                visited,
                truncation,
                warnings,
                cancellationToken);
            var truncatedReasons = truncation.GetReasons();

            return new GetDataContextResponse(
                DataContextType: dataContextType,
                Data: data,
                Summary: summary,
                Truncated: truncation.Truncated,
                Warnings: warnings.Count > 0 ? warnings : null)
            {
                Element = elementRef,
                WindowHandleUsed = windowHandleUsed,
                TruncatedReasons = truncatedReasons
            };
        }

        var allowList = request.PropertyAllowList is { Count: > 0 }
            ? request.PropertyAllowList
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToHashSet(StringComparer.Ordinal)
            : null;

        var summaryOptions = new DataContextSerializationOptions(
            MaxDepth: maxDepth,
            MaxPropertiesPerObject: maxPropertiesPerObject,
            MaxStringLength: maxStringLength,
            IncludeNulls: request.IncludeNulls,
            IncludeFrameworkProperties: request.IncludeFrameworkProperties,
            PropertyAllowList: allowList);

        var summaryVisited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var summaryData = SerializeDataContextValueSummary(
            dataContext,
            summaryOptions,
            maxDepth,
            summaryVisited,
            truncation,
            warnings,
            applyAllowList: allowList is not null,
            cancellationToken);

        if (summaryData is JsonValue valueNode &&
            valueNode.TryGetValue<string>(out var typeName) &&
            string.Equals(typeName, dataContextType, StringComparison.Ordinal) &&
            allowList is null)
        {
            // Reduce noise for framework objects and opaque types: if we ended up with just the type name,
            // return null for Data and rely on DataContextType/Summary.
            summaryData = null;
        }

        var summaryTruncatedReasons = truncation.GetReasons();

        return new GetDataContextResponse(
            DataContextType: dataContextType,
            Data: summaryData,
            Summary: summary,
            Truncated: truncation.Truncated,
            Warnings: warnings.Count > 0 ? warnings : null)
        {
            Element = elementRef,
            WindowHandleUsed = windowHandleUsed,
            TruncatedReasons = summaryTruncatedReasons
        };
    }

    public static GetComputedPropertiesResponse GetComputedProperties(
        string ownerId,
        GetComputedPropertiesRequest request,
        CancellationToken cancellationToken) =>
        GetComputedProperties(ownerId, request, cancellationToken, visibleOnly: true);

    public static GetComputedPropertiesBatchResponse GetComputedPropertiesBatch(
        string ownerId,
        GetComputedPropertiesBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);

        if (request.WindowHandle is not long windowHandle || windowHandle == 0)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: windowHandle is required.",
                nameof(request.WindowHandle));
        }

        if (request.ElementIds is not { Count: > 0 })
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: provide at least one elementId.",
                nameof(request.ElementIds));
        }

        if (request.PropertyNames is not { Count: > 0 })
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                "invalid_request: provide at least one property name.",
                nameof(request.PropertyNames));
        }

        if (request.ElementIds.Count > ComputedPropertiesBatchLimits.MaxInputElements ||
            request.PropertyNames.Count > ComputedPropertiesBatchLimits.MaxInputProperties)
        {
            throw AgentToolError.InvalidArgument(
                "invalid_request",
                $"invalid_request: input supports at most {ComputedPropertiesBatchLimits.MaxInputElements} elementIds and {ComputedPropertiesBatchLimits.MaxInputProperties} propertyNames.");
        }

        var elementIds = NormalizeBatchValues(request.ElementIds, "elementIds");
        var propertyNames = NormalizeBatchValues(request.PropertyNames, "propertyNames");
        var maxElements = Math.Clamp(request.MaxElements, 1, ComputedPropertiesBatchLimits.MaxElements);
        var maxPropertiesPerElement = Math.Clamp(
            request.MaxPropertiesPerElement,
            1,
            ComputedPropertiesBatchLimits.MaxPropertiesPerElement);
        var selectedElementIds = elementIds.Take(maxElements).ToArray();
        var selectedPropertyNames = propertyNames.Take(maxPropertiesPerElement).ToArray();
        var results = new List<GetComputedPropertiesResponse>(selectedElementIds.Length);

        foreach (var elementId in selectedElementIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(GetComputedProperties(
                ownerId,
                new GetComputedPropertiesRequest(
                    WindowHandle: windowHandle,
                    ElementId: elementId,
                    PropertyNames: selectedPropertyNames,
                    IncludeSources: request.IncludeSources,
                    IncludeDefault: false,
                    IncludeUnset: false,
                    MaxProperties: selectedPropertyNames.Length,
                    ValueFormat: request.ValueFormat),
                cancellationToken,
                visibleOnly: true));
        }

        var truncatedReasons = new List<string>(3);
        if (elementIds.Count > selectedElementIds.Length)
        {
            truncatedReasons.Add("maxElements");
        }

        if (propertyNames.Count > selectedPropertyNames.Length)
        {
            truncatedReasons.Add("maxPropertiesPerElement");
        }

        if (results.Any(result => result.Truncated))
        {
            truncatedReasons.Add("elementProperties");
        }

        return new GetComputedPropertiesBatchResponse(
            Results: results,
            RequestedElements: elementIds.Count,
            RequestedProperties: propertyNames.Count,
            ScannedElements: results.Count,
            ReturnedElements: results.Count,
            MaxElements: maxElements,
            MaxPropertiesPerElement: maxPropertiesPerElement,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault(),
            TruncatedReasons: truncatedReasons.Count > 0 ? truncatedReasons : null)
        {
            WindowHandleUsed = windowHandle
        };
    }

    private static IReadOnlyList<string> NormalizeBatchValues(
        IReadOnlyList<string> values,
        string parameterName)
    {
        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                throw AgentToolError.InvalidArgument(
                    "invalid_request",
                    $"invalid_request: {parameterName} cannot contain blank values.",
                    parameterName);
            }

            var value = rawValue.Trim();
            if (!seen.Add(value))
            {
                throw AgentToolError.InvalidArgument(
                    "invalid_request",
                    $"invalid_request: {parameterName} cannot contain duplicates.",
                    parameterName);
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static GetComputedPropertiesResponse GetComputedProperties(
        string ownerId,
        GetComputedPropertiesRequest request,
        CancellationToken cancellationToken,
        bool visibleOnly,
        int maxNodes = 200_000)
    {
        var includeSources = request.IncludeSources;
        var includeDefault = request.IncludeDefault;
        var includeUnset = request.IncludeUnset;
        var includeProvenance = request.IncludeProvenance;
        var requestedMaxProperties = Math.Clamp(request.MaxProperties, 1, 50_000);
        const int maxProvenanceProperties = 100;
        var maxProperties = includeProvenance
            ? Math.Min(requestedMaxProperties, maxProvenanceProperties)
            : requestedMaxProperties;
        var provenancePropertyCapApplied = includeProvenance && requestedMaxProperties > maxProvenanceProperties;
        var maxProvenanceCandidates = Math.Clamp(request.MaxProvenanceCandidates, 0, 50);
        var valueFormat = string.IsNullOrWhiteSpace(request.ValueFormat) ? "string" : request.ValueFormat;
        maxNodes = Math.Clamp(maxNodes, 1, 200_000);

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
            visibleOnly,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = BuildElementRefWpf(ownerId, element, xpath, FindReturnFields.Standard);

        string[]? propertyNames;
        IReadOnlyList<string>? propertyNameTruncatedReasons = null;
        if (includeProvenance && request.PropertyNames is { } requestedPropertyNames)
        {
            var preparedPropertyNames = PrepareProvenancePropertyNames(requestedPropertyNames);
            propertyNames = preparedPropertyNames.Names;
            propertyNameTruncatedReasons = preparedPropertyNames.TruncatedReasons;
        }
        else
        {
            propertyNames = request.PropertyNames?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray();
        }

        var properties = new HashSet<DependencyProperty>(GetDependencyPropertiesCached(element.GetType()));
        var propertyInspectionComplete = true;
        try
        {
            var enumerator = element.GetLocalValueEnumerator();
            while (enumerator.MoveNext())
            {
                properties.Add(enumerator.Current.Property);
            }
        }
        catch
        {
            propertyInspectionComplete = false;
        }

        var computed = new List<ComputedPropertyInfo>();
        var warnings = new List<string>();
        var truncatedReasons = new List<string>(4);
        if (propertyNameTruncatedReasons is not null)
        {
            truncatedReasons.AddRange(propertyNameTruncatedReasons);
        }

        var discoveredProperties = 0;
        var scannedProperties = 0;
        const int maxValueLength = 2000;

        if (propertyNames is { Length: > 0 })
        {
            var missing = new List<string>();
            foreach (var requestedName in propertyNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedProperties++;

                if (!TryResolvePropertyByName(element.GetType(), properties, requestedName, out var dp))
                {
                    missing.Add(requestedName);
                    continue;
                }

                discoveredProperties++;
                if (computed.Count >= maxProperties)
                {
                    continue;
                }

                computed.Add(BuildComputedPropertyInfo(
                    element,
                    dp,
                    valueFormat,
                    includeSources,
                    includeProvenance,
                    maxProvenanceCandidates,
                    maxValueLength));
            }

            if (discoveredProperties > computed.Count)
            {
                truncatedReasons.Add(provenancePropertyCapApplied
                    ? "maxProvenanceProperties"
                    : "maxProperties");
            }

            if (!propertyInspectionComplete)
            {
                truncatedReasons.Add("propertyInspectionUnavailable");
            }

            return new GetComputedPropertiesResponse(
                Element: elementRef,
                Properties: computed,
                Truncated: truncatedReasons.Count > 0,
                TruncatedReason: truncatedReasons.FirstOrDefault(),
                MissingPropertyNames: missing.Count > 0 ? missing : null,
                Warnings: warnings.Count > 0 ? warnings : null)
            {
                WindowHandleUsed = GetWindowHandle(window),
                ReturnedProperties = computed.Count,
                DiscoveredProperties = discoveredProperties,
                ScannedProperties = scannedProperties,
                ScanComplete = propertyNameTruncatedReasons is null && propertyInspectionComplete,
                TruncatedReasons = truncatedReasons.Count > 0 ? truncatedReasons : null
            };
        }

        var orderedProperties = properties.OrderBy(dp => dp.Name, StringComparer.Ordinal).ToArray();

        foreach (var property in orderedProperties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedProperties++;

            ValueSource? valueSource = null;
            try
            {
                valueSource = DependencyPropertyHelper.GetValueSource(element, property);
            }
            catch
            {
                propertyInspectionComplete = false;
                continue;
            }

            var isExpression = valueSource?.IsExpression == true;
            var baseValueSource = valueSource?.BaseValueSource ?? BaseValueSource.Default;

            BindingExpressionBase? bindingExpression = null;
            try
            {
                bindingExpression = BindingOperations.GetBindingExpressionBase(element, property);
            }
            catch
            {
                propertyInspectionComplete = false;
                continue;
            }

            var include = includeDefault || baseValueSource != BaseValueSource.Default || isExpression || bindingExpression is not null;
            if (!include)
            {
                continue;
            }

            if (!includeUnset)
            {
                try
                {
                    var value = element.GetValue(property);
                    if (ReferenceEquals(value, DependencyProperty.UnsetValue))
                    {
                        continue;
                    }
                }
                catch
                {
                    propertyInspectionComplete = false;
                    continue;
                }
            }

            discoveredProperties++;
            if (computed.Count >= maxProperties)
            {
                continue;
            }

            computed.Add(BuildComputedPropertyInfo(
                element,
                property,
                valueFormat,
                includeSources,
                includeProvenance,
                maxProvenanceCandidates,
                maxValueLength));
        }

        if (discoveredProperties > computed.Count)
        {
            truncatedReasons.Add(provenancePropertyCapApplied
                ? "maxProvenanceProperties"
                : "maxProperties");
        }

        if (!propertyInspectionComplete)
        {
            truncatedReasons.Add("propertyInspectionUnavailable");
        }

        return new GetComputedPropertiesResponse(
            Element: elementRef,
            Properties: computed,
            Truncated: truncatedReasons.Count > 0,
            TruncatedReason: truncatedReasons.FirstOrDefault(),
            MissingPropertyNames: null,
            Warnings: warnings.Count > 0 ? warnings : null)
        {
            WindowHandleUsed = GetWindowHandle(window),
            ReturnedProperties = computed.Count,
            DiscoveredProperties = discoveredProperties,
            ScannedProperties = scannedProperties,
            ScanComplete = propertyNameTruncatedReasons is null && propertyInspectionComplete,
            TruncatedReasons = truncatedReasons.Count > 0 ? truncatedReasons : null
        };
    }

    internal readonly record struct PreparedProvenancePropertyNames(
        string[] Names,
        IReadOnlyList<string>? TruncatedReasons)
    {
        public string? TruncatedReason => TruncatedReasons?.FirstOrDefault();
    }

    internal static PreparedProvenancePropertyNames PrepareProvenancePropertyNames(
        IReadOnlyList<string> propertyNames)
    {
        const int maxPropertyNames = 100;
        const int maxPropertyNameLength = 512;

        var count = Math.Min(propertyNames.Count, maxPropertyNames);
        var names = new List<string>(count);
        var propertyNameLengthTruncated = false;
        for (var i = 0; i < count; i++)
        {
            var rawName = propertyNames[i] ?? string.Empty;
            propertyNameLengthTruncated |= rawName.Length > maxPropertyNameLength;
            var name = TruncateProvenanceText(rawName, maxPropertyNameLength).Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        var truncatedReasons = new List<string>(2);
        if (propertyNames.Count > maxPropertyNames)
        {
            truncatedReasons.Add("maxProvenancePropertyNames");
        }

        if (propertyNameLengthTruncated)
        {
            truncatedReasons.Add("maxProvenancePropertyNameLength");
        }

        return new PreparedProvenancePropertyNames(
            names.ToArray(),
            truncatedReasons.Count > 0 ? truncatedReasons : null);
    }

    public static GetStyleChainResponse GetStyleChain(
        string ownerId,
        GetStyleChainRequest request,
        CancellationToken cancellationToken)
    {
        var includeThemeStyle = request.IncludeThemeStyle;
        var includeResourceKeys = request.IncludeResourceKeys;
        var maxBasedOnDepth = Math.Clamp(request.MaxBasedOnDepth, 0, 50);

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
            visibleOnly: true,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = BuildElementRefWpf(ownerId, element, xpath, FindReturnFields.Standard);

        var warnings = new List<string>();
        var styles = new List<StyleChainEntry>();

        if (element is FrameworkElement fe)
        {
            var styleValueSource = TryGetValueSourceForStyle(fe);
            var effectiveStyle = fe.Style;

            if (effectiveStyle is not null)
            {
                var kind = styleValueSource?.BaseValueSource == BaseValueSource.ImplicitStyleReference
                    ? StyleChainKind.ImplicitStyle
                    : StyleChainKind.LocalStyle;

                styles.Add(BuildStyleEntry(kind, fe, effectiveStyle, includeResourceKeys, maxBasedOnDepth, styleValueSource));
            }

            if (includeThemeStyle)
            {
                try
                {
                    var themeStyle = FrameworkElementHelper.GetThemeStyle(fe);
                    if (themeStyle is not null)
                    {
                        styles.Add(BuildStyleEntry(StyleChainKind.ThemeStyle, fe, themeStyle, includeResourceKeys, maxBasedOnDepth, styleValueSource: null));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"theme_style_error: {ex.Message}");
                }
            }
        }
        else if (element is FrameworkContentElement fce)
        {
            ValueSource? valueSource = null;
            try
            {
                valueSource = DependencyPropertyHelper.GetValueSource(fce, FrameworkContentElement.StyleProperty);
            }
            catch
            {
            }

            var effectiveStyle = fce.Style;
            if (effectiveStyle is not null)
            {
                var kind = valueSource?.BaseValueSource == BaseValueSource.ImplicitStyleReference
                    ? StyleChainKind.ImplicitStyle
                    : StyleChainKind.LocalStyle;

                styles.Add(BuildStyleEntry(kind, fce, effectiveStyle, includeResourceKeys, maxBasedOnDepth, valueSource));
            }

            if (includeThemeStyle)
            {
                try
                {
                    var themeStyle = FrameworkElementHelper.GetThemeStyle(fce);
                    if (themeStyle is not null)
                    {
                        styles.Add(BuildStyleEntry(StyleChainKind.ThemeStyle, fce, themeStyle, includeResourceKeys, maxBasedOnDepth, styleValueSource: null));
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"theme_style_error: {ex.Message}");
                }
            }
        }
        else
        {
            warnings.Add("not_framework_element: Style inspection is supported only for FrameworkElement / FrameworkContentElement.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new GetStyleChainResponse(
            Element: elementRef,
            Styles: styles,
            Warnings: warnings.Count > 0 ? warnings : null)
        {
            WindowHandleUsed = GetWindowHandle(window)
        };
    }

    public static GetTemplateInfoResponse GetTemplateInfo(
        string ownerId,
        GetTemplateInfoRequest request,
        CancellationToken cancellationToken)
    {
        var includeNamedElements = request.IncludeNamedElements;
        var includeResourceKeys = request.IncludeResourceKeys;
        var includePartElementRefs = request.IncludePartElementRefs;
        var maxNamedElements = Math.Clamp(request.MaxNamedElements, 0, 500);

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
            visibleOnly: true,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = BuildElementRefWpf(ownerId, element, xpath, FindReturnFields.Standard);

        var warnings = new List<string>();

        TemplateInfo AddNamedElementMetadata(
            TemplateInfo info,
            bool scanComplete,
            NamedTemplateElementDiscovery? discovery = null)
        {
            if (!includeNamedElements)
            {
                return info;
            }

            var returned = discovery?.Elements.Count ?? 0;
            var discovered = discovery?.DiscoveredElements ?? 0;
            return info with
            {
                ReturnedNamedElements = returned,
                DiscoveredNamedElements = discovered,
                NamedElementsScanComplete = discovery?.ScanComplete ?? scanComplete,
                NamedElementsTruncated = discovered > returned,
                MaxNamedElements = maxNamedElements
            };
        }

        if (element is not FrameworkElement fe)
        {
            warnings.Add("not_framework_element: Template inspection is supported only for FrameworkElement.");
            return new GetTemplateInfoResponse(
                Element: elementRef,
                Template: AddNamedElementMetadata(new TemplateInfo(TemplateKind.None), scanComplete: false),
                Warnings: warnings)
            {
                WindowHandleUsed = GetWindowHandle(window)
            };
        }

        FrameworkTemplate? template = null;
        var templateInspectionComplete = true;
        try
        {
            template = FrameworkElementHelper.GetTemplate(fe);
        }
        catch (Exception ex)
        {
            templateInspectionComplete = false;
            warnings.Add($"template_error: {ex.Message}");
        }

        if (template is null)
        {
            return new GetTemplateInfoResponse(
                Element: elementRef,
                Template: AddNamedElementMetadata(
                    new TemplateInfo(TemplateKind.None),
                    scanComplete: templateInspectionComplete),
                Warnings: warnings.Count > 0 ? warnings : null)
            {
                WindowHandleUsed = GetWindowHandle(window)
            };
        }

        var kind = template switch
        {
            System.Windows.Controls.ControlTemplate => TemplateKind.ControlTemplate,
            DataTemplate => TemplateKind.DataTemplate,
            System.Windows.Controls.ItemsPanelTemplate => TemplateKind.ItemsPanelTemplate,
            _ => TemplateKind.FrameworkTemplate
        };

        string? targetType = null;
        int triggersCount = 0;
        try
        {
            switch (template)
            {
                case System.Windows.Controls.ControlTemplate controlTemplate:
                    targetType = controlTemplate.TargetType?.FullName ?? controlTemplate.TargetType?.Name;
                    triggersCount = controlTemplate.Triggers.Count;
                    break;
                case DataTemplate dataTemplate:
                    targetType = dataTemplate.DataType?.ToString();
                    triggersCount = dataTemplate.Triggers.Count;
                    break;
                case System.Windows.Controls.ItemsPanelTemplate:
                    triggersCount = 0;
                    break;
            }
        }
        catch
        {
        }

        string? templateType = null;
        try
        {
            templateType = template.GetType().FullName ?? template.GetType().Name;
        }
        catch
        {
        }

        string? resourceKey = null;
        if (includeResourceKeys)
        {
            resourceKey = TryGetResourceKey(fe, template);
        }

        IReadOnlyList<TemplatePartInfo>? templateParts = null;
        IReadOnlyList<NamedTemplateElementInfo>? namedElements = null;
        NamedTemplateElementDiscovery? namedElementDiscovery = null;
        var namedElementsScanSupported = false;

        if (fe is System.Windows.Controls.Control control && template is System.Windows.Controls.ControlTemplate appliedControlTemplate)
        {
            namedElementsScanSupported = true;
            var namedElementsInspectionFailed = false;
            try
            {
                _ = control.ApplyTemplate();
            }
            catch
            {
                namedElementsInspectionFailed = true;
            }

            templateParts = ResolveTemplateParts(
                control,
                appliedControlTemplate,
                includePartElementRefs,
                window,
                treeService,
                warnings,
                cancellationToken);

            if (includeNamedElements)
            {
                namedElementDiscovery = FindNamedTemplateElements(control, maxNamedElements, cancellationToken);
                if (namedElementsInspectionFailed)
                {
                    namedElementDiscovery = namedElementDiscovery.Value with
                    {
                        ScanComplete = false,
                        InspectionFailed = true
                    };
                }

                namedElements = namedElementDiscovery.Value.Elements;
                if (namedElementDiscovery.Value.InspectionFailed)
                {
                    warnings.Add(
                        "named_elements_scan_incomplete: The template visual tree could not be inspected completely.");
                }
            }
        }
        else if (includeNamedElements)
        {
            warnings.Add("named_elements_unsupported: Named template elements are currently only supported for Control templates.");
        }
        else if (includePartElementRefs)
        {
            warnings.Add("template_part_refs_unsupported: Template part element references are currently only supported for Control templates.");
        }

        var templateInfo = new TemplateInfo(
            Kind: kind,
            TemplateType: templateType,
            TargetType: targetType,
            ResourceKey: resourceKey,
            TriggersCount: triggersCount,
            TemplateParts: templateParts,
            NamedElements: namedElements);
        templateInfo = AddNamedElementMetadata(
            templateInfo,
            scanComplete: namedElementsScanSupported,
            discovery: namedElementDiscovery);

        return new GetTemplateInfoResponse(
            Element: elementRef,
            Template: templateInfo,
            Warnings: warnings.Count > 0 ? warnings : null)
        {
            WindowHandleUsed = GetWindowHandle(window)
        };
    }

    private static ComputedPropertyInfo BuildComputedPropertyInfo(
        DependencyObject element,
        DependencyProperty property,
        string valueFormat,
        bool includeSources,
        bool includeProvenance,
        int maxProvenanceCandidates,
        int maxStringLength)
    {
        var ownerType = includeProvenance
            ? GetTypeName(property.OwnerType) ?? "unknown"
            : property.OwnerType.FullName ?? property.OwnerType.Name;
        string? value = null;
        string? valueType = null;
        ProvenanceEvidence? valueEvidence = null;

        try
        {
            var rawValue = element.GetValue(property);
            if (includeProvenance)
            {
                var formatted = FormatProvenanceValueWithEvidence(
                    rawValue,
                    valueFormat,
                    maxStringLength);
                value = formatted.Value;
                valueEvidence = formatted.Evidence;
            }
            else
            {
                value = FormatValueForBinding(rawValue, valueFormat, maxStringLength);
            }

            if (rawValue is not null && !ReferenceEquals(rawValue, DependencyProperty.UnsetValue))
            {
                valueType = includeProvenance
                    ? GetTypeName(rawValue)
                    : rawValue.GetType().FullName ?? rawValue.GetType().Name;
            }
        }
        catch (Exception valueReadFailure)
        {
            if (includeProvenance)
            {
                valueEvidence = new ProvenanceEvidence(
                    ProvenanceEvidenceKind.Unavailable,
                    BuildFormattingFailureReason(
                        "value_read_failed",
                        valueReadFailure.GetType().FullName ?? valueReadFailure.GetType().Name));
            }
        }

        string? valueSource = null;
        if (includeSources)
        {
            valueSource = GetValueSourceWpf(element, property);
        }

        string? bindingKind = null;
        string? path = null;
        string? mode = null;
        string? updateSourceTrigger = null;
        string? converter = null;
        bool? isBinding = null;

        try
        {
            var expression = BindingOperations.GetBindingExpressionBase(element, property);
            if (expression is not null)
            {
                isBinding = true;
                bindingKind = expression switch
                {
                    BindingExpression => "Binding",
                    MultiBindingExpression => "MultiBinding",
                    PriorityBindingExpression => "PriorityBinding",
                    _ => "Binding"
                };

                if (expression is BindingExpression be)
                {
                    var binding = be.ParentBinding;
                    path = includeProvenance && binding.Path?.Path is { } boundedPath
                        ? TruncateProvenanceText(boundedPath, maxStringLength)
                        : binding.Path?.Path;
                    mode = binding.Mode.ToString();
                    updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
                    converter = includeProvenance
                        ? GetTypeName(binding.Converter)
                        : binding.Converter?.GetType().FullName;
                }
                else if (expression is MultiBindingExpression mbe)
                {
                    var binding = mbe.ParentMultiBinding;
                    bindingKind = "MultiBinding";
                    mode = binding.Mode.ToString();
                    updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
                    converter = includeProvenance
                        ? GetTypeName(binding.Converter)
                        : binding.Converter?.GetType().FullName;
                }
                else if (expression is PriorityBindingExpression)
                {
                    bindingKind = "PriorityBinding";
                }
            }
        }
        catch
        {
        }

        DependencyPropertyProvenance? provenance = null;
        if (includeProvenance)
        {
            try
            {
                provenance = BuildDependencyPropertyProvenance(
                    element,
                    property,
                    valueFormat,
                    maxStringLength,
                    maxProvenanceCandidates);
            }
            catch
            {
                provenance = CreateUnavailableDependencyPropertyProvenance();
            }
        }

        return new ComputedPropertyInfo(
            Name: includeProvenance
                ? TruncateProvenanceText(property.Name, 512)
                : property.Name,
            OwnerType: ownerType,
            Value: value,
            ValueType: valueType,
            ValueSource: valueSource,
            IsBinding: isBinding,
            BindingKind: bindingKind,
            Path: path,
            Mode: mode,
            UpdateSourceTrigger: updateSourceTrigger,
            Converter: converter,
            Provenance: provenance)
        {
            ValueEvidence = valueEvidence
        };
    }

    private static bool TryResolvePropertyByName(
        Type elementType,
        IReadOnlyCollection<DependencyProperty> candidates,
        string requestedName,
        out DependencyProperty dependencyProperty)
    {
        dependencyProperty = null!;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return false;
        }

        var trimmed = requestedName.Trim();
        string? ownerHint = null;
        var propertyName = trimmed;

        var dot = trimmed.LastIndexOf('.');
        if (dot > 0 && dot < trimmed.Length - 1)
        {
            ownerHint = trimmed[..dot];
            propertyName = trimmed[(dot + 1)..];
        }

        var matches = candidates
            .Where(dp => string.Equals(dp.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(ownerHint))
        {
            matches = matches
                .Where(dp =>
                    string.Equals(dp.OwnerType.Name, ownerHint, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dp.OwnerType.FullName, ownerHint, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (matches.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(ownerHint) &&
                TryResolveAttachedDependencyProperty(ownerHint, propertyName, elementType, out var attached))
            {
                dependencyProperty = attached;
                return true;
            }

            return false;
        }

        if (matches.Length == 1)
        {
            dependencyProperty = matches[0];
            return true;
        }

        dependencyProperty = matches
            .OrderBy(dp => GetInheritanceDistance(elementType, dp.OwnerType))
            .ThenBy(dp => dp.OwnerType.FullName ?? dp.OwnerType.Name, StringComparer.Ordinal)
            .First();

        return true;
    }

    private static readonly Dictionary<string, Type?> OwnerTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object OwnerTypeCacheSync = new();

    private static bool TryResolveAttachedDependencyProperty(
        string ownerHint,
        string propertyName,
        Type elementType,
        out DependencyProperty dependencyProperty)
    {
        dependencyProperty = null!;

        Type? ownerType;
        lock (OwnerTypeCacheSync)
        {
            if (!OwnerTypeCache.TryGetValue(ownerHint, out ownerType))
            {
                ownerType = ResolveTypeByName(ownerHint, elementType);
                OwnerTypeCache[ownerHint] = ownerType;
            }
        }

        if (ownerType is null)
        {
            return false;
        }

        var fieldName = propertyName.EndsWith("Property", StringComparison.Ordinal)
            ? propertyName
            : propertyName + "Property";

        try
        {
            var field = ownerType.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (field is null || field.FieldType != typeof(DependencyProperty))
            {
                return false;
            }

            var value = field.GetValue(null) as DependencyProperty;
            if (value is null)
            {
                return false;
            }

            dependencyProperty = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Type? ResolveTypeByName(string hint, Type elementType)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        // Full name fast paths
        try
        {
            var direct = Type.GetType(hint, throwOnError: false, ignoreCase: true);
            if (direct is not null)
            {
                return direct;
            }
        }
        catch
        {
        }

        var assemblies = new[]
        {
            elementType.Assembly,
            typeof(DependencyObject).Assembly,
            typeof(FrameworkElement).Assembly,
            typeof(System.Windows.Media.RenderOptions).Assembly
        }.Distinct().ToArray();

        // If it looks like a full name, prefer Assembly.GetType lookups.
        if (hint.Contains('.', StringComparison.Ordinal))
        {
            foreach (var asm in assemblies)
            {
                try
                {
                    var type = asm.GetType(hint, throwOnError: false, ignoreCase: true);
                    if (type is not null)
                    {
                        return type;
                    }
                }
                catch
                {
                }
            }
        }

        // Fallback: search by short name in a small set of assemblies.
        foreach (var asm in assemblies)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (string.Equals(type.Name, hint, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, hint, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static int GetInheritanceDistance(Type derivedType, Type baseType)
    {
        if (derivedType == baseType)
        {
            return 0;
        }

        if (!baseType.IsAssignableFrom(derivedType))
        {
            return int.MaxValue / 2;
        }

        var distance = 0;
        var current = derivedType;

        while (current != baseType && current.BaseType is not null)
        {
            distance++;
            current = current.BaseType;
        }

        return distance;
    }

    private static ValueSource? TryGetValueSourceForStyle(FrameworkElement element)
    {
        try
        {
            return DependencyPropertyHelper.GetValueSource(element, FrameworkElement.StyleProperty);
        }
        catch
        {
            return null;
        }
    }

    private static StyleChainEntry BuildStyleEntry(
        StyleChainKind kind,
        DependencyObject sourceElement,
        Style style,
        bool includeResourceKeys,
        int maxBasedOnDepth,
        ValueSource? styleValueSource)
    {
        string? targetType = null;
        string? resourceKey = null;
        var basedOn = new List<string>();
        var returnedBasedOnStyles = 0;
        var discoveredBasedOnStyles = 0;
        var basedOnScanComplete = true;
        var settersCount = 0;
        var triggersCount = 0;

        try
        {
            targetType = style.TargetType?.FullName ?? style.TargetType?.Name;
        }
        catch
        {
        }

        if (includeResourceKeys)
        {
            resourceKey = TryGetResourceKey(sourceElement, style);
        }

        try
        {
            settersCount = style.Setters.Count;
            triggersCount = style.Triggers.Count;
        }
        catch
        {
        }

        try
        {
            var current = style.BasedOn;
            while (current is not null && returnedBasedOnStyles < maxBasedOnDepth)
            {
                discoveredBasedOnStyles++;
                returnedBasedOnStyles++;
                var currentTargetType = current.TargetType?.FullName ?? current.TargetType?.Name;
                if (!string.IsNullOrWhiteSpace(currentTargetType))
                {
                    basedOn.Add(currentTargetType);
                }

                current = current.BasedOn;
            }

            if (current is not null)
            {
                discoveredBasedOnStyles++;
                basedOnScanComplete = false;
            }
        }
        catch
        {
            basedOnScanComplete = false;
        }

        string? valueSourceText = null;
        if (styleValueSource is { } vs)
        {
            valueSourceText = vs.BaseValueSource.ToString();
        }

        return new StyleChainEntry
        {
            Kind = kind,
            TargetType = targetType,
            ResourceKey = resourceKey,
            BasedOnChainTargetTypes = basedOn,
            ReturnedBasedOnStyles = returnedBasedOnStyles,
            DiscoveredBasedOnStyles = discoveredBasedOnStyles,
            BasedOnScanComplete = basedOnScanComplete,
            BasedOnTruncated = discoveredBasedOnStyles > returnedBasedOnStyles,
            MaxBasedOnDepth = maxBasedOnDepth,
            SettersCount = settersCount,
            TriggersCount = triggersCount,
            StylePropertyValueSource = valueSourceText
        };
    }

    private static string? TryGetResourceKey(DependencyObject element, object resourceItem)
    {
        try
        {
            var key = ResourceDictionaryKeyHelpers.GetKeyOfResourceItem(element, resourceItem);
            if (ReferenceEquals(key, DependencyProperty.UnsetValue) || key is null)
            {
                return null;
            }

            return FormatResourceKey(key);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatResourceKey(object key)
    {
        if (key is Type type)
        {
            return type.FullName ?? type.Name;
        }

        try
        {
            return key.ToString() ?? key.GetType().FullName ?? key.GetType().Name;
        }
        catch
        {
            return key.GetType().FullName ?? key.GetType().Name;
        }
    }

    private static IReadOnlyList<TemplatePartInfo> ResolveTemplateParts(
        System.Windows.Controls.Control control,
        System.Windows.Controls.ControlTemplate template,
        bool includePartElementRefs,
        Window window,
        VisualTreeService treeService,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var parts = new List<TemplatePartDraft>();
        TemplatePartAttribute[] attributes;
        try
        {
            attributes = control.GetType()
                .GetCustomAttributes(typeof(TemplatePartAttribute), inherit: true)
                .OfType<TemplatePartAttribute>()
                .ToArray();
        }
        catch
        {
            attributes = [];
        }

        foreach (var attr in attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = attr.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? expectedType = null;
            try
            {
                expectedType = attr.Type?.FullName ?? attr.Type?.Name;
            }
            catch
            {
            }

            object? found = null;
            try
            {
                found = template.FindName(name, control);
            }
            catch
            {
            }

            string? actualType = null;
            DependencyObject? foundElement = null;
            if (found is not null)
            {
                try
                {
                    actualType = found.GetType().FullName ?? found.GetType().Name;
                }
                catch
                {
                }

                foundElement = found as DependencyObject;
                if (includePartElementRefs && foundElement is null)
                {
                    warnings.Add($"template_part_not_dependency_object: {name}");
                }
            }

            var partXPath = default(string);
            ContractRect? bounds = null;

            if (includePartElementRefs && foundElement is not null)
            {
                bounds = GetBoundsWpf(foundElement);

                if (TryBuildXPathChainUpwards(treeService, window, foundElement, visibleOnly: true, out var chain) &&
                    chain.Count > 0)
                {
                    partXPath = chain[^1].XPath;
                }
            }

            parts.Add(new TemplatePartDraft(
                name: name,
                expectedType: expectedType,
                found: found is not null,
                actualType: actualType,
                foundElement: foundElement,
                xpath: partXPath,
                bounds: bounds));
        }

        if (includePartElementRefs)
        {
            var unresolved = parts
                .Where(p => p is { Found: true, FoundElement: not null, XPath: null })
                .ToArray();

            if (unresolved.Length > 0)
            {
                var missing = new HashSet<DependencyObject>(unresolved.Select(p => p.FoundElement!), ReferenceEqualityComparer.Instance);

                try
                {
                    foreach (var (element, xpath) in EnumerateDescendantsWithXPath(
                                 root: window,
                                 rootXPath: "/Window",
                                 treeService: treeService,
                                 visibleOnly: true,
                                 includeOffViewport: true,
                                 viewportBounds: null,
                                 maxNodes: 200_000,
                                 cancellationToken: cancellationToken))
                    {
                        if (!missing.Remove(element))
                        {
                            continue;
                        }

                        for (var i = 0; i < parts.Count; i++)
                        {
                            if (ReferenceEquals(parts[i].FoundElement, element))
                            {
                                parts[i].XPath = xpath;
                            }
                        }

                        if (missing.Count == 0)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"template_part_xpath_scan_error: {ex.Message}");
                }

                foreach (var part in parts.Where(p => p is { Found: true, FoundElement: not null, XPath: null }))
                {
                    warnings.Add($"template_part_xpath_unavailable: {part.Name}");
                }
            }
        }

        return parts
            .Select(p => new TemplatePartInfo(
                Name: p.Name,
                ExpectedType: p.ExpectedType,
                Found: p.Found,
                ActualType: p.ActualType,
                XPath: includePartElementRefs ? p.XPath : null,
                Bounds: includePartElementRefs ? p.Bounds : null))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class TemplatePartDraft(
        string name,
        string? expectedType,
        bool found,
        string? actualType,
        DependencyObject? foundElement,
        string? xpath,
        ContractRect? bounds)
    {
        public string Name { get; } = name;
        public string? ExpectedType { get; } = expectedType;
        public bool Found { get; } = found;
        public string? ActualType { get; } = actualType;
        public DependencyObject? FoundElement { get; } = foundElement;
        public string? XPath { get; set; } = xpath;
        public ContractRect? Bounds { get; } = bounds;
    }

    private readonly record struct NamedTemplateElementDiscovery(
        IReadOnlyList<NamedTemplateElementInfo> Elements,
        int DiscoveredElements,
        bool ScanComplete,
        bool InspectionFailed);

    private static NamedTemplateElementDiscovery FindNamedTemplateElements(
        System.Windows.Controls.Control control,
        int maxNamedElements,
        CancellationToken cancellationToken)
    {
        var results = new List<NamedTemplateElementInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var discoveredElements = 0;
        var inspectionFailed = false;

        var stack = new Stack<DependencyObject>();
        stack.Push(control);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = stack.Pop();

            var count = 0;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                inspectionFailed = true;
            }

            for (var i = 0; i < count; i++)
            {
                DependencyObject? child = null;
                try
                {
                    child = VisualTreeHelper.GetChild(current, i);
                }
                catch
                {
                    inspectionFailed = true;
                }

                if (child is null)
                {
                    continue;
                }

                stack.Push(child);

                if (child is FrameworkElement fe &&
                    ReferenceEquals(fe.TemplatedParent, control) &&
                    !string.IsNullOrWhiteSpace(fe.Name) &&
                    seen.Add(fe.Name))
                {
                    discoveredElements++;
                    if (results.Count >= maxNamedElements)
                    {
                        return new NamedTemplateElementDiscovery(
                            results.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
                            discoveredElements,
                            ScanComplete: false,
                            InspectionFailed: inspectionFailed);
                    }

                    var typeName = fe.GetType().FullName ?? fe.GetType().Name;
                    results.Add(new NamedTemplateElementInfo(fe.Name, typeName));
                }
            }
        }

        return new NamedTemplateElementDiscovery(
            results.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            discoveredElements,
            ScanComplete: !inspectionFailed,
            InspectionFailed: inspectionFailed);
    }
}
