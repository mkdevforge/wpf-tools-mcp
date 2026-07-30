using FlaUI.Core.AutomationElements;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public async Task<SendKeysResponse> SendKeysAsync(
        SendKeysRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("send_keys");
        try
        {
            var hasLocator = request.Locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            if (hasLocator && hasElementId)
            {
                throw new ArgumentException("send_keys requires at most one of: locator OR elementId.");
            }

            var preparedSequence = KeyboardInputEngine.BuildSequence(request.Sequence);
            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            if (!hasElementId)
            {
                EnsurePhysicalInputAllowed(
                    operation: "send_keys",
                    policy,
                    semanticAlternative: "send_keys has no semantic-input path.");
            }

            var application = EnsureAttached();
            var automation = EnsureAutomation();
            var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
            var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
            var stableMs = Math.Clamp(request.StableMs, 0, 5000);
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();

            Window window;
            AutomationElement? uiaElement = null;
            ElementHandle? wpfHandle = null;
            string? wpfElementId = null;

            if (!hasLocator && !hasElementId)
            {
                window = request.WindowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                var effects = new InteractionEffectTracker();
                var focusedBeforeOperation = TryGetFocusedElement(automation);
                await PrepareWindowForPhysicalInputAsync(
                    window,
                    operation: "send_keys",
                    policy,
                    effects,
                    semanticAlternative: "send_keys has no semantic-input path.",
                    cancellationToken,
                    focusWindow: false).ConfigureAwait(false);

                uiaElement = GetFocusedElementInSession(window, automation, rawWalker, "send_keys");
                await WaitForKeyboardTargetAsync(
                    uiaElement,
                    request.AutoWait,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    cancellationToken).ConfigureAwait(false);

                KeyboardInputEngine.SendPreparedSequence(preparedSequence, cancellationToken);
                effects.MarkKeyboardInput();
                MarkKeyboardFocusChangeIfDifferent(focusedBeforeOperation, automation, effects);
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=keyboard_focused");
                return new SendKeysResponse(
                    Sent: true,
                    MethodUsed: "keyboard_focused",
                    Effects: effects.ToContract(),
                    ForegroundFocusRequired: true,
                    PhysicalInputRequired: true);
            }

            if (hasElementId)
            {
                var elementId = request.ElementId!.Trim();
                var handle = RequireHandle(elementId);
                if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the elementId window.");
                }

                try
                {
                    window = FindWindowByHandle(application, automation, handle.WindowHandle);
                }
                catch
                {
                    throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
                }

                if (handle.Backend == InspectionBackend.Wpf)
                {
                    await EnsureWpfHandleEnabledOrThrowAsync(elementId, "send_keys", cancellationToken).ConfigureAwait(false);
                    try
                    {
                        uiaElement = ResolveUiaElementByWpfHandle(
                            window,
                            controlWalker,
                            rawWalker,
                            elementId,
                            handle,
                            out _);
                    }
                    catch (InvalidOperationException)
                    {
                        wpfHandle = handle;
                        wpfElementId = elementId;
                    }
                }
                else if (handle.Backend == InspectionBackend.Uia)
                {
                    uiaElement = ResolveUiaElementById(
                        window,
                        rawWalker,
                        elementId,
                        out _,
                        UiaHandleResolutionMode.RequireRegisteredIdentity);
                }
                else
                {
                    throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
                }
            }
            else
            {
                window = request.WindowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                    window,
                    request.Locator!,
                    request.AutoWait ? timeoutMs : 0,
                    pollIntervalMs,
                    request.AutoWait ? stableMs : 0,
                    visibleOnly: true,
                    includeOffViewport: true,
                    interactiveOnly: false,
                    interactiveMode: InteractiveMode.Heuristic,
                    cancellationToken).ConfigureAwait(false);

                if (wpfTarget is not null)
                {
                    await EnsureWpfHandleEnabledOrThrowAsync(wpfTarget.ElementId, "send_keys", cancellationToken).ConfigureAwait(false);
                    try
                    {
                        uiaElement = ResolveUiaElementByWpfHandle(
                            window,
                            controlWalker,
                            rawWalker,
                            wpfTarget.ElementId,
                            wpfTarget.Handle,
                            out _);
                    }
                    catch (InvalidOperationException)
                    {
                        wpfHandle = wpfTarget.Handle;
                        wpfElementId = wpfTarget.ElementId;
                    }
                }
                else
                {
                    uiaElement = request.AutoWait
                        ? await ResolveUiaElementWithWaitAsync(
                            window,
                            request.Locator!,
                            controlWalker,
                            rawWalker,
                            timeoutMs,
                            pollIntervalMs,
                            ActionKind.Inspect,
                            cancellationToken).ConfigureAwait(false)
                        : ResolveElement(
                            window,
                            request.Locator!,
                            controlWalker,
                            rawWalker,
                            ActionKind.Inspect);
                }
            }

            if (hasElementId)
            {
                EnsurePhysicalInputAllowed(
                    operation: "send_keys",
                    policy,
                    semanticAlternative: "send_keys has no semantic-input path.");
            }

            var targetEffects = new InteractionEffectTracker();
            var focusedBeforeInput = TryGetFocusedElement(automation);
            if (wpfHandle is not null)
            {
                await PrepareWindowForPhysicalInputAsync(
                    window,
                    operation: "send_keys",
                    policy,
                    targetEffects,
                    semanticAlternative: "send_keys has no semantic-input path.",
                    cancellationToken,
                    focusWindow: false).ConfigureAwait(false);

                var focused = await FocusWpfHandleForKeyboardInputAsync(
                    wpfElementId!,
                    wpfHandle,
                    cancellationToken).ConfigureAwait(false);
                if (focused.KeyboardFocusChanged)
                {
                    targetEffects.MarkKeyboardFocusChanged();
                }

                KeyboardInputEngine.SendPreparedSequence(preparedSequence, cancellationToken);
                targetEffects.MarkKeyboardInput();
                MarkKeyboardFocusChangeIfDifferent(focusedBeforeInput, automation, targetEffects);
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=keyboard_wpf_focus");
                return new SendKeysResponse(
                    Sent: true,
                    MethodUsed: "keyboard_wpf_focus",
                    Effects: targetEffects.ToContract(),
                    ForegroundFocusRequired: true,
                    PhysicalInputRequired: true);
            }

            await PrepareWindowForPhysicalInputAsync(
                window,
                operation: "send_keys",
                policy,
                targetEffects,
                semanticAlternative: "send_keys has no semantic-input path.",
                cancellationToken,
                focusWindow: false).ConfigureAwait(false);
            TryScrollIntoView(uiaElement!);
            await WaitForKeyboardTargetAsync(
                uiaElement!,
                request.AutoWait,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                cancellationToken).ConfigureAwait(false);
            FocusUiaElementForKeyboardInput(uiaElement!, automation, rawWalker, targetEffects);
            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            KeyboardInputEngine.SendPreparedSequence(preparedSequence, cancellationToken);
            targetEffects.MarkKeyboardInput();
            MarkKeyboardFocusChangeIfDifferent(focusedBeforeInput, automation, targetEffects);
            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            trace?.SetSummary("method=keyboard_uia_focus");
            return new SendKeysResponse(
                Sent: true,
                MethodUsed: "keyboard_uia_focus",
                Effects: targetEffects.ToContract(),
                ForegroundFocusRequired: true,
                PhysicalInputRequired: true);
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

    private static AutomationElement GetFocusedElementInSession(
        Window window,
        FlaUI.Core.AutomationBase automation,
        FlaUI.Core.ITreeWalker rawWalker,
        string operation)
    {
        AutomationElement element;
        try
        {
            element = automation.FocusedElement();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("focused_element_unavailable: unable to read the currently focused element.", ex);
        }

        if (AreSameElement(window, element))
        {
            throw new InvalidOperationException("focused_element_unavailable: the session window is focused, but no child input target is focused.");
        }

        if (!IsElementWithinWindow(window, element, rawWalker))
        {
            throw new InvalidOperationException("focused_element_outside_session: the currently focused element does not belong to the active session window.");
        }

        EnsureEnabledOrThrow(element, operation);
        return element;
    }

    private static async Task WaitForKeyboardTargetAsync(
        AutomationElement element,
        bool autoWait,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        CancellationToken cancellationToken)
    {
        EnsureEnabledOrThrow(element, "send_keys");
        if (!autoWait)
        {
            return;
        }

        if (stableMs > 0)
        {
            await WaitForResolvedElementStateAsync(
                element,
                WaitForState.Stable,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                expectedValue: null,
                expectedText: null,
                cancellationToken).ConfigureAwait(false);
        }

        await WaitForResolvedElementStateAsync(
            element,
            WaitForState.Visible,
            timeoutMs,
            pollIntervalMs,
            stableMs,
            expectedValue: null,
            expectedText: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static void FocusUiaElementForKeyboardInput(
        AutomationElement element,
        FlaUI.Core.AutomationBase automation,
        FlaUI.Core.ITreeWalker rawWalker,
        InteractionEffectTracker effects)
    {
        var before = TryGetFocusedElement(automation);
        try
        {
            element.Focus();
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"focus_failed_uia_target: unable to focus target AutomationId='{GetAutomationId(element)}', Name='{GetName(element)}'.",
                ex);
        }

        var after = TryGetFocusedElement(automation);
        if (after is null || !IsElementWithinTarget(element, after, rawWalker))
        {
            throw new InvalidOperationException(
                $"focus_failed_uia_target: target AutomationId='{GetAutomationId(element)}', Name='{GetName(element)}' " +
                "did not receive keyboard focus. No keys were sent.");
        }

        if (before is null || !AreSameElement(before, after))
        {
            effects.MarkKeyboardFocusChanged();
        }
    }

    private static bool IsElementWithinTarget(
        AutomationElement target,
        AutomationElement element,
        FlaUI.Core.ITreeWalker rawWalker)
    {
        var current = element;
        for (var depth = 0; depth < 256; depth++)
        {
            if (AreSameElement(target, current))
            {
                return true;
            }

            try
            {
                current = rawWalker.GetParent(current);
            }
            catch
            {
                return false;
            }

            if (current is null)
            {
                return false;
            }
        }

        return false;
    }

    private static AutomationElement? TryGetFocusedElement(FlaUI.Core.AutomationBase automation)
    {
        try
        {
            return automation.FocusedElement();
        }
        catch
        {
            return null;
        }
    }

    private static void MarkKeyboardFocusChangeIfDifferent(
        AutomationElement? focusedBefore,
        FlaUI.Core.AutomationBase automation,
        InteractionEffectTracker effects)
    {
        var focusedAfter = TryGetFocusedElement(automation);
        if (focusedAfter is not null &&
            (focusedBefore is null || !AreSameElement(focusedBefore, focusedAfter)))
        {
            effects.MarkKeyboardFocusChanged();
        }
    }
}
