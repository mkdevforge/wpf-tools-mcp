using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Snoop.Data.Tree;
using Snoop.Infrastructure.Helpers;
using WpfPilot.Contracts;
using ContractRect = WpfPilot.Contracts.Rect;

namespace WpfPilot.Agent;

internal static class WpfVisualTreeInspector
{
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
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        public VisualTreeService TreeService { get; } = treeService;
        public WpfTreeFieldSet FieldSet { get; } = fieldSet;
        public int MaxNodes { get; } = maxNodes;
        public bool VisibleOnly { get; } = visibleOnly;
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

        if (!isRoot && context.VisibleOnly && !IsVisibleWpf(element))
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

        var rawChildren = GetChildrenWpf(element, context.TreeService, context.VisibleOnly);
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

    private static DependencyObject[] GetChildrenWpf(DependencyObject parent, VisualTreeService treeService, bool visibleOnly)
    {
        var children = treeService.GetChildren(parent)
            .OfType<DependencyObject>()
            .ToArray();

        if (visibleOnly)
        {
            children = children.Where(IsVisibleWpf).ToArray();
        }

        return children;
    }

    private static string GetXPathLabel(DependencyObject element) =>
        element is Window ? "Window" : element.GetType().Name;

    private sealed record XPathSegment(string TypeName, int? OneBasedIndex);

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
            var rawChildren = GetChildrenWpf(current, treeService, visibleOnly);
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
                    $"XPath segment '{segment.TypeName}' is ambiguous (found {matching.Length}). Provide an index.");
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

    private static Window ResolveWindow(long? windowHandle)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("Application.Current is null. Is the target a WPF application?");

        if (windowHandle is long requestedHwnd)
        {
            foreach (Window window in application.Windows)
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero && hwnd.ToInt64() == requestedHwnd)
                {
                    return window;
                }
            }
        }

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
            var name = GetName(element);
            if (!string.Equals(name, query.NameEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.NameContains))
        {
            var name = GetName(element) ?? string.Empty;
            if (name.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static ElementRef BuildElementRefWpf(DependencyObject element, string xpath, FindReturnFields returnFields)
    {
        if (returnFields == FindReturnFields.Standard)
        {
            return new ElementRef(
                Type: element.GetType().Name,
                AutomationId: GetAutomationId(element),
                Name: GetName(element),
                XPath: xpath,
                ClassName: element.GetType().FullName,
                Bounds: GetBoundsWpf(element));
        }

        return new ElementRef(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath);
    }

    public static GetVisualTreeResponse GetVisualTree(GetWpfVisualTreeRequestV2 request, CancellationToken cancellationToken)
    {
        var depth = request.Depth <= 0 ? 1 : request.Depth;
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 5000);

        var window = ResolveWindow(request.WindowHandle);
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
            Warnings: null);
    }

    public static FindElementsResponse FindElements(FindElementsWpfRequest request, CancellationToken cancellationToken)
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
        using var treeService = new VisualTreeService();

        var rootObject = (DependencyObject)window;
        var rootXPath = "/Window";

        if (!string.IsNullOrWhiteSpace(request.RootXPath))
        {
            rootXPath = NormalizeXPath(request.RootXPath);
            rootObject = ResolveByXPath(treeService, window, rootXPath, request.VisibleOnly, cancellationToken);
        }

        var matches = new List<ElementRef>();
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

            if (!ReferenceEquals(current, rootObject) && request.VisibleOnly && !IsVisibleWpf(current))
            {
                continue;
            }

            if (IsQueryMatchWpf(current, query) &&
                (!request.InteractiveOnly || IsInteractiveWpf(current, request.InteractiveMode)))
            {
                matches.Add(BuildElementRefWpf(current, currentXPath, request.ReturnFields));
                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    truncatedReason = "maxResults";
                    break;
                }
            }

            var rawChildren = GetChildrenWpf(current, treeService, request.VisibleOnly);
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
            Warnings: null);
    }

    private static IEnumerable<(DependencyObject Element, string XPath)> EnumerateDescendantsWithXPath(
        DependencyObject root,
        string rootXPath,
        VisualTreeService treeService,
        bool visibleOnly,
        int maxNodes,
        CancellationToken cancellationToken)
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

            if (!ReferenceEquals(current, root) && visibleOnly && !IsVisibleWpf(current))
            {
                continue;
            }

            yield return (current, currentXPath);

            var rawChildren = GetChildrenWpf(current, treeService, visibleOnly);
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
            throw new InvalidOperationException("wpf_resolve:not_found: Locator did not match any elements.");
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (locator.Index is int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            var ordered = OrderMatchesForLocator(matches, locator);
            if (index >= ordered.Count)
            {
                throw new InvalidOperationException(
                    $"wpf_resolve:not_found: Locator index {index} is out of range (found {ordered.Count}).");
            }

            return ordered[index];
        }

        if (locator.Strict)
        {
            throw new InvalidOperationException(
                $"wpf_resolve:ambiguous: Locator is ambiguous (found {matches.Count}). Provide 'index' to disambiguate.");
        }

        var orderedDefault = OrderMatchesForLocator(matches, locator);
        return orderedDefault.Count > 0 ? orderedDefault[0] : matches[0];
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

            return string.Compare(GetName(a.Element), GetName(b.Element), StringComparison.Ordinal);
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
                return (element, normalized);
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"wpf_resolve:ambiguous: {ex.Message}");
                }

                throw new InvalidOperationException($"wpf_resolve:not_found: {ex.Message}");
            }
        }

        var descendants = EnumerateDescendantsWithXPath(rootObject, rootXPath, treeService, visibleOnly, maxNodes, cancellationToken)
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
                throw new InvalidOperationException($"wpf_resolve:not_found: Locator index {index} is out of range (found {descendants.Length}).");
            }

            return descendants[index];
        }

        var matches = descendants
            .Where(e => MatchesLocatorWpf(e.Element, locator))
            .ToArray();

        return SelectMatchForLocator(matches, locator);
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
            !string.Equals(GetName(element), locator.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.NameContains))
        {
            var expected = locator.NameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetName(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
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

    public static GetPathToElementResponse GetPath(GetWpfPathRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("get_path requires a locator.");
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

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject,
            rootXPath,
            locator,
            request.VisibleOnly,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes,
            cancellationToken);

        return new GetPathToElementResponse(InspectionBackend.Wpf, resolved.XPath);
    }

    public static ElementRef ResolveElement(ResolveWpfElementRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("resolve_element requires a locator.");
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

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject,
            rootXPath,
            locator,
            request.VisibleOnly,
            request.InteractiveOnly,
            request.InteractiveMode,
            maxNodes,
            cancellationToken);

        return BuildElementRefWpf(resolved.Element, resolved.XPath, request.ReturnFields);
    }

    public static PickWpfElementAtPointResponse PickElementAtPoint(PickWpfElementAtPointRequest request, CancellationToken cancellationToken)
    {
        var maxAncestors = Math.Clamp(request.MaxAncestors, 0, 50);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var hit = PickWpfDependencyObjectAtPoint(window, request.X, request.Y);
        hit = PromotePickedWpfElement(hit, window);

        var chain = BuildXPathChainForElement(treeService, window, hit, visibleOnly: true, maxNodes: 200_000, cancellationToken);
        var (pickedElement, pickedXPath) = chain[^1];

        var elementRef = BuildElementRefWpf(pickedElement, pickedXPath, request.ReturnFields);

        IReadOnlyList<ElementRef>? ancestors = null;
        if (request.IncludeAncestors)
        {
            var candidateAncestors = chain.Take(Math.Max(0, chain.Count - 1)).ToArray();
            if (maxAncestors > 0 && candidateAncestors.Length > maxAncestors)
            {
                candidateAncestors = candidateAncestors[^maxAncestors..];
            }

            ancestors = candidateAncestors
                .Select(a => BuildElementRefWpf(a.Element, a.XPath, FindReturnFields.Minimal))
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
        foreach (var item in EnumerateDescendantsWithXPath(root, rootXPath, treeService, visibleOnly, maxNodes, cancellationToken))
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

            var rawChildren = GetChildrenWpf(parent, treeService, visibleOnly);
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
        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
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
                    if (error.ErrorContent is not null)
                    {
                        return error.ErrorContent.ToString();
                    }

                    if (error.Exception is not null)
                    {
                        return error.Exception.Message;
                    }

                    return "Validation error";
                }
            }

            if (expression.HasError)
            {
                var first = errors[0];
                if (first.ErrorContent is not null)
                {
                    return first.ErrorContent.ToString();
                }

                if (first.Exception is not null)
                {
                    return first.Exception.Message;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static GetBindingInfoResponse GetBindingInfo(GetBindingInfoRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("get_binding_info requires a locator.");
        var maxProperties = Math.Clamp(request.MaxProperties, 1, 50_000);
        var valueFormat = string.IsNullOrWhiteSpace(request.ValueFormat) ? "string" : request.ValueFormat;

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            locator,
            visibleOnly: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = new ElementRef(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ClassName: element.GetType().FullName,
            Bounds: GetBoundsWpf(element));

        var properties = new HashSet<DependencyProperty>(GetDependencyPropertiesCached(element.GetType()));
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
        }

        var orderedProperties = properties.OrderBy(dp => dp.Name, StringComparer.Ordinal).ToArray();

        var bindings = new List<BindingInfo>();
        var truncated = false;
        string? truncatedReason = null;

        const int maxValueLength = 2000;

        foreach (var property in orderedProperties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bindings.Count >= maxProperties)
            {
                truncated = true;
                truncatedReason = "maxProperties";
                break;
            }

            BindingExpressionBase? expression = null;
            try
            {
                expression = BindingOperations.GetBindingExpressionBase(element, property);
            }
            catch
            {
            }

            if (expression is null)
            {
                if (!request.IncludeUnbound)
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

        return new GetBindingInfoResponse(
            Element: elementRef,
            Bindings: bindings,
            Truncated: truncated,
            TruncatedReason: truncatedReason);
    }

    private static void CollectBindingErrorsForElement(
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
            return;
        }

        while (localValues.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (errors.Count >= maxErrors)
            {
                return;
            }

            var property = localValues.Current.Property;
            BindingExpressionBase? expression = null;
            try
            {
                expression = BindingOperations.GetBindingExpressionBase(element, property);
            }
            catch
            {
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
            }

            if (!shouldReport)
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
    }

    public static GetBindingErrorsResponse GetBindingErrors(GetBindingErrorsRequest request, CancellationToken cancellationToken)
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
        var truncated = false;
        string? truncatedReason = null;

        var stack = new Stack<(DependencyObject Element, string XPath, int RemainingDepth)>();
        stack.Push((rootObject, rootXPath, depth));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                truncated = true;
                truncatedReason = "maxNodes";
                break;
            }

            var (current, currentXPath, remainingDepth) = stack.Pop();
            scannedNodes++;

            if (!ReferenceEquals(current, rootObject) && visibleOnly && !IsVisibleWpf(current))
            {
                continue;
            }

            CollectBindingErrorsForElement(current, currentXPath, errors, maxErrors, cancellationToken);
            if (errors.Count >= maxErrors)
            {
                truncated = true;
                truncatedReason = "maxErrors";
                break;
            }

            if (remainingDepth <= 1)
            {
                continue;
            }

            var rawChildren = GetChildrenWpf(current, treeService, visibleOnly);
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

        return new GetBindingErrorsResponse(
            Errors: errors,
            ScannedNodes: scannedNodes,
            Truncated: truncated,
            TruncatedReason: truncatedReason);
    }

    public static GetUiaCoverageReportResponse GetUiaCoverageReport(GetUiaCoverageReportRequest request, CancellationToken cancellationToken)
    {
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 200_000);
        var maxFindings = Math.Clamp(request.MaxFindings, 1, 5000);
        var visibleOnly = request.VisibleOnly;
        var interactiveOnly = request.InteractiveOnly;
        var interactiveMode = request.InteractiveMode;

        var window = ResolveWindow(request.WindowHandle);
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

        var scannedNodes = 0;
        var consideredNodes = 0;
        var truncated = false;
        string? truncatedReason = null;

        try
        {
            foreach (var (element, xpath) in EnumerateDescendantsWithXPath(
                         root: rootObject,
                         rootXPath: rootXPath,
                         treeService: treeService,
                         visibleOnly: visibleOnly,
                         maxNodes: maxNodes,
                         cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                scannedNodes++;

                var isInteractive = IsInteractiveWpf(element, interactiveMode);
                if (interactiveOnly && !isInteractive)
                {
                    continue;
                }

                consideredNodes++;

                var elementRef = new ElementRef(
                    Type: element.GetType().Name,
                    AutomationId: GetAutomationId(element),
                    Name: GetName(element),
                    XPath: xpath,
                    ClassName: element.GetType().FullName,
                    Bounds: GetBoundsWpf(element));

                var elementFindings = AnalyzeCoverageForElement(element, elementRef, isInteractive);
                foreach (var finding in elementFindings)
                {
                    findings.Add(finding);
                    if (findings.Count >= maxFindings)
                    {
                        truncated = true;
                        truncatedReason = "maxFindings";
                        break;
                    }
                }

                if (truncated)
                {
                    break;
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Search exceeded maxNodes=", StringComparison.OrdinalIgnoreCase))
        {
            truncated = true;
            truncatedReason = "maxNodes";
            warnings.Add(ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var ordered = findings
            .OrderByDescending(f => GetSeverityRank(f.Severity))
            .ThenBy(f => f.Element.XPath, StringComparer.Ordinal)
            .ToArray();

        var summary = new UiaCoverageSummary(
            ScannedNodes: scannedNodes,
            ConsideredNodes: consideredNodes,
            FindingsCount: ordered.Length,
            Truncated: truncated,
            TruncatedReason: truncatedReason);

        return new GetUiaCoverageReportResponse(
            Summary: summary,
            Findings: ordered,
            Warnings: warnings.Count > 0 ? warnings : null);
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
                var severity = isInteractive ? "error" : "info";
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
        var hasAutomationId = !string.IsNullOrWhiteSpace(elementRef.AutomationId);

        if (isInteractive && !HasAnyActionablePattern(peer))
        {
            findings.Add(new UiaCoverageFinding(
                IssueCode: "no_actionable_patterns",
                Severity: "warning",
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

        if (isInteractive && !hasPeerName && !hasAutomationPropertiesName && !hasAutomationId)
        {
            findings.Add(new UiaCoverageFinding(
                IssueCode: "missing_accessible_name",
                Severity: "warning",
                Element: elementRef,
                Details:
                [
                    "Element has no accessible name (UIA Name / AutomationProperties.Name) and no AutomationId.",
                    "peer_name_empty",
                    "automation_properties_name_empty",
                    "automation_id_empty"
                ],
                Suggestions:
                [
                    "Set AutomationProperties.Name to an accessible, user-facing label.",
                    "Optionally set AutomationProperties.AutomationId to a stable identifier for automation."
                ]));
        }

        return findings;
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
        bool IncludeNulls);

    private static JsonNode? SerializeDataContextValue(
        object? value,
        DataContextSerializationOptions options,
        int remainingDepth,
        HashSet<object> visited,
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
            return JsonValue.Create(TruncateString(s, options.MaxStringLength));
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return JsonValue.Create(value);
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
            var count = 0;

            foreach (DictionaryEntry entry in dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (count >= options.MaxPropertiesPerObject)
                {
                    break;
                }

                var key = entry.Key?.ToString() ?? "null";
                var node = SerializeDataContextValue(entry.Value, options, remainingDepth - 1, visited, cancellationToken);
                if (node is null && !options.IncludeNulls)
                {
                    continue;
                }

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
                    array.Add(JsonValue.Create("<truncated>"));
                    break;
                }

                var node = SerializeDataContextValue(item, options, remainingDepth - 1, visited, cancellationToken);
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

        var valueType = value.GetType();
        var properties = valueType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Take(options.MaxPropertiesPerObject)
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
                json[property.Name] = JsonValue.Create("<error>");
                continue;
            }

            var node = SerializeDataContextValue(propertyValue, options, remainingDepth - 1, visited, cancellationToken);
            if (node is null && !options.IncludeNulls)
            {
                continue;
            }

            json[property.Name] = node;
        }

        return json;
    }

    public static GetDataContextResponse GetDataContext(GetDataContextRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("get_data_context requires a locator.");
        var maxDepth = Math.Clamp(request.MaxDepth, 0, 25);
        var maxPropertiesPerObject = Math.Clamp(request.MaxPropertiesPerObject, 1, 5000);
        var maxStringLength = Math.Clamp(request.MaxStringLength, 0, 200_000);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            locator,
            visibleOnly: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

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
            return new GetDataContextResponse(DataContextType: null, Data: null);
        }

        var dataContextType = dataContext.GetType().FullName ?? dataContext.GetType().Name;
        var options = new DataContextSerializationOptions(
            MaxDepth: maxDepth,
            MaxPropertiesPerObject: maxPropertiesPerObject,
            MaxStringLength: maxStringLength,
            IncludeNulls: request.IncludeNulls);

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var data = SerializeDataContextValue(dataContext, options, maxDepth, visited, cancellationToken);

        return new GetDataContextResponse(
            DataContextType: dataContextType,
            Data: data);
    }

    public static GetComputedPropertiesResponse GetComputedProperties(GetComputedPropertiesRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("get_computed_properties requires a locator.");
        var includeSources = request.IncludeSources;
        var includeDefault = request.IncludeDefault;
        var includeUnset = request.IncludeUnset;
        var maxProperties = Math.Clamp(request.MaxProperties, 1, 50_000);
        var valueFormat = string.IsNullOrWhiteSpace(request.ValueFormat) ? "string" : request.ValueFormat;

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            locator,
            visibleOnly: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = new ElementRef(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ClassName: element.GetType().FullName,
            Bounds: GetBoundsWpf(element));

        var propertyNames = request.PropertyNames?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();

        var properties = new HashSet<DependencyProperty>(GetDependencyPropertiesCached(element.GetType()));
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
        }

        var computed = new List<ComputedPropertyInfo>();
        var warnings = new List<string>();
        var truncated = false;
        string? truncatedReason = null;

        const int maxValueLength = 2000;

        if (propertyNames is { Length: > 0 })
        {
            var missing = new List<string>();
            foreach (var requestedName in propertyNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (computed.Count >= maxProperties)
                {
                    truncated = true;
                    truncatedReason = "maxProperties";
                    break;
                }

                if (!TryResolvePropertyByName(element.GetType(), properties, requestedName, out var dp))
                {
                    missing.Add(requestedName);
                    continue;
                }

                computed.Add(BuildComputedPropertyInfo(element, dp, valueFormat, includeSources, maxValueLength));
            }

            return new GetComputedPropertiesResponse(
                Element: elementRef,
                Properties: computed,
                Truncated: truncated,
                TruncatedReason: truncatedReason,
                MissingPropertyNames: missing.Count > 0 ? missing : null,
                Warnings: warnings.Count > 0 ? warnings : null);
        }

        var orderedProperties = properties.OrderBy(dp => dp.Name, StringComparer.Ordinal).ToArray();

        foreach (var property in orderedProperties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (computed.Count >= maxProperties)
            {
                truncated = true;
                truncatedReason = "maxProperties";
                break;
            }

            ValueSource? valueSource = null;
            try
            {
                valueSource = DependencyPropertyHelper.GetValueSource(element, property);
            }
            catch
            {
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
                }
            }

            computed.Add(BuildComputedPropertyInfo(element, property, valueFormat, includeSources, maxValueLength));
        }

        return new GetComputedPropertiesResponse(
            Element: elementRef,
            Properties: computed,
            Truncated: truncated,
            TruncatedReason: truncatedReason,
            MissingPropertyNames: null,
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    public static GetStyleChainResponse GetStyleChain(GetStyleChainRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("get_style_chain requires a locator.");
        var includeThemeStyle = request.IncludeThemeStyle;
        var includeResourceKeys = request.IncludeResourceKeys;
        var maxBasedOnDepth = Math.Clamp(request.MaxBasedOnDepth, 0, 50);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            locator,
            visibleOnly: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = new ElementRef(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ClassName: element.GetType().FullName,
            Bounds: GetBoundsWpf(element));

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
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    public static GetTemplateInfoResponse GetTemplateInfo(GetTemplateInfoRequest request, CancellationToken cancellationToken)
    {
        var locator = request.Locator ?? throw new ArgumentException("get_template_info requires a locator.");
        var includeNamedElements = request.IncludeNamedElements;
        var includeResourceKeys = request.IncludeResourceKeys;
        var includePartElementRefs = request.IncludePartElementRefs;
        var maxNamedElements = Math.Clamp(request.MaxNamedElements, 0, 500);

        var window = ResolveWindow(request.WindowHandle);
        using var treeService = new VisualTreeService();

        var resolved = ResolveLocatorOrThrow(
            window,
            treeService,
            rootObject: window,
            rootXPath: "/Window",
            locator,
            visibleOnly: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            maxNodes: 200_000,
            cancellationToken);

        var element = resolved.Element;
        var xpath = resolved.XPath;

        var elementRef = new ElementRef(
            Type: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ClassName: element.GetType().FullName,
            Bounds: GetBoundsWpf(element));

        var warnings = new List<string>();

        if (element is not FrameworkElement fe)
        {
            warnings.Add("not_framework_element: Template inspection is supported only for FrameworkElement.");
            return new GetTemplateInfoResponse(
                Element: elementRef,
                Template: new TemplateInfo(TemplateKind.None),
                Warnings: warnings);
        }

        FrameworkTemplate? template = null;
        try
        {
            template = FrameworkElementHelper.GetTemplate(fe);
        }
        catch (Exception ex)
        {
            warnings.Add($"template_error: {ex.Message}");
        }

        if (template is null)
        {
            return new GetTemplateInfoResponse(
                Element: elementRef,
                Template: new TemplateInfo(TemplateKind.None),
                Warnings: warnings.Count > 0 ? warnings : null);
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

        if (fe is System.Windows.Controls.Control control && template is System.Windows.Controls.ControlTemplate appliedControlTemplate)
        {
            try
            {
                _ = control.ApplyTemplate();
            }
            catch
            {
            }

            templateParts = ResolveTemplateParts(
                control,
                appliedControlTemplate,
                includePartElementRefs,
                window,
                treeService,
                warnings,
                cancellationToken);

            if (includeNamedElements && maxNamedElements > 0)
            {
                namedElements = FindNamedTemplateElements(control, maxNamedElements, cancellationToken);
            }
        }
        else if (includeNamedElements && maxNamedElements > 0)
        {
            warnings.Add("named_elements_unsupported: Named template elements are currently only supported for Control templates.");
        }
        else if (includePartElementRefs)
        {
            warnings.Add("template_part_refs_unsupported: Template part element references are currently only supported for Control templates.");
        }

        return new GetTemplateInfoResponse(
            Element: elementRef,
            Template: new TemplateInfo(
                Kind: kind,
                TemplateType: templateType,
                TargetType: targetType,
                ResourceKey: resourceKey,
                TriggersCount: triggersCount,
                TemplateParts: templateParts,
                NamedElements: namedElements),
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    private static ComputedPropertyInfo BuildComputedPropertyInfo(
        DependencyObject element,
        DependencyProperty property,
        string valueFormat,
        bool includeSources,
        int maxStringLength)
    {
        var ownerType = property.OwnerType.FullName ?? property.OwnerType.Name;
        string? value = null;
        string? valueType = null;

        try
        {
            var rawValue = element.GetValue(property);
            value = FormatValueForBinding(rawValue, valueFormat, maxStringLength);

            if (rawValue is not null && !ReferenceEquals(rawValue, DependencyProperty.UnsetValue))
            {
                valueType = rawValue.GetType().FullName ?? rawValue.GetType().Name;
            }
        }
        catch
        {
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
                    path = binding.Path?.Path;
                    mode = binding.Mode.ToString();
                    updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
                    converter = binding.Converter?.GetType().FullName;
                }
                else if (expression is MultiBindingExpression mbe)
                {
                    var binding = mbe.ParentMultiBinding;
                    bindingKind = "MultiBinding";
                    mode = binding.Mode.ToString();
                    updateSourceTrigger = binding.UpdateSourceTrigger.ToString();
                    converter = binding.Converter?.GetType().FullName;
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

        return new ComputedPropertyInfo(
            Name: property.Name,
            OwnerType: ownerType,
            Value: value,
            ValueType: valueType,
            ValueSource: valueSource,
            IsBinding: isBinding,
            BindingKind: bindingKind,
            Path: path,
            Mode: mode,
            UpdateSourceTrigger: updateSourceTrigger,
            Converter: converter);
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
            var safety = 0;
            while (current is not null && safety++ < maxBasedOnDepth)
            {
                var currentTargetType = current.TargetType?.FullName ?? current.TargetType?.Name;
                if (!string.IsNullOrWhiteSpace(currentTargetType))
                {
                    basedOn.Add(currentTargetType);
                }

                current = current.BasedOn;
            }
        }
        catch
        {
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

    private static IReadOnlyList<NamedTemplateElementInfo> FindNamedTemplateElements(
        System.Windows.Controls.Control control,
        int maxNamedElements,
        CancellationToken cancellationToken)
    {
        var results = new List<NamedTemplateElementInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
                    var typeName = fe.GetType().FullName ?? fe.GetType().Name;
                    results.Add(new NamedTemplateElementInfo(fe.Name, typeName));
                    if (results.Count >= maxNamedElements)
                    {
                        return results
                            .OrderBy(n => n.Name, StringComparer.Ordinal)
                            .ToArray();
                    }
                }
            }
        }

        return results
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
