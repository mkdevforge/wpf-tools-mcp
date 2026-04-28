using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    public async Task<PickElementAtPointResponse> PickElementAtPointAsync(
        PickElementAtPointRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("pick_element_at_point");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            int xScreen;
            int yScreen;
            var coordSpaceUsed = request.CoordSpace;

            switch (request.CoordSpace)
            {
                case MouseCoordinateSpace.Screen:
                    xScreen = request.X;
                    yScreen = request.Y;
                    break;
                case MouseCoordinateSpace.Client:
                    var clientWindowHandle = request.WindowHandle;
                    if (clientWindowHandle is null or 0)
                    {
                        clientWindowHandle = FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
                    }

                    if (!TryGetClientTopLeftScreen(new IntPtr(clientWindowHandle.Value), out var clientTopLeft))
                    {
                        throw new InvalidOperationException("client_origin_unavailable");
                    }

                    xScreen = clientTopLeft.X + request.X;
                    yScreen = clientTopLeft.Y + request.Y;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.CoordSpace), request.CoordSpace, "Unsupported coordinate space.");
            }

            var backend = request.Backend;
            if (backend == InspectionBackend.Auto)
            {
                backend = IsAgentConnected ? InspectionBackend.Wpf : InspectionBackend.Uia;
            }

            long windowHandleForWpf;
            if (backend == InspectionBackend.Wpf)
            {
                var resolvedWindowHandle = ResolveWindowHandleAtPointUia(xScreen, yScreen, cancellationToken);
                if (request.WindowHandle is long expectedHandle && expectedHandle != resolvedWindowHandle)
                {
                    throw new InvalidOperationException(
                        $"pick_point_in_different_window: expected_window={expectedHandle} actual_window={resolvedWindowHandle}.");
                }

                windowHandleForWpf = request.WindowHandle ?? resolvedWindowHandle;
            }
            else
            {
                windowHandleForWpf = 0;
            }

            var response = backend switch
            {
                InspectionBackend.Uia => PickElementAtPointUia(request, xScreen, yScreen, coordSpaceUsed, cancellationToken),
                InspectionBackend.Wpf => await PickElementAtPointWpfAsync(windowHandleForWpf, request, xScreen, yScreen, coordSpaceUsed, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Backend), request.Backend, "Unsupported backend.")
            };

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

    private PickElementAtPointResponse PickElementAtPointUia(
        PickElementAtPointRequest request,
        int xScreen,
        int yScreen,
        MouseCoordinateSpace coordSpaceUsed,
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var requestedWindowHandle = request.WindowHandle;

        var point = new System.Drawing.Point(xScreen, yScreen);
        var element = automation.FromPoint(point);

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        long windowHandleUsed;
        if (element is null)
        {
            if (requestedWindowHandle is not long requested)
            {
                throw new InvalidOperationException($"no_hit_at_point: x={xScreen} y={yScreen}.");
            }

            windowHandleUsed = requested;
        }
        else
        {
            try
            {
                var pid = element.Properties.ProcessId.Value;
                if (pid != application.ProcessId)
                {
                    throw new InvalidOperationException($"no_hit_at_point: point resolved to process {pid}, expected {application.ProcessId}.");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to validate picked element: {ex.Message}");
            }

            var resolved = ResolveContainingWindowUia(element, rawWalker);
            windowHandleUsed = resolved.WindowHandle;
        }

        if (requestedWindowHandle is long expectedHandle && expectedHandle != windowHandleUsed)
        {
            throw new InvalidOperationException(
                $"pick_point_in_different_window: expected_window={expectedHandle} actual_window={windowHandleUsed}.");
        }

        Window window;
        try
        {
            window = FindWindowByHandle(application, automation, windowHandleUsed);
        }
        catch
        {
            throw new InvalidOperationException($"Failed to resolve picked element window handle {windowHandleUsed}.");
        }

        var deepest = FindDeepestUiaElementAtPoint(window, rawWalker, xScreen, yScreen, maxNodes: 10_000, cancellationToken);
        if (deepest is null || AreSameElement(deepest, window))
        {
            if (!request.ReturnRootOnMiss)
            {
                throw new InvalidOperationException($"no_hit_at_point: x={xScreen} y={yScreen}.");
            }

            element = window;
        }
        else
        {
            element = deepest;
        }

        var xpath = ComputeXPath(window, element, rawWalker);

        var elementId = _elementHandles.RegisterUia(
            windowHandleUsed,
            xpath,
            TryGetRuntimeId(element),
            element.ControlType.ToString(),
            GetAutomationId(element),
            GetName(element),
            GetClassName(element));

        var elementRef = BuildElementRefUia(element, xpath, FindReturnFields.Standard, elementId);

        IReadOnlyList<ElementRef>? ancestors = null;
        if (request.IncludeAncestors)
        {
            ancestors = BuildUiaAncestorRefs(window, rawWalker, element, windowHandleUsed, request.MaxAncestors);
        }

        return new PickElementAtPointResponse(
            BackendUsed: InspectionBackend.Uia,
            Element: elementRef,
            WindowHandleUsed: windowHandleUsed,
            XScreen: xScreen,
            YScreen: yScreen,
            CoordSpaceUsed: coordSpaceUsed,
            Ancestors: ancestors);
    }

    private long ResolveWindowHandleAtPointUia(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        var point = new System.Drawing.Point(x, y);
        var element = automation.FromPoint(point)
            ?? throw new InvalidOperationException("No UIA element found at point.");

        try
        {
            var pid = element.Properties.ProcessId.Value;
            if (pid != application.ProcessId)
            {
                throw new InvalidOperationException("Point resolved to a different process.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to validate picked element: {ex.Message}");
        }

        return ResolveContainingWindowUia(element, rawWalker).WindowHandle;
    }

    private static (long WindowHandle, AutomationElement WindowElement) ResolveContainingWindowUia(AutomationElement element, ITreeWalker rawWalker)
    {
        AutomationElement? current = element;
        var safety = 0;

        while (current is not null && safety++ < 512)
        {
            if (current.ControlType == FlaUI.Core.Definitions.ControlType.Window)
            {
                try
                {
                    var handle = current.Properties.NativeWindowHandle.Value.ToInt64();
                    if (handle != 0)
                    {
                        return (handle, current);
                    }
                }
                catch
                {
                }
            }

            AutomationElement? parent;
            try
            {
                parent = rawWalker.GetParent(current);
            }
            catch
            {
                parent = null;
            }

            current = parent;
        }

        throw new InvalidOperationException("Failed to resolve containing window for picked element.");
    }

    private async Task<PickElementAtPointResponse> PickElementAtPointWpfAsync(
        long windowHandleUsed,
        PickElementAtPointRequest request,
        int xScreen,
        int yScreen,
        MouseCoordinateSpace coordSpaceUsed,
        CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            throw new InvalidOperationException("WPF agent is not connected. Call inject_agent first.");
        }

        var response = await client.CallAsync<PickWpfElementAtPointResponse>(
            "wpf/pick_element_at_point",
            new PickWpfElementAtPointRequest(
                WindowHandle: windowHandleUsed,
                X: xScreen,
                Y: yScreen,
                IncludeAncestors: request.IncludeAncestors,
                MaxAncestors: request.MaxAncestors,
                ReturnRootOnMiss: request.ReturnRootOnMiss,
                ReturnFields: FindReturnFields.Standard),
            cancellationToken).ConfigureAwait(false);

        var pickedId = _elementHandles.RegisterWpf(
            windowHandleUsed,
            response.Element.XPath,
            response.Element.ElementIdWpf,
            response.Element.Type,
            response.Element.AutomationId,
            response.Element.Name,
            response.Element.ClassName);

        var picked = response.Element with { ElementId = pickedId, ElementIdWpf = null };

        IReadOnlyList<ElementRef>? ancestors = null;
        if (request.IncludeAncestors && response.Ancestors is { Count: > 0 })
        {
            var withIds = response.Ancestors
                .Select(a =>
                {
                    var id = _elementHandles.RegisterWpf(
                        windowHandleUsed,
                        a.XPath,
                        a.ElementIdWpf,
                        a.Type,
                        a.AutomationId,
                        a.Name,
                        a.ClassName);

                    return a with { ElementId = id, ElementIdWpf = null };
                })
                .ToArray();

            ancestors = withIds;
        }

        return new PickElementAtPointResponse(
            BackendUsed: InspectionBackend.Wpf,
            Element: picked,
            WindowHandleUsed: windowHandleUsed,
            XScreen: xScreen,
            YScreen: yScreen,
            CoordSpaceUsed: coordSpaceUsed,
            Ancestors: ancestors);
    }

    private IReadOnlyList<ElementRef> BuildUiaAncestorRefs(
        Window window,
        ITreeWalker rawWalker,
        AutomationElement element,
        long windowHandleUsed,
        int maxAncestors)
    {
        if (AreSameElement(element, window))
        {
            return [];
        }

        maxAncestors = Math.Clamp(maxAncestors, 0, 50);

        var chain = new List<AutomationElement> { element };
        AutomationElement? current = element;

        while (chain.Count < maxAncestors + 2)
        {
            AutomationElement? parent;
            try
            {
                parent = rawWalker.GetParent(current);
            }
            catch
            {
                parent = null;
            }

            if (parent is null)
            {
                break;
            }

            chain.Add(parent);
            current = parent;

            if (AreSameElement(parent, window))
            {
                break;
            }
        }

        chain.Reverse();

        var results = new List<ElementRef>(Math.Max(0, chain.Count - 1));
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var ancestor = chain[i];
            var xpath = ComputeXPath(window, ancestor, rawWalker);

            var elementId = _elementHandles.RegisterUia(
                windowHandleUsed,
                xpath,
                TryGetRuntimeId(ancestor),
                ancestor.ControlType.ToString(),
                GetAutomationId(ancestor),
                GetName(ancestor),
                GetClassName(ancestor));

            results.Add(BuildElementRefUia(ancestor, xpath, FindReturnFields.Minimal, elementId));
        }

        return results;
    }

    private static AutomationElement? FindDeepestUiaElementAtPoint(
        Window window,
        ITreeWalker rawWalker,
        int x,
        int y,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        maxNodes = Math.Clamp(maxNodes, 1, 200_000);
        var point = new System.Drawing.Point(x, y);
        AutomationElement? best = null;
        long bestArea = long.MaxValue;
        var bestDepth = -1;
        var scanned = 0;

        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((window, 0));

        while (stack.Count > 0 && scanned < maxNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = stack.Pop();
            scanned++;

            if (TryGetContainingBounds(current, point, out var bounds))
            {
                var area = Math.Max(1L, (long)bounds.Width * bounds.Height);
                if (area < bestArea || (area == bestArea && depth > bestDepth))
                {
                    best = current;
                    bestArea = area;
                    bestDepth = depth;
                }
            }

            AutomationElement? child;
            try
            {
                child = rawWalker.GetFirstChild(current);
            }
            catch
            {
                child = null;
            }

            while (child is not null && scanned + stack.Count < maxNodes)
            {
                stack.Push((child, depth + 1));
                try
                {
                    child = rawWalker.GetNextSibling(child);
                }
                catch
                {
                    child = null;
                }
            }
        }

        return best;
    }

    private static bool TryGetContainingBounds(
        AutomationElement element,
        System.Drawing.Point point,
        out System.Drawing.Rectangle bounds)
    {
        bounds = default;
        try
        {
            bounds = element.BoundingRectangle;
            return bounds.Width > 0 &&
                   bounds.Height > 0 &&
                   point.X >= bounds.Left &&
                   point.X < bounds.Right &&
                   point.Y >= bounds.Top &&
                   point.Y < bounds.Bottom;
        }
        catch
        {
            return false;
        }
    }
}
