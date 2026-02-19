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
            var width = size.Width;
            var height = size.Height;

            // Fall back to ActualWidth/ActualHeight when available.
            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                width = fe.ActualWidth;
                height = fe.ActualHeight;
            }

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
        ElementLocator locator,
        string strategyName)
    {
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"wpf_resolve:not_found: Locator strategy '{strategyName}' did not match any elements.");
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

            if (index >= matches.Count)
            {
                throw new InvalidOperationException(
                    $"wpf_resolve:not_found: Locator strategy '{strategyName}' index {index} is out of range (found {matches.Count}).");
            }

            return matches[index];
        }

        throw new InvalidOperationException(
            $"wpf_resolve:ambiguous: Locator strategy '{strategyName}' is ambiguous (found {matches.Count}). Provide 'index' to disambiguate.");
    }

    private static (DependencyObject Element, string XPath) ResolveLocatorOrThrow(
        Window window,
        VisualTreeService treeService,
        DependencyObject rootObject,
        string rootXPath,
        ElementLocator locator,
        bool visibleOnly,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(locator.XPath))
        {
            var normalized = NormalizeXPath(locator.XPath);
            try
            {
                var element = ResolveByXPath(treeService, window, normalized, visibleOnly, cancellationToken);
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
            .ToArray();

        if (!string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            var matches = descendants
                .Where(e => string.Equals(GetAutomationId(e.Element), locator.AutomationId, StringComparison.Ordinal))
                .ToArray();

            return SelectMatchForLocator(matches, locator, "automationId");
        }

        if (!string.IsNullOrWhiteSpace(locator.Name))
        {
            var matches = descendants
                .Where(e => string.Equals(GetName(e.Element), locator.Name, StringComparison.Ordinal))
                .ToArray();

            return SelectMatchForLocator(matches, locator, "name");
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName))
        {
            var matches = descendants
                .Where(e => string.Equals(e.Element.GetType().FullName, locator.ClassName, StringComparison.Ordinal))
                .ToArray();

            return SelectMatchForLocator(matches, locator, "className");
        }

        if (locator.Index is int index &&
            string.IsNullOrWhiteSpace(locator.AutomationId) &&
            string.IsNullOrWhiteSpace(locator.Name) &&
            string.IsNullOrWhiteSpace(locator.ClassName))
        {
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

        throw new ArgumentException("Locator must specify one of: xpath, automationId, name, className, or index.");
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
            maxNodes,
            cancellationToken);

        return BuildElementRefWpf(resolved.Element, resolved.XPath, request.ReturnFields);
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
}
