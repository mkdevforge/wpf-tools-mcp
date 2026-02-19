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

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var window = request.WindowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        var windowHandleUsed = window.Properties.NativeWindowHandle.Value.ToInt64();

        if (!IsPointInWindowBounds(window, request.X, request.Y))
        {
            throw new InvalidOperationException(
                $"pick_point_outside_window: point=({request.X},{request.Y}) window={windowHandleUsed}.");
        }

        var backend = request.Backend;
        if (backend == InspectionBackend.Auto)
        {
            backend = IsAgentConnected ? InspectionBackend.Wpf : InspectionBackend.Uia;
        }

        return backend switch
        {
            InspectionBackend.Uia => PickElementAtPointUia(window, windowHandleUsed, request, cancellationToken),
            InspectionBackend.Wpf => await PickElementAtPointWpfAsync(windowHandleUsed, request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Backend), request.Backend, "Unsupported backend.")
        };
    }

    private PickElementAtPointResponse PickElementAtPointUia(
        Window window,
        long windowHandleUsed,
        PickElementAtPointRequest request,
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var point = new System.Drawing.Point(request.X, request.Y);
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

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
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

        return new PickElementAtPointResponse(InspectionBackend.Uia, elementRef, windowHandleUsed, ancestors);
    }

    private async Task<PickElementAtPointResponse> PickElementAtPointWpfAsync(
        long windowHandleUsed,
        PickElementAtPointRequest request,
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
                X: request.X,
                Y: request.Y,
                IncludeAncestors: request.IncludeAncestors,
                MaxAncestors: request.MaxAncestors,
                ReturnFields: FindReturnFields.Standard),
            cancellationToken).ConfigureAwait(false);

        var pickedId = _elementHandles.RegisterWpf(
            windowHandleUsed,
            response.Element.XPath,
            response.Element.Type,
            response.Element.AutomationId,
            response.Element.Name,
            response.Element.ClassName);

        var picked = response.Element with { ElementId = pickedId };

        IReadOnlyList<ElementRef>? ancestors = null;
        if (request.IncludeAncestors && response.Ancestors is { Count: > 0 })
        {
            var withIds = response.Ancestors
                .Select(a =>
                {
                    var id = _elementHandles.RegisterWpf(
                        windowHandleUsed,
                        a.XPath,
                        a.Type,
                        a.AutomationId,
                        a.Name,
                        a.ClassName);

                    return a with { ElementId = id };
                })
                .ToArray();

            ancestors = withIds;
        }

        return new PickElementAtPointResponse(InspectionBackend.Wpf, picked, windowHandleUsed, ancestors);
    }

    private static bool IsPointInWindowBounds(Window window, int x, int y)
    {
        var bounds = window.BoundingRectangle;
        return x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom;
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
}
