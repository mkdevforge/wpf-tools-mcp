using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private const int MaxWaitTimeoutMs = 60_000;
    private const int MinWaitPollIntervalMs = 25;
    private const int MaxWaitPollIntervalMs = 2_000;
    private const int MaxWaitHoldMs = 5_000;
    private const int MaxWaitDesktopWindowsScanned = 2_048;
    private const int MaxWaitWindowCandidates = 128;
    private const int MaxWaitWindowFrameworkProbes = 16;

    private async Task<WaitForResponse> WaitForConditionAsync(
        WaitForRequest request,
        CancellationToken cancellationToken)
    {
        var condition = request.Condition
            ?? throw new ArgumentException("wait_for structured condition is required.");

        ValidateStructuredWaitDoesNotMixLegacyArguments(request);

        var response = condition.Kind switch
        {
            WaitConditionKind.DependencyPropertyValue or WaitConditionKind.DataContextValue =>
                await WaitForWpfValueConditionAsync(request, condition, cancellationToken).ConfigureAwait(false),
            WaitConditionKind.WindowOpen or WaitConditionKind.WindowClosed =>
                await WaitForWindowConditionAsync(request, condition, cancellationToken).ConfigureAwait(false),
            _ => await WaitForElementConditionAsync(request, condition, cancellationToken).ConfigureAwait(false)
        };

        if (response.LastObservation?.WindowHandle is long windowHandle)
        {
            TrackOrRejectExternalWindowHandle(windowHandle);
        }

        if (response.LastObservation?.OwnerHandle is long ownerHandle)
        {
            TrackOrRejectExternalWindowHandle(ownerHandle);
        }

        return response;
    }

    private static void ValidateStructuredWaitDoesNotMixLegacyArguments(WaitForRequest request)
    {
        if (request.ExpectedValue is not null || request.ExpectedText is not null)
        {
            throw new ArgumentException(
                "wait_for structured conditions use condition.expected; expectedValue and expectedText are legacy-only.");
        }
    }

    private async Task<WaitForResponse> WaitForElementConditionAsync(
        WaitForRequest request,
        WaitCondition condition,
        CancellationToken cancellationToken)
    {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException(
                "wait_for element conditions require exactly one of: locator OR elementId.");
        }

        if (condition.PropertyName is not null ||
            condition.DataContextPath is not null ||
            condition.Window is not null)
        {
            throw new ArgumentException(
                $"condition kind {condition.Kind} does not accept propertyName, dataContextPath, or window.");
        }

        var (state, expectedValue, expectedText, stableMs) = condition.Kind switch
        {
            WaitConditionKind.Attached => ("attached", (double?)null, (string?)null, request.StableMs),
            WaitConditionKind.Visible => ("visible", (double?)null, (string?)null, request.StableMs),
            WaitConditionKind.Enabled => ("enabled", (double?)null, (string?)null, request.StableMs),
            WaitConditionKind.Actionable => ("actionable", (double?)null, (string?)null, request.StableMs),
            WaitConditionKind.BoundsStable =>
                ("stable", (double?)null, (string?)null, ValidateHoldForMs(condition.HoldForMs ?? request.StableMs)),
            WaitConditionKind.NumericValueEquals =>
                ("value_equals", RequireExpectedNumber(condition), (string?)null, request.StableMs),
            WaitConditionKind.NameContains =>
                ("name_contains", (double?)null, RequireExpectedString(condition), request.StableMs),
            _ => throw new ArgumentException(
                $"condition kind {condition.Kind} is not an element-state condition.")
        };

        if (condition.Kind != WaitConditionKind.BoundsStable && condition.HoldForMs is not null)
        {
            throw new ArgumentException(
                "condition.holdForMs is supported by BoundsStable and WPF property/DataContext value conditions only.");
        }

        ValidateElementConditionComparison(condition);

        var legacyRequest = request with
        {
            State = state,
            StableMs = stableMs,
            ExpectedValue = expectedValue,
            ExpectedText = expectedText,
            ThrowOnTimeout = false,
            Condition = null
        };

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, MaxWaitTimeoutMs);
        var stateName = GetConditionStateName(condition.Kind);
        var structuredStart = Stopwatch.GetTimestamp();
        using var deadlineCts = timeoutMs > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        deadlineCts?.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var waitCancellationToken = deadlineCts?.Token ?? cancellationToken;
        var deadline = new StructuredElementWaitDeadline(
            structuredStart,
            cancellationToken,
            waitCancellationToken);

        try
        {
            var response = await WaitForAsync(
                legacyRequest,
                waitCancellationToken,
                deadline).ConfigureAwait(false);
            if (response.Succeeded &&
                timeoutMs > 0 &&
                GetElapsedMilliseconds(structuredStart) >= timeoutMs)
            {
                return CreateStructuredTimeoutResponse(
                    request,
                    stateName,
                    response.BackendUsed ?? WaitBackendForRequest(request),
                    timeoutMs,
                    structuredStart,
                    response.Attempts,
                    response.LastObservation,
                    response.LastObservedValue,
                    "condition_met_after_timeout");
            }

            var structuredResponse = response with
            {
                State = stateName,
                ReasonCode = response.Succeeded
                    ? null
                    : response.ReasonCode ?? "wait_timeout",
                LastObservedValue = response.LastObservedValue ??
                    CreateUnavailableObservedValue("condition_not_observed")
            };

            if (!structuredResponse.Succeeded &&
                request.ThrowOnTimeout &&
                string.Equals(structuredResponse.ReasonCode, "wait_timeout", StringComparison.Ordinal))
            {
                return CreateStructuredTimeoutResponse(
                    request,
                    stateName,
                    structuredResponse.BackendUsed ?? WaitBackendForRequest(request),
                    timeoutMs,
                    structuredStart,
                    structuredResponse.Attempts,
                    structuredResponse.LastObservation,
                    structuredResponse.LastObservedValue,
                    structuredResponse.FailureReason ?? "condition_not_met");
            }

            return structuredResponse;
        }
        catch (OperationCanceledException) when (
            deadlineCts?.IsCancellationRequested == true &&
            !cancellationToken.IsCancellationRequested)
        {
            return CreateStructuredTimeoutResponse(
                request,
                stateName,
                WaitBackendForRequest(request),
                timeoutMs,
                structuredStart,
                attempts: 0,
                lastObservation: null,
                lastObservedValue: CreateUnavailableObservedValue("condition_not_observed"),
                failureReason: "condition_not_observed");
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException &&
            !IsAttached)
        {
            return CreateTargetProcessExitedResponse(
                stateName,
                WaitBackendForRequest(request),
                elapsedMs: GetElapsedMilliseconds(structuredStart),
                attempts: 0,
                lastObservation: null,
                lastObservedValue: CreateUnavailableObservedValue("condition_not_observed"));
        }
    }

    private static void ValidateElementConditionComparison(WaitCondition condition)
    {
        switch (condition.Kind)
        {
            case WaitConditionKind.NumericValueEquals:
                if (condition.Comparison is not null and not WaitComparison.Equals)
                {
                    throw new ArgumentException("NumericValueEquals only supports comparison=Equals.");
                }

                return;
            case WaitConditionKind.NameContains:
                if (condition.Comparison is not null and not WaitComparison.Contains)
                {
                    throw new ArgumentException("NameContains only supports comparison=Contains.");
                }

                return;
            default:
                if (condition.Comparison is not null || condition.Expected is not null)
                {
                    throw new ArgumentException(
                        $"condition kind {condition.Kind} does not accept comparison or expected.");
                }

                return;
        }
    }

    private static double RequireExpectedNumber(WaitCondition condition)
    {
        var expected = condition.Expected
            ?? throw new ArgumentException($"condition.expected is required for {condition.Kind}.");
        if (expected.Kind != WaitScalarKind.Number ||
            expected.NumberValue is not double number ||
            !double.IsFinite(number) ||
            expected.StringValue is not null ||
            expected.BooleanValue is not null)
        {
            throw new ArgumentException(
                $"condition.expected for {condition.Kind} must contain only a finite numberValue.");
        }

        return number;
    }

    private static string RequireExpectedString(WaitCondition condition)
    {
        var expected = condition.Expected
            ?? throw new ArgumentException($"condition.expected is required for {condition.Kind}.");
        if (expected.Kind != WaitScalarKind.String ||
            expected.StringValue is null ||
            expected.NumberValue is not null ||
            expected.BooleanValue is not null)
        {
            throw new ArgumentException(
                $"condition.expected for {condition.Kind} must contain only stringValue.");
        }

        return expected.StringValue;
    }

    private async Task<WaitForResponse> WaitForWpfValueConditionAsync(
        WaitForRequest request,
        WaitCondition condition,
        CancellationToken cancellationToken)
    {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException(
                "DependencyPropertyValue and DataContextValue require exactly one of: locator OR elementId.");
        }

        if (request.Backend == InspectionBackend.Uia)
        {
            throw new ArgumentException(
                $"wait_backend_unsupported: {condition.Kind} requires backend=Wpf or backend=Auto on a WPF window.");
        }

        if (condition.Window is not null)
        {
            throw new ArgumentException($"condition kind {condition.Kind} does not accept condition.window.");
        }

        var source = condition.Kind == WaitConditionKind.DependencyPropertyValue
            ? ObserveStateSource.DependencyProperty
            : ObserveStateSource.DataContextPath;
        var path = condition.Kind switch
        {
            WaitConditionKind.DependencyPropertyValue
                when !string.IsNullOrWhiteSpace(condition.PropertyName) && condition.DataContextPath is null =>
                condition.PropertyName.Trim(),
            WaitConditionKind.DataContextValue
                when !string.IsNullOrWhiteSpace(condition.DataContextPath) && condition.PropertyName is null =>
                condition.DataContextPath.Trim(),
            WaitConditionKind.DependencyPropertyValue => throw new ArgumentException(
                "DependencyPropertyValue requires propertyName and does not accept dataContextPath."),
            _ => throw new ArgumentException(
                "DataContextValue requires dataContextPath and does not accept propertyName.")
        };

        var expected = condition.Expected
            ?? throw new ArgumentException($"condition.expected is required for {condition.Kind}.");
        if (!WaitConditionEvaluator.TryValidateScalar(expected, out var scalarError))
        {
            throw new ArgumentException($"invalid condition.expected: {scalarError}");
        }

        var comparison = condition.Comparison ?? WaitComparison.Equals;
        if (!WaitConditionEvaluator.TryValidateComparison(comparison, expected, out var comparisonError))
        {
            throw new ArgumentException($"invalid condition.comparison: {comparisonError}");
        }

        var holdForMs = ValidateHoldForMs(condition.HoldForMs ?? 0);
        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, MaxWaitTimeoutMs);
        var pollIntervalMs = Math.Clamp(
            request.PollIntervalMs,
            MinWaitPollIntervalMs,
            MaxWaitPollIntervalMs);
        var stateName = GetConditionStateName(condition.Kind);
        var start = Stopwatch.GetTimestamp();
        using var deadlineCts = timeoutMs > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        deadlineCts?.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var waitCancellationToken = deadlineCts?.Token ?? cancellationToken;
        var attempts = 0;
        var failureReason = "not_attached";
        WaitForObservation? lastObservation = null;
        WaitObservedValue? lastObservedValue = CreateUnavailableObservedValue("not_attached");
        WpfStateObservation? observation = null;

        try
        {
            var windowHandle = ResolveWpfValueWaitWindowHandle(request);
            var client = await EnsureAgentConnectedAsync(waitCancellationToken).ConfigureAwait(false);
            EnsureObserveStateCapability(client);

            while (observation is null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if ((timeoutMs > 0 || attempts > 0) &&
                    GetElapsedMilliseconds(start) >= timeoutMs)
                {
                    return CreateStructuredTimeoutResponse(
                        request,
                        stateName,
                        WaitBackend.Wpf,
                        timeoutMs,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        failureReason);
                }

                attempts++;

                if (!IsApplicationRunning(_application))
                {
                    return CreateTargetProcessExitedResponse(
                        stateName,
                        WaitBackend.Wpf,
                        GetElapsedMilliseconds(start),
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }

                try
                {
                    observation = await ObserveStateStartAsync(
                        new ObserveStateStartRequest(
                            WindowHandle: windowHandle,
                            Locator: request.Locator,
                            ElementId: request.ElementId,
                            DependencyProperties: source == ObserveStateSource.DependencyProperty ? [path] : null,
                            DataContextPaths: source == ObserveStateSource.DataContextPath ? [path] : null,
                            MaxNodes: 5_000,
                            DurationMs: Math.Max(1, timeoutMs + 1_000),
                            MaxEvents: 512,
                            MaxValueLength: 512,
                            IncludeVisualMetadata: true,
                            VisibleOnly: false),
                        waitCancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
                {
                    failureReason = "not_attached";
                    lastObservedValue = CreateUnavailableObservedValue(failureReason);
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException &&
                    !IsApplicationRunning(_application))
                {
                    return CreateTargetProcessExitedResponse(
                        stateName,
                        WaitBackend.Wpf,
                        GetElapsedMilliseconds(start),
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException &&
                    !client.IsConnected)
                {
                    return CreateStructuredFailureResponse(
                        stateName,
                        WaitBackend.Wpf,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        "agent_connection_lost",
                        "agent_connection_lost");
                }

                if (observation is not null)
                {
                    break;
                }

                if (GetElapsedMilliseconds(start) >= timeoutMs)
                {
                    return CreateStructuredTimeoutResponse(
                        request,
                        stateName,
                        WaitBackend.Wpf,
                        timeoutMs,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        failureReason);
                }

                var elapsedMs = GetElapsedMilliseconds(start);
                var remainingMs = Math.Max(1, timeoutMs - elapsedMs);
                await Task.Delay(
                    Math.Min(pollIntervalMs, remainingMs),
                    waitCancellationToken).ConfigureAwait(false);
            }

            if (timeoutMs > 0 && GetElapsedMilliseconds(start) >= timeoutMs)
            {
                return CreateStructuredTimeoutResponse(
                    request,
                    stateName,
                    WaitBackend.Wpf,
                    timeoutMs,
                    start,
                    attempts,
                    lastObservation,
                    lastObservedValue,
                    failureReason);
            }

            lastObservation = ToWpfWaitObservation(observation.Started.Element, windowHandle);
            var hold = new ContinuousHoldTracker(holdForMs);
            var currentSatisfied = false;
            var drainImmediately = false;

            foreach (var initialEvent in observation.Started.InitialEvents
                         .Where(item => item.Source == source &&
                                        string.Equals(item.Path, path, StringComparison.Ordinal)))
            {
                var completed = EvaluateWpfWaitEvent(
                    initialEvent,
                    comparison,
                    expected,
                    hold,
                    ref lastObservation,
                    ref lastObservedValue,
                    ref failureReason,
                    ref currentSatisfied);
                if (completed)
                {
                    return CreateStructuredSuccessResponse(
                        stateName,
                        WaitBackend.Wpf,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsApplicationRunning(_application))
                {
                    return CreateTargetProcessExitedResponse(
                        stateName,
                        WaitBackend.Wpf,
                        GetElapsedMilliseconds(start),
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }

                var elapsedMs = GetElapsedMilliseconds(start);
                if (elapsedMs >= timeoutMs)
                {
                    return CreateStructuredTimeoutResponse(
                        request,
                        stateName,
                        WaitBackend.Wpf,
                        timeoutMs,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        failureReason);
                }

                if (!drainImmediately)
                {
                    var remainingMs = Math.Max(1, timeoutMs - elapsedMs);
                    await Task.Delay(
                        Math.Min(pollIntervalMs, remainingMs),
                        waitCancellationToken).ConfigureAwait(false);

                    if (!IsApplicationRunning(_application))
                    {
                        return CreateTargetProcessExitedResponse(
                            stateName,
                            WaitBackend.Wpf,
                            GetElapsedMilliseconds(start),
                            attempts,
                            lastObservation,
                            lastObservedValue);
                    }

                    if (GetElapsedMilliseconds(start) >= timeoutMs)
                    {
                        return CreateStructuredTimeoutResponse(
                            request,
                            stateName,
                            WaitBackend.Wpf,
                            timeoutMs,
                            start,
                            attempts,
                            lastObservation,
                            lastObservedValue,
                            failureReason);
                    }
                }

                attempts++;

                ObserveStatePollResponse poll;
                try
                {
                    poll = await ObserveStatePollAsync(
                        observation,
                        maxBatch: 100,
                        maxPayloadChars: 65_536,
                        waitCancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.StartsWith("observe_state_connection_lost:", StringComparison.Ordinal))
                {
                    var reasonCode = IsApplicationRunning(_application)
                        ? "agent_connection_lost"
                        : "target_process_exited";
                    return CreateStructuredFailureResponse(
                        stateName,
                        WaitBackend.Wpf,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        reasonCode,
                        reasonCode);
                }

                if (!IsApplicationRunning(_application))
                {
                    return CreateTargetProcessExitedResponse(
                        stateName,
                        WaitBackend.Wpf,
                        GetElapsedMilliseconds(start),
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }

                if (GetElapsedMilliseconds(start) >= timeoutMs)
                {
                    return CreateStructuredTimeoutResponse(
                        request,
                        stateName,
                        WaitBackend.Wpf,
                        timeoutMs,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        failureReason);
                }

                drainImmediately = poll.HasMore;

                if (poll.DroppedSinceLastPoll > 0 || poll.CoalescedSinceLastPoll > 0)
                {
                    hold.Reset();
                    currentSatisfied = false;
                    failureReason = "observation_gap";
                }

                foreach (var observationEvent in poll.Events.Where(item =>
                             item.Source == source &&
                             string.Equals(item.Path, path, StringComparison.Ordinal)))
                {
                    var completed = EvaluateWpfWaitEvent(
                        observationEvent,
                        comparison,
                        expected,
                        hold,
                        ref lastObservation,
                        ref lastObservedValue,
                        ref failureReason,
                        ref currentSatisfied);
                    if (completed)
                    {
                        return CreateStructuredSuccessResponse(
                            stateName,
                            WaitBackend.Wpf,
                            start,
                            attempts,
                            lastObservation,
                            lastObservedValue);
                    }
                }

                // Event timestamps and poll duration share the observer's monotonic clock.
                // Only advance a held condition to "now" after draining older queued events.
                if (!poll.HasMore && currentSatisfied && hold.Observe(true, poll.DurationMs))
                {
                    return CreateStructuredSuccessResponse(
                        stateName,
                        WaitBackend.Wpf,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }

                if (poll.Completed && poll.StopReason == ObserveStateStopReason.ElementUnloaded)
                {
                    return CreateStructuredFailureResponse(
                        stateName,
                        WaitBackend.Wpf,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        "target_element_unloaded",
                        "target_element_unloaded");
                }
            }
        }
        catch (OperationCanceledException) when (
            deadlineCts?.IsCancellationRequested == true &&
            !cancellationToken.IsCancellationRequested)
        {
            return CreateStructuredTimeoutResponse(
                request,
                stateName,
                WaitBackend.Wpf,
                timeoutMs,
                start,
                attempts,
                lastObservation,
                lastObservedValue,
                failureReason);
        }
        finally
        {
            if (observation is not null)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ReleaseObserveStateAsync(observation, cleanupCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // The owning pipe releases observations that cannot be explicitly stopped.
                }
            }
        }
    }

    private long ResolveWpfValueWaitWindowHandle(WaitForRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ElementId))
        {
            var handle = RequireHandle(request.ElementId.Trim());
            if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new ArgumentException(
                    "wait_backend_unsupported: dependency-property and DataContext conditions require a WPF elementId.");
            }

            if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            return handle.WindowHandle;
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();
        var window = request.WindowHandle is long requestedWindowHandle
            ? FindWindowByHandle(application, automation, requestedWindowHandle)
            : FindMainWindow(application, automation);

        if (request.Backend == InspectionBackend.Auto && GetAutoBackendRoute(window) == AutoBackendRoute.Uia)
        {
            throw new ArgumentException(
                "wait_backend_unsupported: dependency-property and DataContext conditions require a WPF window.");
        }

        return window.Properties.NativeWindowHandle.Value.ToInt64();
    }

    private static bool EvaluateWpfWaitEvent(
        ObserveStateEvent observationEvent,
        WaitComparison comparison,
        WaitScalar expected,
        ContinuousHoldTracker hold,
        ref WaitForObservation? lastObservation,
        ref WaitObservedValue? lastObservedValue,
        ref string failureReason,
        ref bool currentSatisfied)
    {
        if (observationEvent.CoalescedChangeCount > 0)
        {
            hold.Reset();
        }

        lastObservedValue = WaitConditionEvaluator.FromObserveStateValue(observationEvent.NewValue);
        var evaluation = WaitConditionEvaluator.Evaluate(lastObservedValue, comparison, expected);
        currentSatisfied = evaluation.Satisfied;

        if (observationEvent.Visual is { } visual && lastObservation is not null)
        {
            lastObservation = lastObservation with
            {
                Bounds = visual.Bounds ?? lastObservation.Bounds,
                IsEnabled = visual.IsEnabled ?? lastObservation.IsEnabled,
                IsOffscreen = visual.IsVisible is bool isVisible
                    ? !isVisible
                    : lastObservation.IsOffscreen
            };
        }

        var completed = hold.Observe(evaluation.Satisfied, observationEvent.ElapsedMs);
        failureReason = evaluation.Satisfied && !completed
            ? "hold_duration_not_met"
            : evaluation.FailureReason ?? "value_mismatch";
        return completed;
    }

    private static WaitForObservation ToWpfWaitObservation(ElementRef element, long windowHandle) =>
        new(
            Type: element.Type,
            AutomationId: element.AutomationId,
            Name: element.Name,
            XPath: element.XPath,
            Bounds: element.Bounds,
            IsOffscreen: element.IsOffscreen)
        {
            WindowHandle = windowHandle
        };

    private static WaitObservedValue ObserveLegacyUiaWaitValue(
        AutomationElement element,
        WaitForState state)
    {
        try
        {
            return state switch
            {
                WaitForState.Attached => BooleanObservedValue(true),
                WaitForState.Visible => TryGetIsOffscreen(element) is bool isOffscreen
                    ? BooleanObservedValue(!isOffscreen && HasValidBounds(element))
                    : CreateUnavailableObservedValue("offscreen_unknown"),
                WaitForState.Enabled => SafeGetBool(() => element.Properties.IsEnabled.Value) is bool isEnabled
                    ? BooleanObservedValue(isEnabled)
                    : CreateUnavailableObservedValue("enabled_unknown"),
                WaitForState.Actionable => BooleanObservedValue(
                    HasValidBounds(element) &&
                    TryGetIsOffscreen(element) == false &&
                    SafeGetBool(() => element.Properties.IsEnabled.Value) == true),
                WaitForState.Stable => BoundsObservedValue(TryGetBounds(element)),
                WaitForState.ValueEquals => ObserveUiaNumericValue(element),
                WaitForState.NameContains => StringObservedValue(GetName(element) ?? string.Empty),
                _ => CreateUnavailableObservedValue("value_unavailable")
            };
        }
        catch (Exception ex)
        {
            return new WaitObservedValue(
                WaitObservedValueState.Error,
                Detail: ex.GetBaseException().Message);
        }
    }

    private static WaitObservedValue ObserveLegacyWpfWaitValue(TreeNode node, WaitForState state) =>
        state switch
        {
            WaitForState.Attached => BooleanObservedValue(true),
            WaitForState.Visible => node.IsVisible is bool isVisible
                ? BooleanObservedValue(
                    isVisible && node.Bounds is { Width: > 0, Height: > 0 })
                : CreateUnavailableObservedValue("visible_unknown"),
            WaitForState.Enabled => node.IsEnabled is bool isEnabled
                ? BooleanObservedValue(isEnabled)
                : CreateUnavailableObservedValue("enabled_unknown"),
            WaitForState.Actionable => node.IsVisible is bool visible && node.IsEnabled is bool enabled
                ? BooleanObservedValue(visible && enabled && node.Bounds is { Width: > 0, Height: > 0 })
                : CreateUnavailableObservedValue("actionable_unknown"),
            WaitForState.Stable => BoundsObservedValue(node.Bounds),
            WaitForState.NameContains => StringObservedValue(node.Name ?? string.Empty),
            _ => CreateUnavailableObservedValue("value_unavailable")
        };

    private static WaitObservedValue ObserveWpfComputedValue(
        IReadOnlyList<ComputedPropertyInfo> properties)
    {
        foreach (var propertyName in new[] { "Value", "Text" })
        {
            var property = properties.FirstOrDefault(item =>
                string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            if (property?.Value is null)
            {
                continue;
            }

            if (double.TryParse(
                    property.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                return NumberObservedValue(number);
            }

            return StringObservedValue(property.Value);
        }

        return CreateUnavailableObservedValue("value_unavailable");
    }

    private static WaitObservedValue ObserveWpfNameValue(
        TreeNode node,
        IReadOnlyList<ComputedPropertyInfo> properties,
        string? expectedText)
    {
        var propertyCandidates = new[] { "Text", "Content", "Header", "Name" }
            .Select(propertyName => properties.FirstOrDefault(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        var candidates = new[] { node.Name }
            .Concat(propertyCandidates)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        if (!string.IsNullOrWhiteSpace(expectedText))
        {
            var matching = candidates.FirstOrDefault(value =>
                value.Contains(expectedText, StringComparison.OrdinalIgnoreCase));
            if (matching is not null)
            {
                return StringObservedValue(matching);
            }
        }

        return propertyCandidates.FirstOrDefault() is { } firstProperty
            ? StringObservedValue(firstProperty)
            : candidates.FirstOrDefault() is { } first
            ? StringObservedValue(first)
            : CreateUnavailableObservedValue("value_unavailable");
    }

    private static WaitObservedValue ObserveUiaNumericValue(AutomationElement element)
    {
        var rangeValue = element.Patterns.RangeValue.PatternOrDefault;
        if (rangeValue is not null)
        {
            return NumberObservedValue(rangeValue.Value);
        }

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern is null)
        {
            return CreateUnavailableObservedValue("no_value_pattern");
        }

        var value = valuePattern.Value ?? string.Empty;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? NumberObservedValue(number)
            : StringObservedValue(value);
    }

    private static WaitObservedValue BooleanObservedValue(bool value) =>
        new(
            WaitObservedValueState.Value,
            JsonValue.Create(value),
            ValueType: "System.Boolean");

    private static WaitObservedValue NumberObservedValue(double value) =>
        new(
            WaitObservedValueState.Value,
            JsonValue.Create(value),
            ValueType: "System.Double");

    private static WaitObservedValue StringObservedValue(string value) =>
        new(
            WaitObservedValueState.Value,
            JsonValue.Create(value),
            ValueType: "System.String");

    private static WaitObservedValue BoundsObservedValue(Rect? bounds) =>
        bounds is null
            ? CreateUnavailableObservedValue("invalid_bounds")
            : StringObservedValue(string.Create(
                CultureInfo.InvariantCulture,
                $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}")) with
            {
                ValueType = "bounds"
            };

    private static WaitObservedValue BoundsObservedValue(System.Drawing.Rectangle? bounds) =>
        bounds is null
            ? CreateUnavailableObservedValue("invalid_bounds")
            : StringObservedValue(string.Create(
                CultureInfo.InvariantCulture,
                $"{bounds.Value.Left},{bounds.Value.Top},{bounds.Value.Width},{bounds.Value.Height}")) with
            {
                ValueType = "bounds"
            };

    private async Task<WaitForResponse> WaitForWindowConditionAsync(
        WaitForRequest request,
        WaitCondition condition,
        CancellationToken cancellationToken)
    {
        if (request.Locator is not null || !string.IsNullOrWhiteSpace(request.ElementId))
        {
            throw new ArgumentException("WindowOpen and WindowClosed do not accept locator or elementId.");
        }

        if (condition.PropertyName is not null ||
            condition.DataContextPath is not null ||
            condition.Comparison is not null ||
            condition.Expected is not null ||
            condition.HoldForMs is not null)
        {
            throw new ArgumentException(
                $"condition kind {condition.Kind} only accepts condition.window.");
        }

        var selector = condition.Window
            ?? throw new ArgumentException($"condition.window is required for {condition.Kind}.");
        ValidateWindowSelector(selector);

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, MaxWaitTimeoutMs);
        var pollIntervalMs = Math.Clamp(
            request.PollIntervalMs,
            MinWaitPollIntervalMs,
            MaxWaitPollIntervalMs);
        var application = EnsureAttached();
        var processId = application.ProcessId;
        ValidateWindowSelectorScope(selector, processId);

        var stateName = GetConditionStateName(condition.Kind);
        var start = Stopwatch.GetTimestamp();
        var attempts = 0;
        WaitForObservation? lastObservation = null;
        WaitObservedValue? lastObservedValue = null;
        var automation = EnsureAutomation();
        var failureReason = condition.Kind == WaitConditionKind.WindowOpen
            ? "window_not_open"
            : "window_still_open";
        WaitWindowIdentity? exactIdentity = null;
        var exactSelectorValidated = false;

        WaitForResponse Timeout() => CreateStructuredTimeoutResponse(
            request,
            stateName,
            WaitBackend.Win32,
            timeoutMs,
            start,
            attempts,
            lastObservation,
            lastObservedValue,
            failureReason);

        WaitForResponse TargetExited() => CreateTargetProcessExitedResponse(
            stateName,
            WaitBackend.Win32,
            GetElapsedMilliseconds(start),
            attempts,
            lastObservation,
            lastObservedValue);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempts > 0 && GetElapsedMilliseconds(start) >= timeoutMs)
            {
                return Timeout();
            }

            attempts++;

            if (!IsApplicationRunning(_application))
            {
                return TargetExited();
            }

            if (condition.Kind == WaitConditionKind.WindowOpen)
            {
                var scan = ObserveWaitWindows(processId, selector, automation);
                var current = scan.Matches.FirstOrDefault();
                if (current is null && scan.LastCandidate is not null)
                {
                    lastObservation = ToWaitObservation(scan.LastCandidate);
                    lastObservedValue = ToWindowObservedValue(scan.LastCandidate);
                }

                failureReason = scan.FrameworkMetadataUnavailable
                    ? "window_framework_unavailable"
                    : "window_not_open";

                if (!IsApplicationRunning(_application))
                {
                    return TargetExited();
                }

                if (timeoutMs > 0 && GetElapsedMilliseconds(start) >= timeoutMs)
                {
                    return Timeout();
                }

                if (current is not null)
                {
                    lastObservation = ToWaitObservation(current);
                    lastObservedValue = ToWindowObservedValue(current);
                    return CreateStructuredSuccessResponse(
                        stateName,
                        WaitBackend.Win32,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue);
                }

                lastObservedValue ??= CreateUnavailableObservedValue(failureReason);
            }
            else if (selector.Handle is long handle)
            {
                var requiresFramework = !string.IsNullOrWhiteSpace(selector.FrameworkId);
                var current = ObserveWaitWindow(
                    new IntPtr(handle),
                    processId,
                    automation,
                    requireVisible: false,
                    includeFrameworkId: requiresFramework && !exactSelectorValidated);

                if (!IsApplicationRunning(_application))
                {
                    return TargetExited();
                }

                if (timeoutMs > 0 && GetElapsedMilliseconds(start) >= timeoutMs)
                {
                    return Timeout();
                }

                if (current is null)
                {
                    return CreateStructuredSuccessResponse(
                        stateName,
                        WaitBackend.Win32,
                        start,
                        attempts,
                        lastObservation,
                        CreateUnavailableObservedValue("window_closed"));
                }

                if (exactIdentity is null)
                {
                    exactIdentity = current.Identity;
                    if (!MatchesNativeWindowSelector(current, selector))
                    {
                        throw CreateWindowSelectorMismatchException(handle);
                    }
                }
                else if (!SameWaitWindowIdentity(exactIdentity, current.Identity))
                {
                    return CreateStructuredSuccessResponse(
                        stateName,
                        WaitBackend.Win32,
                        start,
                        attempts,
                        lastObservation,
                        CreateUnavailableObservedValue("window_closed"));
                }

                if (!exactSelectorValidated)
                {
                    if (requiresFramework && !current.FrameworkIdAvailable)
                    {
                        failureReason = "window_framework_unavailable";
                    }
                    else if (requiresFramework && !MatchesFrameworkSelector(current, selector))
                    {
                        throw CreateWindowSelectorMismatchException(handle);
                    }
                    else
                    {
                        exactSelectorValidated = true;
                        failureReason = "window_still_open";
                    }
                }

                lastObservation = ToWaitObservation(current);
                lastObservedValue = ToWindowObservedValue(current);
            }
            else
            {
                var scan = ObserveWaitWindows(processId, selector, automation);
                var current = scan.Matches.FirstOrDefault();
                if (current is null && scan.LastCandidate is not null)
                {
                    lastObservation = ToWaitObservation(scan.LastCandidate);
                    lastObservedValue = ToWindowObservedValue(scan.LastCandidate);
                }

                failureReason = scan.FrameworkMetadataUnavailable
                    ? "window_framework_unavailable"
                    : "window_still_open";

                if (!IsApplicationRunning(_application))
                {
                    return TargetExited();
                }

                if (timeoutMs > 0 && GetElapsedMilliseconds(start) >= timeoutMs)
                {
                    return Timeout();
                }

                if (current is null && !scan.FrameworkMetadataUnavailable)
                {
                    return CreateStructuredSuccessResponse(
                        stateName,
                        WaitBackend.Win32,
                        start,
                        attempts,
                        lastObservation,
                        CreateUnavailableObservedValue("window_closed"));
                }

                if (current is not null)
                {
                    lastObservation = ToWaitObservation(current);
                    lastObservedValue = ToWindowObservedValue(current);
                }
            }

            var elapsedMs = GetElapsedMilliseconds(start);
            if (elapsedMs >= timeoutMs)
            {
                return Timeout();
            }

            var remainingMs = Math.Max(1, timeoutMs - elapsedMs);
            await Task.Delay(
                Math.Min(pollIntervalMs, remainingMs),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateWindowSelector(WaitWindowSelector selector)
    {
        if (selector.Handle is null &&
            string.IsNullOrWhiteSpace(selector.Title) &&
            string.IsNullOrWhiteSpace(selector.TitleContains) &&
            selector.OwnerHandle is null &&
            string.IsNullOrWhiteSpace(selector.FrameworkId))
        {
            throw new ArgumentException(
                "condition.window requires at least one of: handle, title, titleContains, ownerHandle, frameworkId.");
        }

        if (selector.Handle is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selector), "condition.window.handle must be positive.");
        }

        if (selector.OwnerHandle is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selector), "condition.window.ownerHandle must be positive.");
        }
    }

    private static void ValidateWindowSelectorScope(WaitWindowSelector selector, int processId)
    {
        if (selector.Handle is not long handle || !OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = new IntPtr(handle);
        if (!IsWindow(hwnd))
        {
            return;
        }

        GetWindowThreadProcessId(hwnd, out var actualProcessId);
        if (actualProcessId != processId)
        {
            throw new InvalidOperationException(
                $"window_outside_session: HWND {handle} belongs to process {actualProcessId}, " +
                $"not the attached process {processId}.");
        }
    }

    private static InvalidOperationException CreateWindowSelectorMismatchException(long handle) =>
        new(
            $"window_selector_mismatch: live HWND {handle} does not match the additional " +
            "title, owner, or framework selector fields.");

    private WindowMatchScan ObserveWaitWindows(
        int processId,
        WaitWindowSelector selector,
        FlaUI.UIA3.UIA3Automation automation)
    {
        var matches = new List<ObservedWaitWindow>();
        ObservedWaitWindow? lastCandidate = null;
        var frameworkMetadataUnavailable = false;
        var frameworkProbes = 0;

        IReadOnlyList<IntPtr> handles;
        if (selector.Handle is long handle)
        {
            handles = [new IntPtr(handle)];
        }
        else
        {
            var handleScan = EnumerateBoundedVisibleWaitWindowHandles(processId);
            if (handleScan.Truncated)
            {
                throw new InvalidOperationException(
                    $"wait_window_scan_limit: window sampling exceeded {MaxWaitDesktopWindowsScanned} " +
                    $"desktop HWNDs or {MaxWaitWindowCandidates} same-process candidates.");
            }

            handles = handleScan.Handles;
        }

        foreach (var hwnd in handles)
        {
            var observed = ObserveWaitWindow(
                hwnd,
                processId,
                automation,
                requireVisible: true,
                includeFrameworkId: false);
            if (observed is null || !MatchesNativeWindowSelector(observed, selector))
            {
                continue;
            }

            lastCandidate = observed;
            if (!string.IsNullOrWhiteSpace(selector.FrameworkId))
            {
                frameworkProbes++;
                if (frameworkProbes > MaxWaitWindowFrameworkProbes)
                {
                    throw new InvalidOperationException(
                        $"wait_window_framework_probe_limit: more than {MaxWaitWindowFrameworkProbes} " +
                        "native candidates require UI Automation framework inspection.");
                }

                observed = ObserveWaitWindow(
                    hwnd,
                    processId,
                    automation,
                    requireVisible: true,
                    includeFrameworkId: true);
                if (observed is null)
                {
                    continue;
                }

                lastCandidate = observed;
                if (!observed.FrameworkIdAvailable)
                {
                    frameworkMetadataUnavailable = true;
                    continue;
                }

                if (!MatchesFrameworkSelector(observed, selector))
                {
                    continue;
                }
            }

            matches.Add(observed);
        }

        return new WindowMatchScan(matches, lastCandidate, frameworkMetadataUnavailable);
    }

    private static ObservedWaitWindow? ObserveWaitWindow(
        IntPtr hwnd,
        int processId,
        FlaUI.UIA3.UIA3Automation automation,
        bool requireVisible,
        bool includeFrameworkId)
    {
        if (hwnd == IntPtr.Zero ||
            !IsWindow(hwnd) ||
            (requireVisible && !IsWindowVisible(hwnd)))
        {
            return null;
        }

        var threadId = GetWindowThreadProcessId(hwnd, out var actualProcessId);
        if (actualProcessId != processId)
        {
            return null;
        }

        var title = GetWindowTitleForWait(hwnd);
        var ownerHandle = TryGetOwnerHandle(hwnd);
        var isVisible = IsWindowVisible(hwnd);
        if (!IsWindow(hwnd) || (requireVisible && !isVisible))
        {
            return null;
        }

        Rect? bounds = null;
        if (GetWindowRect(hwnd, out var nativeBounds) &&
            nativeBounds.Width > 0 &&
            nativeBounds.Height > 0)
        {
            bounds = new Rect(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Width,
                nativeBounds.Height);
        }

        string? frameworkId = null;
        var frameworkIdAvailable = false;
        if (includeFrameworkId)
        {
            try
            {
                frameworkIdAvailable = automation
                    .FromHandle(hwnd)
                    .AsWindow()
                    .Properties
                    .FrameworkId
                    .TryGetValue(out frameworkId);
            }
            catch
            {
            }
        }

        if (!IsWindow(hwnd) ||
            GetWindowThreadProcessId(hwnd, out var finalProcessId) != threadId ||
            finalProcessId != processId)
        {
            return null;
        }

        return new ObservedWaitWindow(
            hwnd.ToInt64(),
            threadId,
            GetNativeWindowClassName(hwnd),
            title,
            ownerHandle,
            frameworkId,
            frameworkIdAvailable,
            bounds,
            IsWindowEnabled(hwnd),
            isVisible);
    }

    private static WaitWindowHandleScan EnumerateBoundedVisibleWaitWindowHandles(int processId)
    {
        var handles = new List<IntPtr>();
        var desktopWindowsScanned = 0;
        var truncated = false;

        var completed = EnumWindows(
            (hwnd, _) =>
            {
                desktopWindowsScanned++;
                if (desktopWindowsScanned > MaxWaitDesktopWindowsScanned)
                {
                    truncated = true;
                    return false;
                }

                try
                {
                    GetWindowThreadProcessId(hwnd, out var actualProcessId);
                    if (actualProcessId != processId || !IsWindowVisible(hwnd))
                    {
                        return true;
                    }

                    if (handles.Count >= MaxWaitWindowCandidates)
                    {
                        truncated = true;
                        return false;
                    }

                    handles.Add(hwnd);
                }
                catch
                {
                }

                return true;
            },
            IntPtr.Zero);

        if (!completed && !truncated)
        {
            throw new InvalidOperationException(
                "wait_window_scan_failed: EnumWindows did not complete the native window sample.");
        }

        return new WaitWindowHandleScan(handles, truncated);
    }

    private static string GetNativeWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetNativeWindowClassNameCore(hwnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetWindowTitleForWait(IntPtr hwnd)
    {
        try
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder(length + 1);
            return GetWindowText(hwnd, text, text.Capacity) > 0
                ? text.ToString()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool MatchesNativeWindowSelector(
        ObservedWaitWindow window,
        WaitWindowSelector selector)
    {
        if (selector.Handle is long handle && window.Handle != handle)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.Title) &&
            !string.Equals(window.Title, selector.Title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.TitleContains) &&
            !window.Title.Contains(selector.TitleContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.OwnerHandle is long ownerHandle && window.OwnerHandle != ownerHandle)
        {
            return false;
        }

        return true;
    }

    private static bool MatchesFrameworkSelector(
        ObservedWaitWindow window,
        WaitWindowSelector selector) =>
        string.IsNullOrWhiteSpace(selector.FrameworkId) ||
        string.Equals(
            window.FrameworkId,
            selector.FrameworkId,
            StringComparison.OrdinalIgnoreCase);

    internal static bool SameWaitWindowIdentity(
        WaitWindowIdentity expected,
        WaitWindowIdentity actual) =>
        expected.Handle == actual.Handle &&
        expected.ThreadId == actual.ThreadId &&
        expected.OwnerHandle == actual.OwnerHandle &&
        (string.IsNullOrEmpty(expected.ClassName) ||
         string.IsNullOrEmpty(actual.ClassName) ||
         string.Equals(expected.ClassName, actual.ClassName, StringComparison.Ordinal));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetClassNameW")]
    private static extern int GetNativeWindowClassNameCore(
        IntPtr hwnd,
        StringBuilder className,
        int maxCount);

    private static WaitForObservation ToWaitObservation(ObservedWaitWindow window) =>
        new(
            Type: "Window",
            Name: window.Title,
            Bounds: window.Bounds,
            IsEnabled: window.IsEnabled,
            IsOffscreen: !window.IsVisible)
        {
            WindowHandle = window.Handle,
            OwnerHandle = window.OwnerHandle,
            FrameworkId = window.FrameworkId
        };

    private static WaitObservedValue ToWindowObservedValue(ObservedWaitWindow window) =>
        !string.IsNullOrWhiteSpace(window.Title)
            ? new WaitObservedValue(
                WaitObservedValueState.Value,
                JsonValue.Create(window.Title),
                ValueType: "System.String")
            : new WaitObservedValue(
                WaitObservedValueState.Value,
                JsonValue.Create(window.Handle),
                ValueType: "System.Int64");

    private static WaitObservedValue CreateUnavailableObservedValue(string detail) =>
        new(WaitObservedValueState.Unavailable, Detail: detail);

    private static WaitForResponse CreateStructuredSuccessResponse(
        string state,
        WaitBackend backend,
        long startTimestamp,
        int attempts,
        WaitForObservation? lastObservation,
        WaitObservedValue? lastObservedValue) =>
        new(
            Succeeded: true,
            State: state,
            ElapsedMs: GetElapsedMilliseconds(startTimestamp),
            Attempts: attempts,
            LastObservation: lastObservation)
        {
            BackendUsed = backend,
            LastObservedValue = lastObservedValue
        };

    private static WaitForResponse CreateStructuredFailureResponse(
        string state,
        WaitBackend backend,
        long startTimestamp,
        int attempts,
        WaitForObservation? lastObservation,
        WaitObservedValue? lastObservedValue,
        string failureReason,
        string reasonCode) =>
        new(
            Succeeded: false,
            State: state,
            ElapsedMs: GetElapsedMilliseconds(startTimestamp),
            Attempts: attempts,
            LastObservation: lastObservation,
            FailureReason: failureReason)
        {
            BackendUsed = backend,
            ReasonCode = reasonCode,
            LastObservedValue = lastObservedValue
        };

    private static WaitForResponse CreateStructuredTimeoutResponse(
        WaitForRequest request,
        string state,
        WaitBackend backend,
        int timeoutMs,
        long startTimestamp,
        int attempts,
        WaitForObservation? lastObservation,
        WaitObservedValue? lastObservedValue,
        string failureReason)
    {
        var response = new WaitForResponse(
            Succeeded: false,
            State: state,
            ElapsedMs: GetElapsedMilliseconds(startTimestamp),
            Attempts: attempts,
            LastObservation: lastObservation,
            FailureReason: failureReason)
        {
            BackendUsed = backend,
            ReasonCode = "wait_timeout",
            LastObservedValue = lastObservedValue
        };

        if (request.ThrowOnTimeout)
        {
            throw new InvalidOperationException(
                $"timeout: wait_for condition='{state}' after {timeoutMs}ms ({failureReason}).");
        }

        return response;
    }

    private static WaitForResponse CreateLegacyWaitTimeoutResponse(
        string state,
        WaitBackend backend,
        int timeoutMs,
        long startTimestamp,
        int attempts,
        WaitForObservation? lastObservation,
        WaitObservedValue? lastObservedValue,
        string failureReason,
        bool throwOnTimeout)
    {
        var response = new WaitForResponse(
            Succeeded: false,
            State: state,
            ElapsedMs: GetElapsedMilliseconds(startTimestamp),
            Attempts: attempts,
            LastObservation: lastObservation,
            FailureReason: failureReason)
        {
            BackendUsed = backend,
            ReasonCode = "wait_timeout",
            LastObservedValue = lastObservedValue
        };

        if (throwOnTimeout)
        {
            throw new InvalidOperationException(
                $"timeout: wait_for state='{state}' after {timeoutMs}ms ({failureReason}).");
        }

        return response;
    }

    private static WaitForResponse CreateTargetProcessExitedResponse(
        string state,
        WaitBackend backend,
        int elapsedMs,
        int attempts,
        WaitForObservation? lastObservation,
        WaitObservedValue? lastObservedValue) =>
        new(
            Succeeded: false,
            State: state,
            ElapsedMs: elapsedMs,
            Attempts: attempts,
            LastObservation: lastObservation,
            FailureReason: "target_process_exited")
        {
            BackendUsed = backend,
            ReasonCode = "target_process_exited",
            LastObservedValue = lastObservedValue
        };

    private static int GetElapsedMilliseconds(long startTimestamp) =>
        (int)Math.Round(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            MidpointRounding.AwayFromZero);

    private static int ValidateHoldForMs(int holdForMs)
    {
        if (holdForMs is < 0 or > MaxWaitHoldMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holdForMs),
                $"condition.holdForMs must be between 0 and {MaxWaitHoldMs} milliseconds.");
        }

        return holdForMs;
    }

    private static string GetConditionStateName(WaitConditionKind kind) => kind switch
    {
        WaitConditionKind.Attached => "attached",
        WaitConditionKind.Visible => "visible",
        WaitConditionKind.Enabled => "enabled",
        WaitConditionKind.Actionable => "actionable",
        WaitConditionKind.BoundsStable => "bounds_stable",
        WaitConditionKind.NumericValueEquals => "numeric_value_equals",
        WaitConditionKind.NameContains => "name_contains",
        WaitConditionKind.DependencyPropertyValue => "dependency_property_value",
        WaitConditionKind.DataContextValue => "data_context_value",
        WaitConditionKind.WindowOpen => "window_open",
        WaitConditionKind.WindowClosed => "window_closed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private WaitBackend WaitBackendForRequest(WaitForRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ElementId))
        {
            return RequireHandle(request.ElementId.Trim()).Backend == InspectionBackend.Wpf
                ? WaitBackend.Wpf
                : WaitBackend.Uia;
        }

        return request.Backend == InspectionBackend.Wpf
            ? WaitBackend.Wpf
            : WaitBackend.Uia;
    }

    internal sealed record WaitWindowIdentity(
        long Handle,
        uint ThreadId,
        string ClassName,
        long? OwnerHandle);

    private sealed record ObservedWaitWindow(
        long Handle,
        uint ThreadId,
        string ClassName,
        string Title,
        long? OwnerHandle,
        string? FrameworkId,
        bool FrameworkIdAvailable,
        Rect? Bounds,
        bool IsEnabled,
        bool IsVisible)
    {
        public WaitWindowIdentity Identity { get; } =
            new(Handle, ThreadId, ClassName, OwnerHandle);
    }

    private sealed record WaitWindowHandleScan(
        IReadOnlyList<IntPtr> Handles,
        bool Truncated);

    private sealed record WindowMatchScan(
        IReadOnlyList<ObservedWaitWindow> Matches,
        ObservedWaitWindow? LastCandidate,
        bool FrameworkMetadataUnavailable);

    private static bool IsStructuredElementDeadlineCancellation(
        StructuredElementWaitDeadline? deadline) =>
        deadline is not null &&
        deadline.DeadlineToken.IsCancellationRequested &&
        !deadline.CallerToken.IsCancellationRequested;

    private sealed record StructuredElementWaitDeadline(
        long StartTimestamp,
        CancellationToken CallerToken,
        CancellationToken DeadlineToken);
}
