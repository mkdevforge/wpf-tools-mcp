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

            var backend = request.Backend;
            if (backend == InspectionBackend.Auto)
            {
                backend = IsAgentConnected ? InspectionBackend.Wpf : InspectionBackend.Uia;
            }

            long windowHandleForWpf;
            if (backend == InspectionBackend.Wpf)
            {
                var resolvedWindowHandle = ResolveWindowHandleAtPointUia(request.X, request.Y, cancellationToken);
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
                InspectionBackend.Uia => PickElementAtPointUia(request, cancellationToken),
                InspectionBackend.Wpf => await PickElementAtPointWpfAsync(windowHandleForWpf, request, cancellationToken).ConfigureAwait(false),
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
        CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var requestedWindowHandle = request.WindowHandle;

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

        var resolved = ResolveContainingWindowUia(element, rawWalker);
        var windowHandleUsed = resolved.WindowHandle;

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
