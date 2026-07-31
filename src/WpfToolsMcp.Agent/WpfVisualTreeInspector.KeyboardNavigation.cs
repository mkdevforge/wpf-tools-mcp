using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Snoop.Data.Tree;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static partial class WpfVisualTreeInspector
{
    public static WpfKeyboardNavigationStepResponse TraceKeyboardNavigationStep(
        string ownerId,
        WpfKeyboardNavigationStepRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(request);

        var window = ResolveWindow(request.WindowHandle);
        var focusedBefore = Keyboard.FocusedElement as DependencyObject;
        if (!IsFocusWithinWindow(focusedBefore, window))
        {
            return new WpfKeyboardNavigationStepResponse(
                Focus: null,
                Metadata: null,
                MoveAttempted: request.Move,
                MoveAccepted: false,
                InteropBoundary: true);
        }

        var moveAccepted = false;
        if (request.Move)
        {
            var traversalDirection = request.Direction switch
            {
                KeyboardNavigationDirection.Next => FocusNavigationDirection.Next,
                KeyboardNavigationDirection.Previous => FocusNavigationDirection.Previous,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Direction,
                    "Unknown keyboard navigation direction.")
            };

            var traversalRequest = new TraversalRequest(traversalDirection);
            moveAccepted = focusedBefore switch
            {
                UIElement uiElement => uiElement.MoveFocus(traversalRequest),
                ContentElement contentElement => contentElement.MoveFocus(traversalRequest),
                UIElement3D uiElement3D => uiElement3D.MoveFocus(traversalRequest),
                _ => false
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var focusedAfter = Keyboard.FocusedElement as DependencyObject;
        if (!IsFocusWithinWindow(focusedAfter, window))
        {
            return new WpfKeyboardNavigationStepResponse(
                Focus: null,
                Metadata: null,
                MoveAttempted: request.Move,
                MoveAccepted: moveAccepted,
                InteropBoundary: true);
        }

        using var treeService = new VisualTreeService();
        var chain = BuildKeyboardNavigationFocusChain(
            treeService,
            window,
            focusedAfter!,
            cancellationToken);
        var focusEntry = chain.LastOrDefault(entry => ReferenceEquals(entry.Element, focusedAfter));
        if (focusEntry.Element is null)
        {
            return new WpfKeyboardNavigationStepResponse(
                Focus: null,
                Metadata: null,
                MoveAttempted: request.Move,
                MoveAccepted: moveAccepted,
                InteropBoundary: true);
        }

        var element = BuildElementRefWpf(
            ownerId,
            focusedAfter!,
            focusEntry.XPath,
            FindReturnFields.Standard);
        var metadata = BuildKeyboardNavigationMetadata(focusedAfter!, chain);

        return new WpfKeyboardNavigationStepResponse(
            element,
            metadata,
            MoveAttempted: request.Move,
            MoveAccepted: moveAccepted,
            InteropBoundary: false);
    }

    private static bool IsFocusWithinWindow(DependencyObject? element, Window window) =>
        element is not null && ReferenceEquals(GetContainingWindow(element), window);

    private static IReadOnlyList<(DependencyObject Element, string XPath)> BuildKeyboardNavigationFocusChain(
        VisualTreeService treeService,
        Window window,
        DependencyObject element,
        CancellationToken cancellationToken)
    {
        try
        {
            return BuildXPathChainForElement(
                treeService,
                window,
                element,
                visibleOnly: false,
                maxNodes: 8_000,
                cancellationToken);
        }
        catch (Exception) when (element is ContentElement)
        {
            // ContentElement focus (for example, a Hyperlink) may not have a visual parent.
            // Preserve an observed handle and useful logical ancestry without a full-tree retry.
        }

        var leafFirst = new List<DependencyObject>();
        DependencyObject? current = element;
        for (var depth = 0; current is not null && depth < 512; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            leafFirst.Add(current);
            if (ReferenceEquals(current, window))
            {
                break;
            }

            current = TryGetKeyboardNavigationParent(current);
        }

        leafFirst.Reverse();
        if (leafFirst.Count == 0 || !ReferenceEquals(leafFirst[0], window))
        {
            leafFirst.Insert(0, window);
        }

        var chain = new List<(DependencyObject Element, string XPath)>(leafFirst.Count);
        var xpath = "/Window";
        chain.Add((window, xpath));
        foreach (var item in leafFirst.Skip(1))
        {
            xpath += "/" + GetXPathLabel(item);
            chain.Add((item, xpath));
        }

        return chain;
    }

    private static DependencyObject? TryGetKeyboardNavigationParent(DependencyObject element)
    {
        try
        {
            var visualParent = VisualTreeHelper.GetParent(element);
            if (visualParent is not null)
            {
                return visualParent;
            }
        }
        catch
        {
        }

        try
        {
            return element switch
            {
                ContentElement contentElement =>
                    ContentOperations.GetParent(contentElement) ??
                    (contentElement as FrameworkContentElement)?.Parent,
                FrameworkElement frameworkElement =>
                    frameworkElement.Parent ?? LogicalTreeHelper.GetParent(frameworkElement),
                _ => LogicalTreeHelper.GetParent(element)
            };
        }
        catch
        {
            return null;
        }
    }

    private static WpfKeyboardNavigationMetadata BuildKeyboardNavigationMetadata(
        DependencyObject element,
        IReadOnlyList<(DependencyObject Element, string XPath)> chain)
    {
        var focusScope = FocusManager.GetFocusScope(element);
        var focusScopeXPath = focusScope is null
            ? null
            : chain.LastOrDefault(entry => ReferenceEquals(entry.Element, focusScope)).XPath;

        var navigationGroup = chain
            .Reverse()
            .FirstOrDefault(entry =>
                KeyboardNavigation.GetTabNavigation(entry.Element) != KeyboardNavigationMode.Continue);
        var navigationGroupElement = navigationGroup.Element ?? element;

        return new WpfKeyboardNavigationMetadata(
            TabIndex: KeyboardNavigation.GetTabIndex(element),
            IsTabStop: KeyboardNavigation.GetIsTabStop(element),
            Focusable: IsKeyboardFocusableWpf(element),
            IsEnabled: GetIsEnabledWpf(element),
            IsVisible: TryGetIsVisibleWpf(element),
            IsFocusScope: FocusManager.GetIsFocusScope(element),
            FocusScopeXPath: string.IsNullOrWhiteSpace(focusScopeXPath) ? null : focusScopeXPath,
            NavigationGroupXPath: string.IsNullOrWhiteSpace(navigationGroup.XPath) ? null : navigationGroup.XPath,
            TabNavigation: KeyboardNavigation.GetTabNavigation(navigationGroupElement).ToString(),
            ControlTabNavigation: KeyboardNavigation.GetControlTabNavigation(navigationGroupElement).ToString(),
            DirectionalNavigation: KeyboardNavigation.GetDirectionalNavigation(navigationGroupElement).ToString());
    }
}
