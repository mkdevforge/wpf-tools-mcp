using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal enum RealizeItemTargetState
{
    AlreadyRealized,
    Virtualized,
    Unsupported
}

internal enum RealizeItemPostconditionState
{
    Pending,
    Verified,
    Terminal
}

internal sealed record RealizeItemPostconditionResult(
    RealizeItemPostconditionState State,
    bool Reusable = false,
    ElementRef? Element = null,
    string? StopReason = null,
    string? RecoveryReason = null,
    FailureInfo? Failure = null)
{
    public static RealizeItemPostconditionResult Pending() =>
        new(RealizeItemPostconditionState.Pending);

    public static RealizeItemPostconditionResult Verified(
        ElementRef? element,
        bool reusable,
        string? stopReason = null,
        string? recoveryReason = null,
        FailureInfo? failure = null) =>
        new(
            RealizeItemPostconditionState.Verified,
            Reusable: reusable,
            Element: element,
            StopReason: stopReason,
            RecoveryReason: recoveryReason,
            Failure: failure);

    public static RealizeItemPostconditionResult Terminal(
        string stopReason,
        string? recoveryReason = null,
        FailureInfo? failure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stopReason);
        return new(
            RealizeItemPostconditionState.Terminal,
            StopReason: stopReason,
            RecoveryReason: recoveryReason,
            Failure: failure);
    }
}

internal interface IRealizeItemProvider<TItem>
    where TItem : class
{
    TItem? FindNext(TItem? startAfter);

    TItem? FindByExactName(TItem? startAfter, string exactName);

    RealizeItemTargetState GetTargetState(TItem target);

    void Realize(TItem target);

    ValueTask<RealizeItemPostconditionResult> CheckPostconditionAsync(TItem target);
}

internal static class RealizeItemCoordinator
{
    public static Task<RealizeItemResponse> ExecuteAsync<TItem>(
        RealizeItemRequest request,
        long windowHandleUsed,
        IRealizeItemProvider<TItem> provider,
        Func<Exception, FailureInfo> classifyFailure,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(classifyFailure);
        Validate(request);

        var clock = timeProvider ?? TimeProvider.System;
        return new Execution<TItem>(
            request,
            windowHandleUsed,
            provider,
            classifyFailure,
            cancellationToken,
            clock,
            delayAsync ?? ((delay, token) => Task.Delay(delay, clock, token)))
            .RunAsync();
    }

    private static void Validate(RealizeItemRequest request)
    {
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

        ValidateRange(
            request.MaxProviderCalls,
            RealizeItemLimits.MinimumProviderCalls,
            RealizeItemLimits.MaximumProviderCalls,
            nameof(request.MaxProviderCalls));
        ValidateRange(
            request.AdvisoryElapsedLimitMs,
            RealizeItemLimits.MinimumAdvisoryElapsedLimitMs,
            RealizeItemLimits.MaximumAdvisoryElapsedLimitMs,
            nameof(request.AdvisoryElapsedLimitMs));
        ValidateRange(
            request.PollIntervalMs,
            RealizeItemLimits.MinimumPollIntervalMs,
            RealizeItemLimits.MaximumPollIntervalMs,
            nameof(request.PollIntervalMs));
    }

