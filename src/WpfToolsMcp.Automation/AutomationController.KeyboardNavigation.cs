using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public async Task<TraceKeyboardNavigationResponse> TraceKeyboardNavigationAsync(
        TraceKeyboardNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trace = BeginTraceSpan("trace_keyboard_navigation");
        try
        {
            var hasLocator = request.Locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            if (hasLocator && hasElementId)
            {
                throw new ArgumentException("trace_keyboard_navigation requires at most one of: locator OR elementId.");
            }

            if (!Enum.IsDefined(request.Direction))
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.Direction, "Unknown keyboard navigation direction.");
            }

            if (!Enum.IsDefined(request.Mode))
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unknown keyboard navigation mode.");
            }

            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            if (request.Mode == KeyboardNavigationTraceMode.Physical)
            {
                EnsurePhysicalInputAllowed(
                    operation: "trace_keyboard_navigation",
                    policy,
                    semanticAlternative: "Use mode=WpfSemantic for in-process WPF traversal.");
            }

            var maxSteps = NormalizeKeyboardNavigationMaxSteps(request.MaxSteps);
            var application = EnsureAttached();
            var automation = EnsureAutomation();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();

            ElementHandle? suppliedHandle = null;
            if (hasElementId)
            {
                suppliedHandle = RequireHandle(request.ElementId!.Trim());
                if (request.WindowHandle is long requestedHandle && requestedHandle != suppliedHandle.WindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the elementId window.");
                }
            }

            var window = suppliedHandle is not null
                ? FindWindowByHandle(application, automation, suppliedHandle.WindowHandle)
                : request.WindowHandle is long requestedWindowHandle
                    ? FindWindowByHandle(application, automation, requestedWindowHandle)
                    : FindMainWindow(application, automation);
            var windowHandle = window.Properties.NativeWindowHandle.Value.ToInt64();

            var effects = new InteractionEffectTracker();
            var originalUiaFocus = TryGetFocusedElement(automation);

            var agentClient = await SelectKeyboardNavigationAgentAsync(
                request.Mode,
                () => EnsureAgentConnectedOrNullAsync(cancellationToken),
                () => EnsureAgentConnectedAsync(cancellationToken)).ConfigureAwait(false);
            if (request.Mode == KeyboardNavigationTraceMode.WpfSemantic)
            {
                EnsureKeyboardNavigationCapability(agentClient!);
                effects.MarkSemantic();
            }
            else
            {
                if (agentClient is not null && !AgentSupportsCapability(agentClient, AgentProtocolCapabilities.KeyboardNavigationStep))
                {
                    agentClient = null;
                }
            }

            var originalWpfFocus = await TryReadWpfFocusAsync(
                agentClient,
                windowHandle,
                request.Direction,
                required: request.Mode == KeyboardNavigationTraceMode.WpfSemantic,
                cancellationToken).ConfigureAwait(false);

            if (request.Mode == KeyboardNavigationTraceMode.Physical)
            {
                await PrepareWindowForPhysicalInputAsync(
                    window,
                    operation: "trace_keyboard_navigation",
                    policy,
                    effects,
                    semanticAlternative: "Use mode=WpfSemantic for in-process WPF traversal.",
                    cancellationToken,
                    focusWindow: false).ConfigureAwait(false);
            }

            await FocusKeyboardNavigationStartAsync(
                request,
                suppliedHandle,
                window,
                automation,
                controlWalker,
                rawWalker,
                effects,
                cancellationToken).ConfigureAwait(false);

            if (request.Mode == KeyboardNavigationTraceMode.Physical && agentClient is null)
            {
                agentClient = await SelectKeyboardNavigationAgentAsync(
                    request.Mode,
                    () => EnsureAgentConnectedOrNullAsync(cancellationToken),
                    () => EnsureAgentConnectedAsync(cancellationToken)).ConfigureAwait(false);
                if (agentClient is not null && !AgentSupportsCapability(agentClient, AgentProtocolCapabilities.KeyboardNavigationStep))
                {
                    agentClient = null;
                }
            }

            KeyboardNavigationFocusResult start;
            try
            {
                start = await ObserveKeyboardNavigationFocusAsync(
                    window,
                    rawWalker,
                    agentClient,
                    request.Direction,
                    requiredWpfEvidence: request.Mode == KeyboardNavigationTraceMode.WpfSemantic,
                    suppliedWpfResponse: null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!IsWindow(new IntPtr(windowHandle)))
            {
                start = KeyboardNavigationFocusResult.Unavailable;
            }
            var traceState = new KeyboardNavigationTraceState(start.Identity);
            var steps = new List<KeyboardNavigationTraceStep>(maxSteps);
            var startStopReason = ClassifyTerminalObservation(start, windowHandle, request.Mode);
            var stopReason = startStopReason ?? KeyboardNavigationStopReason.MaximumSteps;
            if (request.Mode == KeyboardNavigationTraceMode.Physical &&
                agentClient is not null &&
                !start.WpfEvidenceCallSucceeded)
            {
                agentClient = null;
            }

            if (traceState.HasStart && startStopReason is null)
            {
                var preparedPhysicalStep = request.Mode == KeyboardNavigationTraceMode.Physical
                    ? BuildPhysicalKeyboardNavigationStep(request.Direction)
                    : null;

                for (var index = 1; index <= maxSteps; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var startedAt = Stopwatch.GetTimestamp();
                    var methodUsed = GetKeyboardNavigationMethod(request.Mode, request.Direction);
                    WpfKeyboardNavigationStepResponse? semanticResponse = null;

                    if (!IsWindow(new IntPtr(windowHandle)))
                    {
                        stopReason = KeyboardNavigationStopReason.WindowClosed;
                        break;
                    }

                    try
                    {
                        if (request.Mode == KeyboardNavigationTraceMode.Physical)
                        {
                            KeyboardInputEngine.SendPreparedSequence(preparedPhysicalStep!, cancellationToken);
                            effects.MarkKeyboardInput();
                        }
                        else
                        {
                            semanticResponse = await agentClient!.CallAsync<WpfKeyboardNavigationStepResponse>(
                                AgentProtocolCapabilities.KeyboardNavigationStep,
                                new WpfKeyboardNavigationStepRequest(windowHandle, request.Direction, Move: true),
                                cancellationToken).ConfigureAwait(false);
                        }

                        if (UiDelayMs > 0)
                        {
                            await Task.Delay(UiDelayMs, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception) when (!IsWindow(new IntPtr(windowHandle)))
                    {
                        steps.Add(new KeyboardNavigationTraceStep(
                            index,
                            methodUsed,
                            ElapsedMilliseconds(startedAt),
                            Focus: null));
                        stopReason = KeyboardNavigationStopReason.WindowClosed;
                        break;
                    }

                    KeyboardNavigationFocusResult observed;
                    try
                    {
                        observed = await ObserveKeyboardNavigationFocusAsync(
                            window,
                            rawWalker,
                            agentClient,
                            request.Direction,
                            requiredWpfEvidence: request.Mode == KeyboardNavigationTraceMode.WpfSemantic,
                            suppliedWpfResponse: semanticResponse,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception) when (!IsWindow(new IntPtr(windowHandle)))
                    {
                        steps.Add(new KeyboardNavigationTraceStep(
                            index,
                            methodUsed,
                            ElapsedMilliseconds(startedAt),
                            Focus: null));
                        effects.MarkKeyboardFocusChanged();
                        stopReason = KeyboardNavigationStopReason.WindowClosed;
                        break;
                    }

                    if (request.Mode == KeyboardNavigationTraceMode.Physical &&
                        agentClient is not null &&
                        !observed.WpfEvidenceCallSucceeded)
                    {
                        agentClient = null;
                    }

                    steps.Add(new KeyboardNavigationTraceStep(
                        index,
                        methodUsed,
                        ElapsedMilliseconds(startedAt),
                        observed.Public));

                    var terminalReason = ClassifyTerminalObservation(observed, windowHandle, request.Mode);
                    if (terminalReason is KeyboardNavigationStopReason.FocusLeftWindow or
                        KeyboardNavigationStopReason.SemanticInteropBoundary or
                        KeyboardNavigationStopReason.WindowClosed)
                    {
                        effects.MarkKeyboardFocusChanged();
                    }

                    if (terminalReason is not null)
                    {
                        stopReason = terminalReason.Value;
                        break;
                    }

                    var transition = traceState.Add(observed.Identity);
                    if (transition.FocusChanged)
                    {
                        effects.MarkKeyboardFocusChanged();
                    }

                    if (transition.StopReason is not null)
                    {
                        stopReason = transition.StopReason.Value;
                        break;
                    }
                    if (index == maxSteps)
                    {
                        stopReason = KeyboardNavigationStopReason.MaximumSteps;
                    }
                }
            }

            var restoration = await RestoreKeyboardNavigationFocusAsync(
                request.RestoreFocus,
                originalUiaFocus,
                originalWpfFocus,
                agentClient,
                windowHandle,
                automation,
                cancellationToken).ConfigureAwait(false);

            trace?.SetSummary($"mode={request.Mode} steps={steps.Count} stop={stopReason}");
            return new TraceKeyboardNavigationResponse(
                windowHandle,
                request.Direction,
                request.Mode,
                start.Public,
                steps,
                stopReason,
                restoration,
                effects.ToContract());
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

    private async Task FocusKeyboardNavigationStartAsync(
        TraceKeyboardNavigationRequest request,
        ElementHandle? suppliedHandle,
        Window window,
        FlaUI.Core.AutomationBase automation,
        FlaUI.Core.ITreeWalker controlWalker,
        FlaUI.Core.ITreeWalker rawWalker,
        InteractionEffectTracker effects,
        CancellationToken cancellationToken)
    {
        if (suppliedHandle is null && request.Locator is null)
        {
            return;
        }

        if (suppliedHandle is not null)
        {
            var elementId = request.ElementId!.Trim();
            if (suppliedHandle.Backend == InspectionBackend.Wpf)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(
                    elementId,
                    "trace_keyboard_navigation",
                    cancellationToken).ConfigureAwait(false);
                var focused = await FocusWpfHandleForKeyboardInputAsync(
                    elementId,
                    suppliedHandle,
                    cancellationToken).ConfigureAwait(false);
                if (focused.KeyboardFocusChanged)
                {
                    effects.MarkKeyboardFocusChanged();
                }

                return;
            }

            if (suppliedHandle.Backend != InspectionBackend.Uia)
            {
                throw new InvalidOperationException(
                    $"elementId '{elementId}' has unsupported backend '{suppliedHandle.Backend}'.");
            }

            var uiaElement = ResolveUiaElementById(
                window,
                rawWalker,
                elementId,
                out _,
                UiaHandleResolutionMode.RequireRegisteredIdentity);
            FocusUiaElementForKeyboardInput(uiaElement, automation, rawWalker, effects);
            return;
        }

        if (request.Mode == KeyboardNavigationTraceMode.WpfSemantic)
        {
            var wpfElement = await ResolveWpfElementRefDetailedAsync(
                request.Locator!,
                window.Properties.NativeWindowHandle.Value.ToInt64(),
                visibleOnly: true,
                includeOffViewport: true,
                interactiveOnly: false,
                interactiveMode: InteractiveMode.Heuristic,
                cancellationToken).ConfigureAwait(false);
            var publicElementId = _elementHandles.RegisterWpf(
                window.Properties.NativeWindowHandle.Value.ToInt64(),
                wpfElement.XPath,
                wpfElement.ElementIdWpf,
                wpfElement.Type,
                wpfElement.AutomationId,
                wpfElement.Name,
                wpfElement.ClassName,
                wpfElement.Bounds);
            var focused = await FocusWpfHandleForKeyboardInputAsync(
                publicElementId,
                RequireHandle(publicElementId),
                cancellationToken).ConfigureAwait(false);
            if (focused.KeyboardFocusChanged)
            {
                effects.MarkKeyboardFocusChanged();
            }

            return;
        }

        var uiaTarget = ResolveElement(
            window,
            request.Locator!,
            controlWalker,
            rawWalker,
            ActionKind.Inspect);
        FocusUiaElementForKeyboardInput(uiaTarget, automation, rawWalker, effects);
    }

    private async Task<KeyboardNavigationFocusResult> ObserveKeyboardNavigationFocusAsync(
        Window window,
        FlaUI.Core.ITreeWalker rawWalker,
        AgentClient? agentClient,
        KeyboardNavigationDirection direction,
        bool requiredWpfEvidence,
        WpfKeyboardNavigationStepResponse? suppliedWpfResponse,
        CancellationToken cancellationToken)
    {
        var windowHandle = window.Properties.NativeWindowHandle.Value.ToInt64();
        var uia = ObserveUiaKeyboardFocus(window, rawWalker);
        var wpfResponse = suppliedWpfResponse ?? await TryReadWpfFocusAsync(
            agentClient,
            windowHandle,
            direction,
            requiredWpfEvidence,
            cancellationToken).ConfigureAwait(false);
        var wpf = NormalizeWpfKeyboardFocus(windowHandle, wpfResponse);

        var publicObservation = uia.Element is null && wpf.Element is null && wpf.Metadata is null
            ? null
            : new KeyboardNavigationFocusObservation(uia.Element, wpf.Element, wpf.Metadata);
        return new KeyboardNavigationFocusResult(
            publicObservation,
            new KeyboardNavigationFocusIdentity(uia.Identity, wpf.Identity),
            UiaOutsideWindow: uia.OutsideWindow,
            SemanticInteropBoundary: wpfResponse?.InteropBoundary == true,
            WpfEvidenceCallSucceeded: wpfResponse is not null);
    }

    private UiaKeyboardFocusEvidence ObserveUiaKeyboardFocus(
        Window window,
        FlaUI.Core.ITreeWalker rawWalker)
    {
        try
        {
            return ObserveUiaKeyboardFocusCore(window, rawWalker);
        }
        catch
        {
            return UiaKeyboardFocusEvidence.None;
        }
    }

    private UiaKeyboardFocusEvidence ObserveUiaKeyboardFocusCore(
        Window window,
        FlaUI.Core.ITreeWalker rawWalker)
    {
        var automation = EnsureAutomation();
        var focused = TryGetFocusedElement(automation);
        if (focused is null || AreSameElement(window, focused))
        {
            return UiaKeyboardFocusEvidence.None;
        }

        var runtimeId = TryGetRuntimeId(focused);
        var identity = runtimeId is { Length: > 0 }
            ? string.Join('.', runtimeId)
            : null;
        var withinWindow = IsElementWithinWindow(window, focused, rawWalker);
        var xpath = withinWindow
            ? ComputeXPath(window, focused, rawWalker)
            : "/Desktop";
        string? elementId = null;
        if (withinWindow)
        {
            elementId = _elementHandles.RegisterUia(
                window.Properties.NativeWindowHandle.Value.ToInt64(),
                xpath,
                runtimeId,
                focused.ControlType.ToString(),
                GetAutomationId(focused),
                GetName(focused),
                GetClassName(focused));
        }

        identity ??= $"{xpath}|{focused.ControlType}|{GetAutomationId(focused)}|{GetName(focused)}";
        return new UiaKeyboardFocusEvidence(
            BuildElementRefUia(focused, xpath, FindReturnFields.Standard, elementId),
            identity,
            OutsideWindow: !withinWindow);
    }

    private WpfKeyboardFocusEvidence NormalizeWpfKeyboardFocus(
        long windowHandle,
        WpfKeyboardNavigationStepResponse? response)
    {
        if (response?.Focus is not { } focus)
        {
            return new WpfKeyboardFocusEvidence(null, response?.Metadata, null);
        }

        var publicElementId = _elementHandles.RegisterWpf(
            windowHandle,
            focus.XPath,
            focus.ElementIdWpf,
            focus.Type,
            focus.AutomationId,
            focus.Name,
            focus.ClassName,
            focus.Bounds);
        var identity = !string.IsNullOrWhiteSpace(focus.ElementIdWpf)
            ? focus.ElementIdWpf
            : $"{focus.XPath}|{focus.Type}|{focus.AutomationId}|{focus.Name}";
        return new WpfKeyboardFocusEvidence(
            focus with { ElementId = publicElementId, ElementIdWpf = null },
            response.Metadata,
            identity);
    }

    private static KeyboardNavigationStopReason? ClassifyTerminalObservation(
        KeyboardNavigationFocusResult observation,
        long windowHandle,
        KeyboardNavigationTraceMode mode)
    {
        if (!IsWindow(new IntPtr(windowHandle)))
        {
            return KeyboardNavigationStopReason.WindowClosed;
        }

        if (observation.UiaOutsideWindow)
        {
            return KeyboardNavigationStopReason.FocusLeftWindow;
        }

        if (mode == KeyboardNavigationTraceMode.WpfSemantic && observation.SemanticInteropBoundary)
        {
            return KeyboardNavigationStopReason.SemanticInteropBoundary;
        }

        return observation.Identity.IsAvailable
            ? null
            : KeyboardNavigationStopReason.FocusUnavailable;
    }

    private static async Task<WpfKeyboardNavigationStepResponse?> TryReadWpfFocusAsync(
        AgentClient? client,
        long windowHandle,
        KeyboardNavigationDirection direction,
        bool required,
        CancellationToken cancellationToken)
    {
        if (client is null)
        {
            return null;
        }

        try
        {
            return await client.CallAsync<WpfKeyboardNavigationStepResponse>(
                AgentProtocolCapabilities.KeyboardNavigationStep,
                new WpfKeyboardNavigationStepRequest(windowHandle, direction, Move: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (!required)
        {
            return null;
        }
    }

    private void EnsureKeyboardNavigationCapability(AgentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!AgentSupportsCapability(client, AgentProtocolCapabilities.KeyboardNavigationStep))
        {
            throw new InvalidOperationException(
                "agent_capability_unavailable: trace_keyboard_navigation mode=WpfSemantic requires the current WPF agent. " +
                "Restart the target application and reconnect so the current agent can be injected.");
        }
    }

    private async Task<KeyboardNavigationRestoration> RestoreKeyboardNavigationFocusAsync(
        bool requested,
        AutomationElement? originalUiaFocus,
        WpfKeyboardNavigationStepResponse? originalWpfFocus,
        AgentClient? agentClient,
        long windowHandle,
        FlaUI.Core.AutomationBase automation,
        CancellationToken cancellationToken)
    {
        if (!requested)
        {
            return BuildKeyboardNavigationRestoration(
                requested: false,
                attempted: false,
                restored: false,
                methodUsed: null,
                failures: null);
        }

        var failures = new List<string>(2);
        var attempted = false;
        var originalWpfElementId = originalWpfFocus?.Focus?.ElementIdWpf;
        if (agentClient is not null &&
            !string.IsNullOrWhiteSpace(originalWpfElementId) &&
            IsWindow(new IntPtr(windowHandle)))
        {
            try
            {
                attempted = true;
                var response = await agentClient.CallAsync<FocusWpfElementResponse>(
                    AgentProtocolCapabilities.FocusElement,
                    new FocusWpfElementRequest(
                        WindowHandle: windowHandle,
                        ElementId: originalWpfElementId,
                        MaxNodes: 1),
                    cancellationToken).ConfigureAwait(false);
                if (response.Focused)
                {
                    return BuildKeyboardNavigationRestoration(
                        requested: true,
                        attempted: true,
                        restored: true,
                        methodUsed: response.MethodUsed,
                        failures: null);
                }

                failures.Add("WPF focus restoration did not report success.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(GetInternalFailureMessage(ex));
            }
        }

        if (originalUiaFocus is not null)
        {
            try
            {
                attempted = true;
                originalUiaFocus.Focus();
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                var restored = TryGetFocusedElement(automation);
                if (restored is not null && AreSameElement(originalUiaFocus, restored))
                {
                    return BuildKeyboardNavigationRestoration(
                        requested: true,
                        attempted: true,
                        restored: true,
                        methodUsed: "uia_focus",
                        failures: null);
                }

                failures.Add("UIA focus restoration did not return focus to the original element.");
            }
            catch (Exception ex)
            {
                failures.Add(GetInternalFailureMessage(ex));
            }
        }

        return BuildKeyboardNavigationRestoration(
            requested: true,
            attempted: attempted,
            restored: false,
            methodUsed: null,
            failures: failures);
    }

    private static string GetKeyboardNavigationMethod(
        KeyboardNavigationTraceMode mode,
        KeyboardNavigationDirection direction) =>
        (mode, direction) switch
        {
            (KeyboardNavigationTraceMode.Physical, KeyboardNavigationDirection.Next) => "physical_tab",
            (KeyboardNavigationTraceMode.Physical, KeyboardNavigationDirection.Previous) => "physical_shift_tab",
            (KeyboardNavigationTraceMode.WpfSemantic, KeyboardNavigationDirection.Next) => "wpf_move_focus_next",
            (KeyboardNavigationTraceMode.WpfSemantic, KeyboardNavigationDirection.Previous) => "wpf_move_focus_previous",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown keyboard navigation mode or direction.")
        };

    private static IReadOnlyList<KeyboardInputChord> BuildPhysicalKeyboardNavigationStep(
        KeyboardNavigationDirection direction)
    {
        IReadOnlyList<KeyboardModifier>? modifiers = direction == KeyboardNavigationDirection.Previous
            ? [KeyboardModifier.Shift]
            : null;
        return KeyboardInputEngine.BuildSequence([new KeyStroke(KeyboardKey.Tab, modifiers)]);
    }

    private static int ElapsedMilliseconds(long startedAt) =>
        Math.Max(
            0,
            (int)Math.Round(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                MidpointRounding.AwayFromZero));

    internal static async Task<T?> SelectKeyboardNavigationAgentAsync<T>(
        KeyboardNavigationTraceMode mode,
        Func<Task<T?>> connectExisting,
        Func<Task<T>> connectOrInject)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(connectExisting);
        ArgumentNullException.ThrowIfNull(connectOrInject);
        return mode switch
        {
            KeyboardNavigationTraceMode.Physical => await connectExisting().ConfigureAwait(false),
            KeyboardNavigationTraceMode.WpfSemantic => await connectOrInject().ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown keyboard navigation mode.")
        };
    }

    internal static int NormalizeKeyboardNavigationMaxSteps(int requested) =>
        Math.Clamp(requested, 1, 100);

    internal static KeyboardNavigationRestoration BuildKeyboardNavigationRestoration(
        bool requested,
        bool attempted,
        bool restored,
        string? methodUsed,
        IEnumerable<string>? failures)
    {
        var failure = failures?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new KeyboardNavigationRestoration(
            requested,
            attempted,
            restored,
            methodUsed,
            restored
                ? null
                : failure is { Length: > 0 }
                    ? string.Join(" ", failure)
                    : requested
                        ? "original_focus_unavailable"
                        : null);
    }

    private sealed record KeyboardNavigationFocusResult(
        KeyboardNavigationFocusObservation? Public,
        KeyboardNavigationFocusIdentity Identity,
        bool UiaOutsideWindow,
        bool SemanticInteropBoundary,
        bool WpfEvidenceCallSucceeded)
    {
        internal static KeyboardNavigationFocusResult Unavailable { get; } = new(
            Public: null,
            Identity: new KeyboardNavigationFocusIdentity(null, null),
            UiaOutsideWindow: false,
            SemanticInteropBoundary: false,
            WpfEvidenceCallSucceeded: false);
    }

    private sealed record UiaKeyboardFocusEvidence(
        ElementRef? Element,
        string? Identity,
        bool OutsideWindow)
    {
        public static UiaKeyboardFocusEvidence None { get; } = new(null, null, false);
    }

    private sealed record WpfKeyboardFocusEvidence(
        ElementRef? Element,
        WpfKeyboardNavigationMetadata? Metadata,
        string? Identity);
}

internal sealed record KeyboardNavigationFocusIdentity(string? Uia, string? Wpf)
{
    internal bool IsAvailable => !string.IsNullOrWhiteSpace(Wpf) || !string.IsNullOrWhiteSpace(Uia);

    internal bool Matches(KeyboardNavigationFocusIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var compared = false;
        if (!string.IsNullOrWhiteSpace(Wpf) && !string.IsNullOrWhiteSpace(other.Wpf))
        {
            compared = true;
            if (!string.Equals(Wpf, other.Wpf, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Uia) && !string.IsNullOrWhiteSpace(other.Uia))
        {
            compared = true;
            if (!string.Equals(Uia, other.Uia, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return compared;
    }
}

internal sealed class KeyboardNavigationTraceState
{
    private readonly List<KeyboardNavigationFocusIdentity> _observed = [];

    internal KeyboardNavigationTraceState(KeyboardNavigationFocusIdentity start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.IsAvailable)
        {
            _observed.Add(start);
        }
    }

    internal bool HasStart => _observed.Count > 0;

    internal KeyboardNavigationTraceTransition Add(KeyboardNavigationFocusIdentity observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!observed.IsAvailable)
        {
            return new KeyboardNavigationTraceTransition(
                KeyboardNavigationStopReason.FocusUnavailable,
                FocusChanged: false);
        }

        if (_observed[^1].Matches(observed))
        {
            return new KeyboardNavigationTraceTransition(
                KeyboardNavigationStopReason.NoFocusChange,
                FocusChanged: false);
        }

        if (_observed.Any(previous => previous.Matches(observed)))
        {
            return new KeyboardNavigationTraceTransition(
                KeyboardNavigationStopReason.CycleDetected,
                FocusChanged: true);
        }

        _observed.Add(observed);
        return new KeyboardNavigationTraceTransition(null, FocusChanged: true);
    }
}

internal sealed record KeyboardNavigationTraceTransition(
    KeyboardNavigationStopReason? StopReason,
    bool FocusChanged);
