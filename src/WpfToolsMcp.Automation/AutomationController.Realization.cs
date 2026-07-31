using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    internal enum RealizedItemIdentityStatus
    {
        Verified,
        IdentityUnavailable,
        IdentityChanged,
        IdentityRecycled,
        ProcessChanged,
        WindowChanged
    }

    public async Task<RealizeItemResponse> RealizeItemAsync(
        ElementLocator? containerLocator,
        string? containerElementId,
        int? index,
        string? name,
        long? windowHandle = null,
        int maxProviderCalls = RealizeItemLimits.DefaultMaxProviderCalls,
        int advisoryElapsedLimitMs = RealizeItemLimits.DefaultAdvisoryElapsedLimitMs,
        int pollIntervalMs = RealizeItemLimits.DefaultPollIntervalMs,
        CancellationToken cancellationToken = default)
    {
        var request = new RealizeItemRequest(
            ContainerLocator: containerLocator,
            ContainerElementId: containerElementId,
            Index: index,
            Name: name,
            WindowHandle: windowHandle,
            MaxProviderCalls: maxProviderCalls,
            AdvisoryElapsedLimitMs: advisoryElapsedLimitMs,
            PollIntervalMs: pollIntervalMs);
        ValidateRealizeItemArguments(request);

        var trace = BeginTraceSpan("realize_item");
        var started = Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var application = EnsureAttached();
            var automation = EnsureAutomation();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();

            var hasContainerElementId = !string.IsNullOrWhiteSpace(containerElementId);
            Window window;
            AutomationElement container;
            if (hasContainerElementId)
            {
                var id = containerElementId!.Trim();
                var handle = RequireHandle(id);
                if (handle.Backend != InspectionBackend.Uia ||
                    !id.StartsWith("uia_", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"containerElementId '{id}' is not a UIA handle.");
                }

                if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the containerElementId window.");
                }

                try
                {
                    window = FindWindowByHandle(application, automation, handle.WindowHandle);
                }
                catch
                {
                    throw new InvalidOperationException(
                        $"stale_element: window_closed for '{id}'. Call resolve_element again.");
                }

                container = ResolveUiaElementById(
                    window,
                    rawWalker,
                    id,
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else
            {
                window = windowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                try
                {
                    container = ResolveElement(
                        window,
                        containerLocator!,
                        controlWalker,
                        rawWalker,
                        ActionKind.Inspect);
                }
                catch (UiaLocatorAmbiguousException ex)
                {
                    var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();
                    throw BuildUiaAmbiguityException(window, rawWalker, hwnd, ex);
                }
            }

            var windowHandleUsed = window.Properties.NativeWindowHandle.Value.ToInt64();
            IItemContainerPattern? itemContainerPattern;
            try
            {
                itemContainerPattern = container.Patterns.ItemContainer.PatternOrDefault;
            }
            catch (Exception ex)
            {
                var failureResponse = CreatePreRealizationResponse(
                    request,
                    windowHandleUsed,
                    started,
                    RealizeItemOutcomes.StopProviderFailure,
                    "Retry after the UI Automation provider is responsive.",
                    ClassifyRealizeItemFailure(ex));
                trace?.SetSummary($"method={failureResponse.MethodUsed} stop={failureResponse.StopReason}");
                return failureResponse;
            }

            if (itemContainerPattern is null)
            {
                var unsupportedResponse = CreatePreRealizationResponse(
                    request,
                    windowHandleUsed,
                    started,
                    RealizeItemOutcomes.StopUnsupported,
                    "The resolved container does not support ItemContainerPattern.");
                trace?.SetSummary($"method={unsupportedResponse.MethodUsed} stop={unsupportedResponse.StopReason}");
                return unsupportedResponse;
            }

            var provider = new UiaRealizeItemProvider(
                this,
                automation,
                window,
                rawWalker,
                itemContainerPattern,
                windowHandleUsed,
                application.ProcessId);
            var response = await RealizeItemCoordinator.ExecuteAsync(
                    request,
                    windowHandleUsed,
                    provider,
                    ClassifyRealizeItemFailure,
                    cancellationToken)
                .ConfigureAwait(false);

            trace?.SetSummary(
                $"method={response.MethodUsed} stop={response.StopReason} " +
                $"realizeInvoked={response.RealizeInvoked} reusable={response.Reusable}");
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

    internal static RealizedItemIdentityStatus ClassifyRealizedItemIdentity(
        long expectedWindowHandle,
        long? currentWindowHandle,
        int expectedProcessId,
        int? targetProcessIdBeforePath,
        int? targetProcessIdAfterPath,
        int? resolvedProcessId,
        int[]? targetRuntimeIdBeforePath,
        int[]? targetRuntimeIdAfterPath,
        int[]? resolvedRuntimeId,
        bool targetWithinWindow,
        bool resolvedWithinWindow)
    {
        if (currentWindowHandle is not null &&
            (currentWindowHandle.Value != expectedWindowHandle ||
             !targetWithinWindow ||
             !resolvedWithinWindow))
        {
            return RealizedItemIdentityStatus.WindowChanged;
        }

        if (currentWindowHandle is null ||
            targetProcessIdBeforePath is null ||
            targetProcessIdAfterPath is null ||
            resolvedProcessId is null ||
            targetRuntimeIdBeforePath is not { Length: > 0 } ||
            targetRuntimeIdAfterPath is not { Length: > 0 } ||
            resolvedRuntimeId is not { Length: > 0 })
        {
            return RealizedItemIdentityStatus.IdentityUnavailable;
        }

        if (targetProcessIdBeforePath.Value != expectedProcessId ||
            targetProcessIdAfterPath.Value != expectedProcessId ||
            resolvedProcessId.Value != expectedProcessId)
        {
            return RealizedItemIdentityStatus.ProcessChanged;
        }

        if (!targetRuntimeIdBeforePath.SequenceEqual(targetRuntimeIdAfterPath))
        {
            return RealizedItemIdentityStatus.IdentityChanged;
        }

        return targetRuntimeIdAfterPath.SequenceEqual(resolvedRuntimeId)
            ? RealizedItemIdentityStatus.Verified
            : RealizedItemIdentityStatus.IdentityRecycled;
    }

    internal static RealizeItemTargetState ClassifyRealizeItemTargetState(
        bool isWithinWindow,
        bool supportsVirtualizedItemPattern) =>
        supportsVirtualizedItemPattern
            ? RealizeItemTargetState.Virtualized
            : isWithinWindow
                ? RealizeItemTargetState.AlreadyRealized
                : RealizeItemTargetState.Unsupported;

    private static void ValidateRealizeItemArguments(RealizeItemRequest request)
    {
        var hasContainerLocator = request.ContainerLocator is not null;
        var hasContainerElementId = !string.IsNullOrWhiteSpace(request.ContainerElementId);
        if (hasContainerLocator == hasContainerElementId)
        {
            throw new ArgumentException(
                "realize_item requires exactly one of: containerLocator OR containerElementId.",
                nameof(request));
        }

        var hasIndex = request.Index is not null;
        var hasName = request.Name is not null;
        if (hasIndex == hasName)
        {
            throw new ArgumentException(
                "Exactly one provider-order index or exact UIA Name selector is required.",
                nameof(request));
        }

        if (request.Index is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Index,
                "The provider-order index must be zero or greater.");
        }

        if (request.Name is { Length: 0 })
        {
            throw new ArgumentException(
                "The exact UIA Name selector must contain at least one character.",
                nameof(request));
        }

        ValidateRealizeItemRange(
            request.MaxProviderCalls,
            RealizeItemLimits.MinimumProviderCalls,
            RealizeItemLimits.MaximumProviderCalls,
            nameof(request.MaxProviderCalls));
        ValidateRealizeItemRange(
            request.AdvisoryElapsedLimitMs,
            RealizeItemLimits.MinimumAdvisoryElapsedLimitMs,
            RealizeItemLimits.MaximumAdvisoryElapsedLimitMs,
            nameof(request.AdvisoryElapsedLimitMs));
        ValidateRealizeItemRange(
            request.PollIntervalMs,
            RealizeItemLimits.MinimumPollIntervalMs,
            RealizeItemLimits.MaximumPollIntervalMs,
            nameof(request.PollIntervalMs));
    }

    private static void ValidateRealizeItemRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}.");
        }
    }

    private static RealizeItemResponse CreatePreRealizationResponse(
        RealizeItemRequest request,
        long windowHandleUsed,
        long started,
        string stopReason,
        string recoveryReason,
        FailureInfo? failure = null) =>
        new(
            RequestedIdentity: new RealizeItemRequestedIdentity(request.Index, request.Name),
            MethodUsed: RealizeItemOutcomes.MethodNone,
            RealizeInvoked: false,
            PostconditionVerified: false,
            FindItemByPropertyCalls: 0,
            PostconditionPolls: 0,
            ElapsedMs: Math.Max(
                0,
                (long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds)),
            StopReason: stopReason,
            ViewportMayHaveChanged: false,
            DataOrContainerLoadingMayHaveOccurred: false,
            Reusable: false,
            WindowHandleUsed: windowHandleUsed,
            RecoveryReason: recoveryReason,
            Failure: failure);

    private static FailureInfo ClassifyRealizeItemFailure(Exception exception) =>
        FailureDiagnostics.WithDiagnosticCause(
            FailureDiagnostics.Create(
                code: "uia_provider_operation_failed",
                stage: FailureDiagnostics.Stages.Protocol,
                detail: "The UI Automation provider could not complete the requested realization operation.",
                retryable: true,
                recoveryActions: [FailureDiagnostics.Recovery.Retry]),
            exception);

    private static FailureInfo ClassifyRealizeItemRegistrationFailure(Exception exception) =>
        FailureDiagnostics.WithDiagnosticCause(
            FailureDiagnostics.Create(
                code: "element_handle_registration_failed",
                stage: FailureDiagnostics.Stages.Protocol,
                detail: "The realized UI Automation element could not be registered as a reusable handle.",
                retryable: true,
                recoveryActions: [FailureDiagnostics.Recovery.Retry]),
            exception);

    private static int? TryGetRealizationProcessId(AutomationElement element)
    {
        try
        {
            var processId = element.Properties.ProcessId.Value;
            return processId > 0 ? processId : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetRealizationWindowHandle(Window window)
    {
        try
        {
            var handle = window.Properties.NativeWindowHandle.Value.ToInt64();
            return handle != 0 ? handle : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class UiaRealizeItemProvider : IRealizeItemProvider<AutomationElement>
    {
        private readonly AutomationController _controller;
        private readonly UIA3Automation _automation;
        private readonly Window _window;
        private readonly ITreeWalker _rawWalker;
        private readonly IItemContainerPattern _itemContainerPattern;
        private readonly long _windowHandle;
        private readonly int _processId;

        public UiaRealizeItemProvider(
            AutomationController controller,
            UIA3Automation automation,
            Window window,
            ITreeWalker rawWalker,
            IItemContainerPattern itemContainerPattern,
            long windowHandle,
            int processId)
        {
            _controller = controller;
            _automation = automation;
            _window = window;
            _rawWalker = rawWalker;
            _itemContainerPattern = itemContainerPattern;
            _windowHandle = windowHandle;
            _processId = processId;
        }

        public AutomationElement? FindNext(AutomationElement? startAfter) =>
            _itemContainerPattern.FindItemByProperty(startAfter, property: null, value: null);

        public AutomationElement? FindByExactName(AutomationElement? startAfter, string exactName) =>
            _itemContainerPattern.FindItemByProperty(
                startAfter,
                _automation.PropertyLibrary.Element.Name,
                exactName);

        public RealizeItemTargetState GetTargetState(AutomationElement target)
        {
            var supportsVirtualizedItemPattern =
                target.Patterns.VirtualizedItem.PatternOrDefault is not null;
            var isWithinWindow = !supportsVirtualizedItemPattern &&
                                 IsElementWithinWindow(_window, target, _rawWalker);
            return ClassifyRealizeItemTargetState(
                isWithinWindow,
                supportsVirtualizedItemPattern);
        }

        public void Realize(AutomationElement target)
        {
            var pattern = target.Patterns.VirtualizedItem.PatternOrDefault
                ?? throw new InvalidOperationException(
                    "The provider item no longer supports VirtualizedItemPattern.");
            pattern.Realize();
        }

        public ValueTask<RealizeItemPostconditionResult> CheckPostconditionAsync(
            AutomationElement target)
        {
            if (!IsElementWithinWindow(_window, target, _rawWalker))
            {
                return ValueTask.FromResult(RealizeItemPostconditionResult.Pending());
            }

            var targetProcessIdBeforePath = TryGetRealizationProcessId(target);
            var targetRuntimeIdBeforePath = TryGetRuntimeId(target);

            string xpath;
            AutomationElement? resolved;
            try
            {
                xpath = ComputeXPath(_window, target, _rawWalker);
                resolved = TryResolveByXPath(
                    _window,
                    new ElementLocator(XPath: xpath),
                    _rawWalker);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return ValueTask.FromResult(RealizeItemPostconditionResult.Pending());
            }

            if (resolved is null)
            {
                return ValueTask.FromResult(RealizeItemPostconditionResult.Pending());
            }

            var targetWithinWindow = IsElementWithinWindow(_window, target, _rawWalker);
            var resolvedWithinWindow = IsElementWithinWindow(_window, resolved, _rawWalker);
            var currentWindowHandle = TryGetRealizationWindowHandle(_window);
            var targetProcessIdAfterPath = TryGetRealizationProcessId(target);
            var resolvedProcessId = TryGetRealizationProcessId(resolved);
            var targetRuntimeIdAfterPath = TryGetRuntimeId(target);
            var resolvedRuntimeId = TryGetRuntimeId(resolved);
            var identityStatus = ClassifyRealizedItemIdentity(
                _windowHandle,
                currentWindowHandle,
                _processId,
                targetProcessIdBeforePath,
                targetProcessIdAfterPath,
                resolvedProcessId,
                targetRuntimeIdBeforePath,
                targetRuntimeIdAfterPath,
                resolvedRuntimeId,
                targetWithinWindow,
                resolvedWithinWindow);

            if (identityStatus != RealizedItemIdentityStatus.Verified)
            {
                return ValueTask.FromResult(CreateIdentityFailureResult(identityStatus, target, xpath));
            }

            ElementRef element;
            try
            {
                element = BuildElementRefUia(
                    resolved,
                    xpath,
                    FindReturnFields.Standard,
                    elementId: null,
                    TryGetClientBoundsScreen(_window, out var viewportBounds) ? viewportBounds : null);
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(
                    RealizeItemPostconditionResult.Verified(
                        element: null,
                        reusable: false,
                        stopReason: RealizeItemOutcomes.StopRegistrationFailed,
                        recoveryReason: "Resolve the realized item again to obtain a reusable handle.",
                        failure: ClassifyRealizeItemRegistrationFailure(ex)));
            }

            try
            {
                var elementId = _controller._elementHandles.RegisterUia(
                    _windowHandle,
                    xpath,
                    resolvedRuntimeId!,
                    element.Type,
                    element.AutomationId,
                    element.Name,
                    element.ClassName,
                    element.Bounds);
                return ValueTask.FromResult(
                    RealizeItemPostconditionResult.Verified(
                        element with { ElementId = elementId },
                        reusable: true));
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(
                    RealizeItemPostconditionResult.Verified(
                        element,
                        reusable: false,
                        stopReason: RealizeItemOutcomes.StopRegistrationFailed,
                        recoveryReason: "Resolve the realized item again to obtain a reusable handle.",
                        failure: ClassifyRealizeItemRegistrationFailure(ex)));
            }
        }

        private static RealizeItemPostconditionResult CreateIdentityFailureResult(
            RealizedItemIdentityStatus status,
            AutomationElement resolved,
            string xpath)
        {
            ElementRef? element = null;
            try
            {
                element = BuildElementRefUia(
                    resolved,
                    xpath,
                    FindReturnFields.Standard,
                    elementId: null);
            }
            catch
            {
            }

            var (stopReason, recoveryReason) = status switch
            {
                RealizedItemIdentityStatus.IdentityUnavailable => (
                    RealizeItemOutcomes.StopIdentityUnavailable,
                    "Resolve the item again once its UIA identity is available."),
                RealizedItemIdentityStatus.IdentityChanged => (
                    RealizeItemOutcomes.StopIdentityChanged,
                    "Resolve the item again before interacting with it."),
                RealizedItemIdentityStatus.IdentityRecycled => (
                    RealizeItemOutcomes.StopIdentityRecycled,
                    "Resolve the item again before interacting with it."),
                RealizedItemIdentityStatus.ProcessChanged => (
                    RealizeItemOutcomes.StopProcessChanged,
                    "Reattach to the current process and realize the item again."),
                RealizedItemIdentityStatus.WindowChanged => (
                    RealizeItemOutcomes.StopWindowChanged,
                    "Resolve the current window and realize the item again."),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
            };

            return RealizeItemPostconditionResult.Verified(
                element,
                reusable: false,
                stopReason,
                recoveryReason);
        }
    }
}