    private static void ValidateRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}.");
        }
    }

    private sealed class Execution<TItem>
        where TItem : class
    {
        private readonly RealizeItemRequest _request;
        private readonly long _windowHandleUsed;
        private readonly IRealizeItemProvider<TItem> _provider;
        private readonly Func<Exception, FailureInfo> _classifyFailure;
        private readonly CancellationToken _cancellationToken;
        private readonly TimeProvider _clock;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly long _startedTimestamp;
        private readonly RealizeItemRequestedIdentity _requestedIdentity;
        private int _findCalls;
        private int _postconditionPolls;
        private bool _realizeInvoked;
        private bool _mutationStarted;
        private string _methodUsed = RealizeItemOutcomes.MethodNone;

        public Execution(
            RealizeItemRequest request,
            long windowHandleUsed,
            IRealizeItemProvider<TItem> provider,
            Func<Exception, FailureInfo> classifyFailure,
            CancellationToken cancellationToken,
            TimeProvider clock,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _request = request;
            _windowHandleUsed = windowHandleUsed;
            _provider = provider;
            _classifyFailure = classifyFailure;
            _cancellationToken = cancellationToken;
            _clock = clock;
            _delayAsync = delayAsync;
            _startedTimestamp = clock.GetTimestamp();
            _requestedIdentity = new RealizeItemRequestedIdentity(request.Index, request.Name);
        }

        public async Task<RealizeItemResponse> RunAsync()
        {
            _cancellationToken.ThrowIfCancellationRequested();

            var search = _request.Index is int index
                ? FindByIndex(index)
                : FindByName(_request.Name!);
            if (search.StopResponse is not null)
            {
                return search.StopResponse;
            }

            var target = search.Item!;
            var boundary = CheckPreMutationBoundary();
            if (boundary is not null)
            {
                return boundary;
            }

            RealizeItemTargetState targetState;
            try
            {
                targetState = _provider.GetTargetState(target);
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Stop(
                    RealizeItemOutcomes.StopProviderFailure,
                    "Retry after the UI Automation provider is responsive.",
                    failure: _classifyFailure(ex));
            }

            boundary = CheckPreMutationBoundary();
            if (boundary is not null)
            {
                return boundary;
            }

            return targetState switch
            {
                RealizeItemTargetState.AlreadyRealized =>
                    await VerifyAlreadyRealizedAsync(target).ConfigureAwait(false),
                RealizeItemTargetState.Virtualized =>
                    await RealizeAndVerifyAsync(target).ConfigureAwait(false),
                RealizeItemTargetState.Unsupported => Stop(
                    RealizeItemOutcomes.StopUnsupported,
                    "The provider item is outside the realized tree and does not support VirtualizedItemPattern."),
                _ => Stop(
                    RealizeItemOutcomes.StopProviderFailure,
                    "Retry after the UI Automation provider returns a supported item state.")
            };
        }

        private SearchResult FindByIndex(int index)
        {
            TItem? current = null;
            for (var currentIndex = 0; currentIndex <= index; currentIndex++)
            {
                var find = InvokeFind(() => _provider.FindNext(current));
                if (find.StopResponse is not null)
                {
                    return find;
                }

                if (find.Item is null)
                {
                    return SearchResult.Stopped(Stop(
                        RealizeItemOutcomes.StopNotFound,
                        "Verify the zero-based provider-order index and retry."));
                }

                current = find.Item;
            }

            return SearchResult.Found(current!);
        }

        private SearchResult FindByName(string exactName)
        {
            var first = InvokeFind(() => _provider.FindByExactName(null, exactName));
            if (first.StopResponse is not null)
            {
                return first;
            }

            if (first.Item is null)
            {
                return SearchResult.Stopped(Stop(
                    RealizeItemOutcomes.StopNotFound,
                    "Verify the exact UIA Name and retry."));
            }

            var second = InvokeFind(() => _provider.FindByExactName(first.Item, exactName));
            if (second.StopResponse is not null)
            {
                return second;
            }

            if (second.Item is not null)
            {
                return SearchResult.Stopped(Stop(
                    RealizeItemOutcomes.StopAmbiguous,
                    "Use the zero-based provider-order index to identify one provider-observed item."));
            }

            var reacquired = InvokeFind(() => _provider.FindByExactName(null, exactName));
            if (reacquired.StopResponse is not null)
            {
                return reacquired;
            }

            return reacquired.Item is null
                ? SearchResult.Stopped(Stop(
                    RealizeItemOutcomes.StopTargetUnavailableAfterProbe,
                    "The provider invalidated the first placeholder; retry the operation."))
                : SearchResult.Found(reacquired.Item);
        }

        private SearchResult InvokeFind(Func<TItem?> find)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (_findCalls >= _request.MaxProviderCalls)
            {
                return SearchResult.Stopped(Stop(
                    RealizeItemOutcomes.StopProviderCallLimit,
                    $"Increase maxProviderCalls up to {RealizeItemLimits.MaximumProviderCalls} or use a closer provider-order index."));
            }

            if (AdvisoryElapsedLimitReached())
            {
                return SearchResult.Stopped(Stop(
                    RealizeItemOutcomes.StopAdvisoryElapsedLimit,
                    $"Increase advisoryElapsedLimitMs up to {RealizeItemLimits.MaximumAdvisoryElapsedLimitMs} or retry."));
            }

            _findCalls++;
            try
            {
                var item = find();
                _cancellationToken.ThrowIfCancellationRequested();
                return SearchResult.FoundOrMissing(item);
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SearchResult.Stopped(Stop(
                    RealizeItemOutcomes.StopProviderFailure,
                    "Retry after the UI Automation provider is responsive.",
                    failure: _classifyFailure(ex)));
            }
        }

        private RealizeItemResponse? CheckPreMutationBoundary()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return AdvisoryElapsedLimitReached()
                ? Stop(
                    RealizeItemOutcomes.StopAdvisoryElapsedLimit,
                    $"Increase advisoryElapsedLimitMs up to {RealizeItemLimits.MaximumAdvisoryElapsedLimitMs} or retry.")
                : null;
        }

        private async Task<RealizeItemResponse> VerifyAlreadyRealizedAsync(TItem target)
        {
            _methodUsed = RealizeItemOutcomes.MethodAlreadyRealized;
            RealizeItemPostconditionResult postcondition;
            try
            {
                _postconditionPolls++;
                postcondition = await _provider.CheckPostconditionAsync(target).ConfigureAwait(false);
                _cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Stop(
                    RealizeItemOutcomes.StopPostconditionFailure,
                    "Resolve the already-realized item again before interacting with it.",
                    failure: _classifyFailure(ex));
            }

            return CompletePostcondition(
                postcondition,
                pendingStopReason: RealizeItemOutcomes.StopPostconditionUnverified,
                pendingRecovery: "Resolve the already-realized item again before interacting with it.");
        }

        private async Task<RealizeItemResponse> RealizeAndVerifyAsync(TItem target)
        {
            _methodUsed = RealizeItemOutcomes.MethodVirtualizedItemRealize;
            _realizeInvoked = true;
            _mutationStarted = true;

            try
            {
                _provider.Realize(target);
            }
            catch (OperationCanceledException ex)
            {
                return _cancellationToken.IsCancellationRequested
                    ? Stop(
                        RealizeItemOutcomes.StopCancelledAfterRealize,
                        "Inspect the container before retrying because realization may have changed its viewport.")
                    : Stop(
                        RealizeItemOutcomes.StopRealizeFailure,
                        "Inspect the container before retrying because realization may have changed its viewport.",
                        failure: _classifyFailure(ex));
            }
            catch (Exception ex)
            {
                return Stop(
                    RealizeItemOutcomes.StopRealizeFailure,
                    "Inspect the container before retrying because realization may have changed its viewport.",
                    failure: _classifyFailure(ex));
            }

            var maximumPolls = 1 + (int)Math.Ceiling(
                (double)_request.AdvisoryElapsedLimitMs / _request.PollIntervalMs);
            while (true)
            {
                RealizeItemPostconditionResult postcondition;
                try
                {
                    _postconditionPolls++;
                    postcondition = await _provider.CheckPostconditionAsync(target).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    return _cancellationToken.IsCancellationRequested
                        ? Stop(
                            RealizeItemOutcomes.StopCancelledAfterRealize,
                            "Inspect the container before retrying because realization may have completed.")
                        : Stop(
                            RealizeItemOutcomes.StopPostconditionFailure,
                            "Inspect the container before retrying because realization may have completed.",
                            failure: _classifyFailure(ex));
                }
                catch (Exception ex)
                {
                    return Stop(
                        RealizeItemOutcomes.StopPostconditionFailure,
                        "Inspect the container before retrying because realization may have completed.",
                        failure: _classifyFailure(ex));
                }

                if (postcondition.State is not RealizeItemPostconditionState.Pending)
                {
                    return CompletePostcondition(
                        postcondition,
                        pendingStopReason: RealizeItemOutcomes.StopPostconditionUnverified,
                        pendingRecovery: "Resolve the item again before interacting with it.");
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    return Stop(
                        RealizeItemOutcomes.StopCancelledAfterRealize,
                        "Inspect the container before retrying because realization may have completed.");
                }

                if (AdvisoryElapsedLimitReached())
                {
                    return Stop(
                        RealizeItemOutcomes.StopAdvisoryElapsedLimit,
                        "Inspect the container before retrying because realization may still complete asynchronously.");
                }

                if (_postconditionPolls >= maximumPolls)
                {
                    return Stop(
                        RealizeItemOutcomes.StopPostconditionPollLimit,
                        "Inspect the container before retrying because realization may still complete asynchronously.");
                }

                try
                {
                    await _delayAsync(
                        TimeSpan.FromMilliseconds(_request.PollIntervalMs),
                        _cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                {
                    return Stop(
                        RealizeItemOutcomes.StopCancelledAfterRealize,
                        "Inspect the container before retrying because realization may still complete asynchronously.");
                }
                catch (Exception ex)
                {
                    return Stop(
                        RealizeItemOutcomes.StopPostconditionFailure,
                        "Inspect the container before retrying because realization may still complete asynchronously.",
                        failure: _classifyFailure(ex));
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    return Stop(
                        RealizeItemOutcomes.StopCancelledAfterRealize,
                        "Inspect the container before retrying because realization may still complete asynchronously.");
                }

                if (AdvisoryElapsedLimitReached())
                {
                    return Stop(
                        RealizeItemOutcomes.StopAdvisoryElapsedLimit,
                        "Inspect the container before retrying because realization may still complete asynchronously.");
                }
            }
        }

        private RealizeItemResponse CompletePostcondition(
            RealizeItemPostconditionResult postcondition,
            string pendingStopReason,
            string pendingRecovery) =>
            postcondition.State switch
            {
                RealizeItemPostconditionState.Verified => Stop(
                    postcondition.StopReason ?? RealizeItemOutcomes.StopCompleted,
                    postcondition.RecoveryReason,
                    postconditionVerified: true,
                    reusable: postcondition.Reusable,
                    element: postcondition.Element,
                    failure: postcondition.Failure),
                RealizeItemPostconditionState.Terminal => Stop(
                    postcondition.StopReason ?? RealizeItemOutcomes.StopPostconditionFailure,
                    postcondition.RecoveryReason,
                    failure: postcondition.Failure),
                _ => Stop(pendingStopReason, pendingRecovery)
            };

        private RealizeItemResponse Stop(
            string stopReason,
            string? recoveryReason,
            bool postconditionVerified = false,
            bool reusable = false,
            ElementRef? element = null,
            FailureInfo? failure = null) =>
            new(
                RequestedIdentity: _requestedIdentity,
                MethodUsed: _methodUsed,
                RealizeInvoked: _realizeInvoked,
                PostconditionVerified: postconditionVerified,
                FindItemByPropertyCalls: _findCalls,
                PostconditionPolls: _postconditionPolls,
                ElapsedMs: ElapsedMilliseconds(),
                StopReason: stopReason,
                ViewportMayHaveChanged: _mutationStarted,
                DataOrContainerLoadingMayHaveOccurred: _mutationStarted,
                Reusable: postconditionVerified && reusable,
                WindowHandleUsed: _windowHandleUsed,
                RecoveryReason: recoveryReason,
                Element: postconditionVerified ? element : null,
                Failure: failure);

        private bool AdvisoryElapsedLimitReached() =>
            _clock.GetElapsedTime(_startedTimestamp, _clock.GetTimestamp()) >=
            TimeSpan.FromMilliseconds(_request.AdvisoryElapsedLimitMs);

        private long ElapsedMilliseconds() =>
            Math.Max(
                0,
                (long)Math.Ceiling(
                    _clock.GetElapsedTime(_startedTimestamp, _clock.GetTimestamp()).TotalMilliseconds));

        private sealed record SearchResult(TItem? Item, RealizeItemResponse? StopResponse)
        {
            public static SearchResult Found(TItem item) => new(item, null);

            public static SearchResult FoundOrMissing(TItem? item) => new(item, null);

            public static SearchResult Stopped(RealizeItemResponse response) => new(null, response);
        }
    }
}
