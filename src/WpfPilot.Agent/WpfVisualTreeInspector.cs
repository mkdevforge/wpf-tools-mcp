using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using Snoop.Data.Tree;
using WpfPilot.Contracts;

namespace WpfPilot.Agent;

internal static class WpfVisualTreeInspector
{
    public static GetWpfVisualTreeResponse GetVisualTree(GetWpfVisualTreeRequest request, CancellationToken cancellationToken)
    {
        var depth = request.Depth <= 0 ? 1 : request.Depth;
        var root = ResolveRoot(request.WindowHandle);

        using var treeService = new VisualTreeService();
        var rootTypeName = root.GetType().Name;
        var rootXPath = $"/{rootTypeName}[1]";
        var rootNode = BuildNode(treeService, root, rootXPath, depth, cancellationToken);

        return new GetWpfVisualTreeResponse(rootNode);
    }

    private static DependencyObject ResolveRoot(long? windowHandle)
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

    private static WpfVisualTreeNode BuildNode(
        VisualTreeService treeService,
        DependencyObject target,
        string xpath,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var type = target.GetType().FullName ?? target.GetType().Name;
        var name = GetName(target);
        var automationId = GetAutomationId(target);
        var visibility = target is UIElement ui ? ui.Visibility.ToString() : null;
        var dataContextType = GetDataContextType(target);

        var children = Array.Empty<WpfVisualTreeNode>();
        if (depth > 1)
        {
            children = BuildChildren(treeService, target, xpath, depth - 1, cancellationToken);
        }

        return new WpfVisualTreeNode(
            Type: type,
            Name: name,
            AutomationId: automationId,
            Visibility: visibility,
            DataContextType: dataContextType,
            XPath: xpath,
            Children: children);
    }

    private static WpfVisualTreeNode[] BuildChildren(
        VisualTreeService treeService,
        DependencyObject parent,
        string parentXPath,
        int depth,
        CancellationToken cancellationToken)
    {
        var results = new List<WpfVisualTreeNode>();
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var childObj in treeService.GetChildren(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (childObj is not DependencyObject child)
            {
                continue;
            }

            var segmentType = child.GetType().Name;
            var index = typeCounts.TryGetValue(segmentType, out var count) ? count + 1 : 1;
            typeCounts[segmentType] = index;

            var childXPath = $"{parentXPath}/{segmentType}[{index}]";
            results.Add(BuildNode(treeService, child, childXPath, depth, cancellationToken));
        }

        return results.ToArray();
    }

    private static string? GetName(DependencyObject target)
    {
        return target switch
        {
            FrameworkElement fe => string.IsNullOrWhiteSpace(fe.Name) ? null : fe.Name,
            FrameworkContentElement fce => string.IsNullOrWhiteSpace(fce.Name) ? null : fce.Name,
            _ => null
        };
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
}
