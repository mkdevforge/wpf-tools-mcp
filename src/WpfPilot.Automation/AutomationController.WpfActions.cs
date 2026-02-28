using System.Drawing;
using System.Linq;
using FlaUI.Core.AutomationElements;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    private async Task EnsureWpfHandleEnabledOrThrowAsync(
        string elementId,
        string actionName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        try
        {
            var props = await GetComputedPropertiesAsync(
                elementId: elementId,
                propertyNames: ["IsEnabled"],
                includeSources: false,
                includeDefault: true,
                includeUnset: false,
                maxProperties: 10,
                valueFormat: "string",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var isEnabledProp = props.Properties.FirstOrDefault(p => string.Equals(p.Name, "IsEnabled", StringComparison.Ordinal));
            if (isEnabledProp?.Value is not null &&
                bool.TryParse(isEnabledProp.Value, out var isEnabled) &&
                !isEnabled)
            {
                throw new InvalidOperationException($"element_disabled: action={actionName} (Backend=wpf, elementId={elementId}).");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"wpf_enabled_check_failed: action={actionName} elementId={elementId}.", ex);
        }
    }

    private async Task<BringIntoViewWpfResponse> BringIntoViewWpfAsync(
        ElementHandle handle,
        CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallAsync<BringIntoViewWpfResponse>(
            "wpf/bring_into_view",
            new BringIntoViewWpfRequest(handle.WindowHandle, handle.XPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BringIntoViewWpfResponse> BringIntoViewWpfAsync(
        long windowHandle,
        string xpath,
        CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallAsync<BringIntoViewWpfResponse>(
            "wpf/bring_into_view",
            new BringIntoViewWpfRequest(windowHandle, xpath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Rect> ResolveWpfBoundsForHandleAsync(
        Window window,
        ElementHandle handle,
        bool autoScroll,
        CancellationToken cancellationToken)
    {
        // First try: resolve even if outside viewport (still "visible" in WPF terms).
        var resolved = await ResolveWpfElementRefAsync(
            new ElementLocator(XPath: handle.XPath),
            handle.WindowHandle,
            visibleOnly: true,
            includeOffViewport: true,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var bounds = resolved.Bounds;
        if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"wpf_element_has_no_bounds: '{handle.XPath}'.");
        }

        if (!autoScroll)
        {
            return bounds;
        }

        if (TryGetClientBoundsScreen(window, out var clientBounds) && !RectIntersects(bounds, clientBounds))
        {
            var bring = await BringIntoViewWpfAsync(handle, cancellationToken).ConfigureAwait(false);
            if (!bring.BroughtIntoView)
            {
                // Fall back to returning the original bounds; caller may still choose to act.
                return bounds;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            // Re-resolve after BringIntoView to get updated bounds (and to confirm existence).
            resolved = await ResolveWpfElementRefAsync(
                new ElementLocator(XPath: handle.XPath),
                handle.WindowHandle,
                visibleOnly: true,
                includeOffViewport: false,
                interactiveOnly: false,
                interactiveMode: InteractiveMode.Heuristic,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            bounds = resolved.Bounds;
            if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException($"wpf_element_has_no_bounds_after_bring_into_view: '{handle.XPath}'.");
            }
        }

        return bounds;
    }

    private static Point GetRectCenterPoint(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Invalid bounds.");
        }

        return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
    }
}
