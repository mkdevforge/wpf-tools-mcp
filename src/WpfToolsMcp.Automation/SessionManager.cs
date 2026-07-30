using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal readonly record struct SessionWindowSelection(long Handle, string Title);

internal readonly record struct SessionTopLevelWindowObservation(
    long Handle,
    string Title,
    long OwnerHandle,
    bool IsVisible,
    bool IsEnabled,
    int ZOrder);

internal readonly record struct SessionAutomaticModalIdentity(long OwnerHandle, long RootOwnerHandle);

internal sealed class SessionTopLevelWindowGraph
{
    private readonly IReadOnlyDictionary<long, SessionTopLevelWindowObservation> _windows;

    private SessionTopLevelWindowGraph(
        IReadOnlyDictionary<long, SessionTopLevelWindowObservation> windows,
        bool isAvailable)
    {
        _windows = windows;
        IsAvailable = isAvailable;
    }

    internal static SessionTopLevelWindowGraph Unavailable { get; } =
        new(new Dictionary<long, SessionTopLevelWindowObservation>(), isAvailable: false);

    internal bool IsAvailable { get; }

    internal static SessionTopLevelWindowGraph Create(IEnumerable<SessionTopLevelWindowObservation> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var byHandle = new Dictionary<long, SessionTopLevelWindowObservation>();
        foreach (var window in windows)
        {
            if (window.Handle == 0)
            {
                continue;
            }

            if (!byHandle.TryGetValue(window.Handle, out var existing) || window.ZOrder < existing.ZOrder)
            {
                byHandle[window.Handle] = window;
            }
        }

        return new SessionTopLevelWindowGraph(byHandle, isAvailable: true);
    }

    internal bool ContainsWindow(long handle) => _windows.ContainsKey(handle);

    internal bool IsAutomaticOwnerLive(long handle) =>
        _windows.TryGetValue(handle, out var window) && window.IsVisible;

    internal bool IsAutomaticModalLive(long handle, SessionAutomaticModalIdentity identity) =>
        TryGetModalIdentity(handle, requireEnabledSurface: false, out var currentIdentity, out _) &&
        currentIdentity == identity;

    internal bool TrySelectModal(
        long activeHandle,
        out SessionTopLevelWindowObservation selected,
        out SessionAutomaticModalIdentity identity)
    {
        selected = default;
        identity = default;
        ModalCandidate? best = null;

        foreach (var window in _windows.Values)
        {
            if (!TryGetModalIdentity(
                    window.Handle,
                    requireEnabledSurface: true,
                    out var candidateIdentity,
                    out var ownershipPath))
            {
                continue;
            }

            var candidate = new ModalCandidate(
                Window: window,
                Identity: candidateIdentity,
                IsInActiveOwnershipGroup: activeHandle != 0 &&
                    IsDescendantOrSelf(window.Handle, activeHandle),
                OwnershipDepth: ownershipPath.Count - 1);

            if (best is null || IsPreferred(candidate, best.Value))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return false;
        }

        selected = best.Value.Window;
        identity = best.Value.Identity;
        return true;
    }

    internal bool TryGetOwnershipPath(
        long handle,
        out IReadOnlyList<SessionTopLevelWindowObservation> ownershipPath)
    {
        var reversed = new List<SessionTopLevelWindowObservation>();
        var seen = new HashSet<long>();
        var currentHandle = handle;

        while (currentHandle != 0)
        {
            if (!seen.Add(currentHandle) || !_windows.TryGetValue(currentHandle, out var current))
            {
                ownershipPath = Array.Empty<SessionTopLevelWindowObservation>();
                return false;
            }

            reversed.Add(current);
            currentHandle = current.OwnerHandle;
        }

        reversed.Reverse();
        ownershipPath = reversed;
        return reversed.Count > 0;
    }

    internal bool TryGetHistoricalModalIdentity(
        long handle,
        out SessionAutomaticModalIdentity identity) =>
        TryGetModalIdentity(handle, requireEnabledSurface: false, out identity, out _);

    private bool TryGetModalIdentity(
        long handle,
        bool requireEnabledSurface,
        out SessionAutomaticModalIdentity identity,
        out IReadOnlyList<SessionTopLevelWindowObservation> ownershipPath)
    {
        identity = default;
        ownershipPath = Array.Empty<SessionTopLevelWindowObservation>();

        if (!_windows.TryGetValue(handle, out var window) ||
            !window.IsVisible ||
            (requireEnabledSurface && !window.IsEnabled) ||
            window.OwnerHandle == 0 ||
            !_windows.TryGetValue(window.OwnerHandle, out var owner) ||
            owner.IsEnabled ||
            !TryGetOwnershipPath(handle, out ownershipPath))
        {
            return false;
        }

        identity = new SessionAutomaticModalIdentity(
            OwnerHandle: window.OwnerHandle,
            RootOwnerHandle: ownershipPath[0].Handle);
        return true;
    }

    private bool IsDescendantOrSelf(long candidateHandle, long ancestorHandle)
    {
        var seen = new HashSet<long>();
        var currentHandle = candidateHandle;

        while (currentHandle != 0 && seen.Add(currentHandle))
        {
            if (currentHandle == ancestorHandle)
            {
                return true;
            }

            if (!_windows.TryGetValue(currentHandle, out var current))
            {
                return false;
            }

            currentHandle = current.OwnerHandle;
        }

        return false;
    }

    private static bool IsPreferred(ModalCandidate candidate, ModalCandidate current)
    {
        if (candidate.Window.ZOrder != current.Window.ZOrder)
        {
            return candidate.Window.ZOrder < current.Window.ZOrder;
        }

        if (candidate.IsInActiveOwnershipGroup != current.IsInActiveOwnershipGroup)
        {
            return candidate.IsInActiveOwnershipGroup;
        }

        if (candidate.OwnershipDepth != current.OwnershipDepth)
        {
            return candidate.OwnershipDepth > current.OwnershipDepth;
        }

        return candidate.Window.Handle < current.Window.Handle;
    }

    private readonly record struct ModalCandidate(
        SessionTopLevelWindowObservation Window,
        SessionAutomaticModalIdentity Identity,
        bool IsInActiveOwnershipGroup,
        int OwnershipDepth);
}

internal sealed class SessionWindowSelectionHistory
{
    private enum SelectionSource
    {
        Explicit,
        AutomaticOwner,
        AutomaticModal
    }

    private sealed record Entry(
        long Handle,
        string Title,
        bool PreserveAsFallback,
        SelectionSource Source,
        SessionAutomaticModalIdentity? AutomaticModalIdentity);

    private const int DefaultCapacity = 16;
    private readonly object _reconciliationSync = new();
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly List<Entry> _entries = [];
    private SessionWindowSelection _active = new(0, "");
    private long _revision;

    internal SessionWindowSelectionHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal void RecordSelection(long handle, string? title, bool preserveAsFallback = false)
        => Record(
            handle,
            title,
            preserveAsFallback,
            SelectionSource.Explicit,
            automaticModalIdentity: null);

    internal SessionWindowSelection RecordFallbackIfEmpty(long handle, string? title)
    {
        lock (_reconciliationSync)
        {
            var active = GetActive();
            if (active.Handle != 0)
            {
                return active;
            }

            RecordSelection(handle, title, preserveAsFallback: true);
            return GetActive();
        }
    }

    internal void RecordAutomaticOwner(long handle, string? title)
        => Record(
            handle,
            title,
            preserveAsFallback: false,
            SelectionSource.AutomaticOwner,
            automaticModalIdentity: null);

    internal void RecordAutomaticModal(
        long handle,
        string? title,
        SessionAutomaticModalIdentity identity)
        => Record(
            handle,
            title,
            preserveAsFallback: false,
            SelectionSource.AutomaticModal,
            identity);

    private void Record(
        long handle,
        string? title,
        bool preserveAsFallback,
        SelectionSource source,
        SessionAutomaticModalIdentity? automaticModalIdentity)
    {
        if (handle == 0)
        {
            return;
        }

        lock (_reconciliationSync)
        {
            lock (_sync)
            {
                var existingIndex = _entries.FindIndex(entry => entry.Handle == handle);
                var existing = existingIndex >= 0 ? _entries[existingIndex] : null;
                var wasPreserved = existing?.PreserveAsFallback == true;
                if (existingIndex >= 0)
                {
                    _entries.RemoveAt(existingIndex);
                }

                var effectiveSource = source;
                var effectiveIdentity = automaticModalIdentity;
                if (source != SelectionSource.Explicit && existing?.Source == SelectionSource.Explicit)
                {
                    effectiveSource = SelectionSource.Explicit;
                    effectiveIdentity = null;
                }
                var normalizedTitle = title ?? "";
                if (normalizedTitle.Length == 0 && existing is not null)
                {
                    normalizedTitle = existing.Title;
                }

                var entry = new Entry(
                    handle,
                    normalizedTitle,
                    preserveAsFallback || wasPreserved,
                    effectiveSource,
                    effectiveIdentity);
                _entries.Add(entry);

                while (_entries.Count > _capacity)
                {
                    var evictionIndex = _entries.FindIndex(
                        startIndex: 0,
                        count: _entries.Count - 1,
                        match: candidate => !candidate.PreserveAsFallback);
                    _entries.RemoveAt(evictionIndex >= 0 ? evictionIndex : 0);
                }

                _active = new SessionWindowSelection(entry.Handle, entry.Title);
                _revision++;
            }
        }
    }

    internal SessionWindowSelection GetActive()
    {
        lock (_sync)
        {
            return _active;
        }
    }

    internal SessionWindowSelection Reconcile(
        Func<long, bool> isWindowValid,
        SessionTopLevelWindowGraph? windowGraph = null)
    {
        ArgumentNullException.ThrowIfNull(isWindowValid);

        while (true)
        {
            Entry[] snapshot;
            long observedRevision;
            lock (_sync)
            {
                snapshot = [.. _entries];
                observedRevision = _revision;
            }

            var invalidHandles = new HashSet<long>();
            Entry? mostRecentLive = null;
            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                var entry = snapshot[index];
                var isLive = entry.Source switch
                {
                    SelectionSource.AutomaticOwner when windowGraph?.IsAvailable == true =>
                        windowGraph.IsAutomaticOwnerLive(entry.Handle),
                    SelectionSource.AutomaticModal when
                        windowGraph?.IsAvailable == true &&
                        entry.AutomaticModalIdentity is SessionAutomaticModalIdentity identity =>
                        windowGraph.IsAutomaticModalLive(entry.Handle, identity),
                    _ => isWindowValid(entry.Handle)
                };

                if (isLive)
                {
                    mostRecentLive ??= entry;
                }
                else
                {
                    invalidHandles.Add(entry.Handle);
                }
            }

            lock (_sync)
            {
                if (observedRevision != _revision)
                {
                    continue;
                }

                if (invalidHandles.Count > 0)
                {
                    _entries.RemoveAll(entry => invalidHandles.Contains(entry.Handle));
                }

                _active = mostRecentLive is null
                    ? new SessionWindowSelection(0, "")
                    : new SessionWindowSelection(mostRecentLive.Handle, mostRecentLive.Title);
                _revision++;
                return _active;
            }
        }
    }

    internal SessionWindowSelection ObserveAndReconcile(
        Func<SessionTopLevelWindowGraph> observeWindowGraph,
        Func<long, bool> isWindowValid)
    {
        ArgumentNullException.ThrowIfNull(observeWindowGraph);
        ArgumentNullException.ThrowIfNull(isWindowValid);

        lock (_reconciliationSync)
        {
            var windowGraph = observeWindowGraph();
            return SessionWindowReconciler.Reconcile(this, isWindowValid, windowGraph);
        }
    }
}

internal static class SessionWindowReconciler
{
    internal static SessionWindowSelection Reconcile(
        SessionWindowSelectionHistory history,
        Func<long, bool> isWindowValid,
        SessionTopLevelWindowGraph windowGraph)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(isWindowValid);
        ArgumentNullException.ThrowIfNull(windowGraph);

        var active = history.Reconcile(isWindowValid, windowGraph);
        if (!windowGraph.IsAvailable ||
            !windowGraph.TrySelectModal(active.Handle, out var modal, out _))
        {
            return active;
        }

        if (!windowGraph.TryGetOwnershipPath(modal.Handle, out var ownershipPath))
        {
            return active;
        }

        foreach (var window in ownershipPath)
        {
            if (windowGraph.TryGetHistoricalModalIdentity(window.Handle, out var identity))
            {
                history.RecordAutomaticModal(window.Handle, window.Title, identity);
            }
            else
            {
                history.RecordAutomaticOwner(window.Handle, window.Title);
            }
        }

        return history.GetActive();
    }
}

public sealed class SessionManager : IDisposable
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private sealed class SessionState
    {
        private readonly object _sync = new();
        private readonly SessionWindowSelectionHistory _windowSelections = new();
        private bool _ending;

        public SessionState(
            string sessionId,
            AutomationController controller,
            int pid,
            string processName,
            ProcessInstanceIdentity processIdentity,
            EffectiveInteractionPolicy interactionPolicy)
        {
            SessionId = sessionId;
            Controller = controller;
            Pid = pid;
            ProcessName = processName;
            ProcessIdentity = processIdentity;
            InteractionPolicy = interactionPolicy;
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        }

        public string SessionId { get; }
        public AutomationController Controller { get; }
        public int Pid { get; }
        public string ProcessName { get; }
        public ProcessInstanceIdentity ProcessIdentity { get; }
        public EffectiveInteractionPolicy InteractionPolicy { get; }
        public string CreatedAtUtc { get; }

        public void RecordWindowSelection(long handle, string title, bool preserveAsFallback = false) =>
            _windowSelections.RecordSelection(handle, title, preserveAsFallback);

        public SessionWindowSelection RecordFallbackWindowIfEmpty(long handle, string title) =>
            _windowSelections.RecordFallbackIfEmpty(handle, title);

        public SessionWindowSelection GetActiveWindow() => _windowSelections.GetActive();

        public SessionWindowSelection ReconcileActiveWindow(
            Func<SessionTopLevelWindowGraph> observeWindowGraph,
            Func<long, bool> isWindowValid) =>
            _windowSelections.ObserveAndReconcile(observeWindowGraph, isWindowValid);

        public bool IsEnding
        {
            get
            {
                lock (_sync)
                {
                    return _ending;
                }
            }
        }

        public bool TryBeginEnding(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_ending)
                {
                    return false;
                }

                _ending = true;
                return true;
            }
        }

        public T RegisterResource<T>(Func<T> register)
        {
            ArgumentNullException.ThrowIfNull(register);

            lock (_sync)
            {
                if (_ending)
                {
                    throw new InvalidOperationException($"Session '{SessionId}' is ending.");
                }

                return register();
            }
        }

        public void ThrowIfEnding()
        {
            lock (_sync)
            {
                if (_ending)
                {
                    throw new InvalidOperationException($"Session '{SessionId}' is ending.");
                }
            }
        }

        public void CancelEnding()
        {
            lock (_sync)
            {
                _ending = false;
            }
        }
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RetiredSession> _retiredSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _retiredSessionOrder = new();
    private readonly object _retiredSessionSync = new();
    private readonly SemaphoreSlim _replacementMutex = new(1, 1);
    private const int MaximumRetiredSessions = 256;

    private sealed record RetiredSession(string SuccessorSessionId, int PreviousPid, int SuccessorPid);

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            try
            {
                kvp.Value.Controller.Dispose();
            }
            catch
            {
            }
        }

        _sessions.Clear();
        _retiredSessions.Clear();
    }

    public async Task<ListSessionsResponse> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = _sessions.Values
            .Where(s => !s.IsEnding)
            .OrderBy(s => s.CreatedAtUtc, StringComparer.Ordinal)
            .ToArray();

        var observedSessions = new List<(
            SessionState Session,
            ProcessInstanceState ProcessState,
            bool ControllerAttached)>(sessions.Length);

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processState = ProcessTargetResolver.Observe(session.ProcessIdentity);
            var controllerAttached =
                processState == ProcessInstanceState.Current &&
                session.Controller.IsAttached;
            if (controllerAttached)
            {
                await RefreshBackendCapabilitiesAsync(session, cancellationToken).ConfigureAwait(false);
                processState = ProcessTargetResolver.Observe(session.ProcessIdentity);
                controllerAttached =
                    processState == ProcessInstanceState.Current &&
                    session.Controller.IsAttached;
            }

            var activeWindow = ReconcileActiveWindow(session);
            session.Controller.TrackOrRejectExternalWindowHandle(activeWindow.Handle);
            observedSessions.Add((session, processState, controllerAttached));
        }

        var activeSessions = observedSessions
            .Where(observed =>
                !observed.Session.IsEnding &&
                _sessions.TryGetValue(observed.Session.SessionId, out var current) &&
                ReferenceEquals(observed.Session, current))
            .ToArray();

        return new ListSessionsResponse(activeSessions
            .Select(observed => ToSessionInfo(
                observed.Session,
                observed.ProcessState,
                observed.ControllerAttached))
            .ToArray());
    }

    public async Task<LaunchAppResponse> LaunchAppAsync(LaunchAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var controller = new AutomationController();
        try
        {
            var launched = await controller.LaunchAsync(request, cancellationToken);
            var identity = controller.AttachedProcessIdentity;
            var sessionId = CreateSessionId();
            var interactionPolicy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var session = new SessionState(
                sessionId,
                controller,
                launched.Pid,
                launched.ProcessName,
                identity,
                interactionPolicy);

            if (!_sessions.TryAdd(sessionId, session))
            {
                throw new InvalidOperationException("Failed to register new session.");
            }

            await InitializeActiveWindowAsync(session, cancellationToken);
            return new LaunchAppResponse(
                sessionId,
                launched.Pid,
                launched.ProcessName,
                interactionPolicy.ToContract());
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    public async Task<AttachToAppResponse> AttachToAppAsync(AttachToAppRequest request, CancellationToken cancellationToken)
        => await AttachToAppAsync(request, releaseReplacedSessionResources: null, cancellationToken).ConfigureAwait(false);

    public async Task<AttachToAppResponse> AttachToAppAsync(
        AttachToAppRequest request,
        Func<Task>? releaseReplacedSessionResources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            return await ReplaceExitedSessionAsync(
                request,
                releaseReplacedSessionResources,
                cancellationToken).ConfigureAwait(false);
        }

        var controller = new AutomationController();
        try
        {
            var attached = await controller.AttachAsync(request, cancellationToken);
            var identity = ParseAttachedIdentity(attached);
            var sessionId = CreateSessionId();
            var interactionPolicy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var session = new SessionState(
                sessionId,
                controller,
                attached.Pid,
                attached.ProcessName,
                identity,
                interactionPolicy);

            if (!_sessions.TryAdd(sessionId, session))
            {
                throw new InvalidOperationException("Failed to register new session.");
            }

            await InitializeActiveWindowAsync(session, cancellationToken);
            return new AttachToAppResponse(
                sessionId,
                attached.Pid,
                attached.ProcessName,
                interactionPolicy.ToContract())
            {
                ProcessInstanceId = identity.Value
            };
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    private async Task<AttachToAppResponse> ReplaceExitedSessionAsync(
        AttachToAppRequest request,
        Func<Task>? releaseReplacedSessionResources,
        CancellationToken cancellationToken)
    {
        var previousSessionId = request.SessionId!.Trim();
        await _replacementMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = GetSession(previousSessionId);
            var previousProcessState = ProcessTargetResolver.Observe(previous.ProcessIdentity);
            if (previousProcessState == ProcessInstanceState.Current)
            {
                throw new InvalidOperationException(
                    $"target_process_still_running: session '{previousSessionId}' still targets live process " +
                    $"{previous.Pid}. Replacement is only allowed after that process instance exits.");
            }

            if (previousProcessState == ProcessInstanceState.Unavailable)
            {
                throw new InvalidOperationException(
                    $"process_state_unavailable: session '{previousSessionId}' target process state could not be " +
                    "verified. Retry after the process can be observed; the target was not replaced.");
            }

            var hasExplicitTarget = request.Pid is not null ||
                                    !string.IsNullOrWhiteSpace(request.ProcessName) ||
                                    !string.IsNullOrWhiteSpace(request.ProcessInstanceId);
            var replacementRequest = request with
            {
                SessionId = null,
                ProcessName = hasExplicitTarget ? request.ProcessName : previous.ProcessName
            };

            var successorController = new AutomationController();
            SessionState? successor = null;
            try
            {
                var attached = await successorController.AttachAsync(replacementRequest, cancellationToken)
                    .ConfigureAwait(false);
                var successorIdentity = ParseAttachedIdentity(attached);
                var successorSessionId = CreateUniqueSessionId();
                var successorPolicy = request.InteractionPolicy is null
                    ? previous.InteractionPolicy
                    : InteractionPolicyResolver.Resolve(request.InteractionPolicy);
                successor = new SessionState(
                    successorSessionId,
                    successorController,
                    attached.Pid,
                    attached.ProcessName,
                    successorIdentity,
                    successorPolicy);
                var activeWindow = await InitializeActiveWindowRequiredAsync(successor, cancellationToken)
                    .ConfigureAwait(false);
                if (ProcessTargetResolver.Observe(successorIdentity) != ProcessInstanceState.Current)
                {
                    throw new InvalidOperationException(
                        $"stale_process_candidate: replacement process {successor.Pid} exited before recovery could commit. " +
                        "The previous session was not changed.");
                }

                if (!previous.TryBeginEnding(cancellationToken))
                {
                    throw new InvalidOperationException($"Session '{previousSessionId}' is already ending.");
                }

                var predecessorRetirementStarted = false;
                try
                {
                    if (ProcessTargetResolver.Observe(successorIdentity) != ProcessInstanceState.Current)
                    {
                        throw new InvalidOperationException(
                            $"stale_process_candidate: replacement process {successor.Pid} exited before recovery " +
                            "could commit. The previous session was not changed.");
                    }

                    activeWindow = await InitializeActiveWindowRequiredAsync(
                        successor,
                        CancellationToken.None).ConfigureAwait(false);
                    if (ProcessTargetResolver.Observe(successorIdentity) != ProcessInstanceState.Current ||
                        !IsWindowHandleValid(activeWindow.Handle, successorIdentity))
                    {
                        throw new InvalidOperationException(
                            $"stale_process_candidate: replacement process {successor.Pid} or its active window " +
                            "changed while recovery was committing. The previous session was not changed.");
                    }

                    var retiredIdentities = await previous.Controller
                        .BeginProcessReplacementRetirementAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    predecessorRetirementStarted = true;
                    successorController.ImportRetiredProcessIdentities(retiredIdentities);

                    if (ProcessTargetResolver.Observe(successorIdentity) != ProcessInstanceState.Current)
                    {
                        throw new InvalidOperationException(
                            $"stale_process_candidate: replacement process {successor.Pid} exited at the recovery " +
                            "commit point. The previous session was not changed.");
                    }

                    activeWindow = await InitializeActiveWindowRequiredAsync(
                        successor,
                        CancellationToken.None).ConfigureAwait(false);
                    if (ProcessTargetResolver.Observe(successorIdentity) != ProcessInstanceState.Current ||
                        !IsWindowHandleValid(activeWindow.Handle, successorIdentity))
                    {
                        throw new InvalidOperationException(
                            $"stale_process_candidate: replacement process {successor.Pid} or its active window " +
                            "changed at the recovery commit point. The previous session was not changed.");
                    }

                    if (!_sessions.TryAdd(successorSessionId, successor))
                    {
                        throw new InvalidOperationException("Failed to register replacement session.");
                    }

                    var retired = new RetiredSession(successorSessionId, previous.Pid, successor.Pid);
                    if (!_retiredSessions.TryAdd(previousSessionId, retired))
                    {
                        _sessions.TryRemove(successorSessionId, out _);
                        throw new InvalidOperationException(
                            $"Failed to publish replacement metadata for session '{previousSessionId}'.");
                    }

                    if (!_sessions.TryRemove(previousSessionId, out var removed) ||
                        !ReferenceEquals(removed, previous))
                    {
                        _retiredSessions.TryRemove(previousSessionId, out _);
                        _sessions.TryRemove(successorSessionId, out _);
                        throw new InvalidOperationException(
                            $"Session '{previousSessionId}' changed while replacement was being committed.");
                    }

                    FinalizeRetiredSession(previousSessionId, retired);
                }
                catch
                {
                    if (predecessorRetirementStarted)
                    {
                        previous.Controller.CancelProcessReplacementRetirement();
                    }

                    previous.CancelEnding();
                    throw;
                }

                var subscriptionsCleared = false;
                if (releaseReplacedSessionResources is not null)
                {
                    try
                    {
                        await releaseReplacedSessionResources().ConfigureAwait(false);
                        subscriptionsCleared = true;
                    }
                    catch
                    {
                        // Registry commit is final. Controller disposal below releases any remaining target resources.
                    }
                }

                try
                {
                    previous.Controller.Dispose();
                }
                catch
                {
                }
                return new AttachToAppResponse(
                    successorSessionId,
                    successor.Pid,
                    successor.ProcessName,
                    successorPolicy.ToContract())
                {
                    ProcessInstanceId = successorIdentity.Value,
                    ActiveWindow = activeWindow,
                    Recovery = new AttachRecoveryInfo(
                        PreviousSessionId: previousSessionId,
                        SuccessorSessionId: successorSessionId,
                        PreviousPid: previous.Pid,
                        WindowHandlesInvalidated: true,
                        ElementIdsInvalidated: true,
                        SubscriptionsCleared: subscriptionsCleared)
                };
            }
            catch (ProcessSelectionAmbiguityException ex)
            {
                successorController.Dispose();
                throw new ProcessSelectionAmbiguityException(ex.Ambiguity with
                {
                    Recovery = $"Retry attach_to_app with sessionId '{previousSessionId}' and one candidate " +
                               "processInstanceId (preferred) or pid."
                });
            }
            catch
            {
                if (successor is null ||
                    !_sessions.TryGetValue(successor.SessionId, out var registered) ||
                    !ReferenceEquals(successor, registered))
                {
                    successorController.Dispose();
                }

                throw;
            }
        }
        finally
        {
            _replacementMutex.Release();
        }
    }

    public async Task<CloseAppResponse> CloseSessionAsync(string sessionId, CloseAppRequest request, CancellationToken cancellationToken)
        => await CloseSessionAsync(sessionId, request, releaseSessionResources: null, cancellationToken).ConfigureAwait(false);

    public Task<CloseAppResponse> CloseSessionAsync(
        string sessionId,
        CloseAppRequest request,
        Func<Task>? releaseSessionResources,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        return EndSessionAsync(
            sessionId,
            releaseSessionResources,
            async session =>
            {
                var response = await session.Controller.RunExclusiveAsync(
                    () => session.Controller.CloseAsync(request, CancellationToken.None),
                    CancellationToken.None).ConfigureAwait(false);
                return response with
                {
                    Closed = true,
                    SessionRemoved = true
                };
            },
            cancellationToken);
    }

    public DetachSessionResponse DetachSession(string sessionId) =>
        DetachSessionAsync(sessionId, releaseSessionResources: null, CancellationToken.None).GetAwaiter().GetResult();

    public Task<DetachSessionResponse> DetachSessionAsync(
        string sessionId,
        Func<Task>? releaseSessionResources,
        CancellationToken cancellationToken) =>
        DetachSessionCoreAsync(sessionId, releaseSessionResources, cancellationToken);

    private async Task<DetachSessionResponse> DetachSessionCoreAsync(
        string sessionId,
        Func<Task>? releaseSessionResources,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = BeginEndingSession(sessionId, cancellationToken);
        try
        {
            var processWasRunningObserved = TryObserveProcessRunning(session.ProcessIdentity, out var processWasRunning);
            if (releaseSessionResources is not null)
            {
                await releaseSessionResources().ConfigureAwait(false);
            }

            var processStillRunningObserved = TryObserveProcessRunning(session.ProcessIdentity, out var processStillRunning);
            return new DetachSessionResponse(
                Pid: session.Pid,
                SessionRemoved: true,
                ProcessWasRunning: processWasRunning,
                ProcessStillRunning: processStillRunning)
            {
                ProcessWasRunningObserved = processWasRunningObserved,
                ProcessStillRunningObserved = processStillRunningObserved
            };
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            session.Controller.Dispose();
        }
    }

    public Task<CloseAppResponse> CloseApplicationAsync(
        string sessionId,
        int timeoutMs,
        CancellationToken cancellationToken) =>
        CloseApplicationAsync(sessionId, timeoutMs, releaseSessionResources: null, cancellationToken);

    public Task<CloseAppResponse> CloseApplicationAsync(
        string sessionId,
        int timeoutMs,
        Func<Task>? releaseSessionResources,
        CancellationToken cancellationToken) =>
        EndSessionAsync(
            sessionId,
            releaseSessionResources,
            async session =>
            {
                var response = await session.Controller.RunExclusiveAsync(
                    () => session.Controller.CloseApplicationAsync(timeoutMs, CancellationToken.None),
                    CancellationToken.None).ConfigureAwait(false);
                return response with { SessionRemoved = true };
            },
            cancellationToken);

    public Task<CloseAppResponse> TerminateApplicationAsync(
        string sessionId,
        int timeoutMs,
        CancellationToken cancellationToken) =>
        TerminateApplicationAsync(sessionId, timeoutMs, releaseSessionResources: null, cancellationToken);

    public Task<CloseAppResponse> TerminateApplicationAsync(
        string sessionId,
        int timeoutMs,
        Func<Task>? releaseSessionResources,
        CancellationToken cancellationToken) =>
        EndSessionAsync(
            sessionId,
            releaseSessionResources,
            async session =>
            {
                var response = await session.Controller.RunExclusiveAsync(
                    () => session.Controller.TerminateApplicationAsync(timeoutMs, CancellationToken.None),
                    CancellationToken.None).ConfigureAwait(false);
                return response with { SessionRemoved = true };
            },
            cancellationToken);

    public async Task<FocusWindowResponse> SetActiveWindowAsync(
        string sessionId,
        FocusWindowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        var session = GetSession(sessionId);
        var response = await session.Controller.RunExclusiveAsync(() => session.Controller.FocusWindowAsync(request, cancellationToken), cancellationToken);
        session.RecordWindowSelection(response.Handle, response.Title);
        return response;
    }

    public async Task<GetActiveWindowResponse> GetActiveWindowAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = GetSession(sessionId);
        return await session.Controller.RunExclusiveAsync(async () =>
        {
            var trace = session.Controller.BeginToolTrace("get_active_window");
            try
            {
                var (handle, title) = ReconcileActiveWindow(session);

                if (handle != 0)
                {
                    session.Controller.TrackOrRejectExternalWindowHandle(handle);
                    var response = new GetActiveWindowResponse(handle, title);
                    trace?.SetSummary($"handle={response.Handle} title={response.Title}");
                    return response;
                }

                var window = await session.Controller.GetWindowMetadataAsync(cancellationToken: cancellationToken);
                var fallback = session.RecordFallbackWindowIfEmpty(window.Handle, window.Title);
                var result = new GetActiveWindowResponse(fallback.Handle, fallback.Title);
                trace?.SetSummary($"handle={result.Handle} title={result.Title}");
                return result;
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
        }, cancellationToken);
    }

    public (AutomationController Controller, long? WindowHandle) GetController(
        string sessionId,
        long? windowHandleOverride = null,
        IReadOnlyList<long>? additionalWindowHandles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = GetSession(sessionId);
        if (additionalWindowHandles is not null)
        {
            foreach (var windowHandle in additionalWindowHandles)
            {
                session.Controller.TrackOrRejectExternalWindowHandle(windowHandle);
            }
        }

        if (windowHandleOverride is long requestedHandle)
        {
            session.Controller.TrackOrRejectExternalWindowHandle(requestedHandle);
            return (session.Controller, requestedHandle);
        }

        var (activeHandle, _) = ReconcileActiveWindow(session);
        session.Controller.TrackOrRejectExternalWindowHandle(activeHandle);
        long? effectiveHandle = activeHandle != 0 ? activeHandle : null;
        return (session.Controller, effectiveHandle);
    }

    public InteractionPolicy ResolveInteractionPolicy(
        string sessionId,
        InteractionPolicy? operationOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = GetSession(sessionId);
        return InteractionPolicyResolver.Resolve(operationOverride, session.InteractionPolicy).ToContract();
    }

    public void EnsureSessionActive(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _ = GetSession(sessionId);
    }

    public T RegisterSessionResource<T>(string sessionId, Func<T> register)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(register);

        var session = GetSessionEntry(sessionId);
        return session.RegisterResource(register);
    }

    private static string CreateSessionId() => Guid.NewGuid().ToString("N");

    private string CreateUniqueSessionId()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var sessionId = CreateSessionId();
            if (!_sessions.ContainsKey(sessionId) && !_retiredSessions.ContainsKey(sessionId))
            {
                return sessionId;
            }
        }

        throw new InvalidOperationException("Failed to allocate a unique replacement session ID.");
    }

    private static ProcessInstanceIdentity ParseAttachedIdentity(AttachToAppResponse attached)
        => ParseProcessIdentity(attached.ProcessInstanceId, attached.Pid);

    private static ProcessInstanceIdentity ParseProcessIdentity(string? processInstanceId, int pid)
    {
        if (!ProcessInstanceIdentity.TryParse(processInstanceId ?? string.Empty, out var identity) ||
            identity.Pid != pid)
        {
            throw new InvalidOperationException(
                $"process_identity_unavailable: process {pid} did not return a stable identity.");
        }

        return identity;
    }

    private void FinalizeRetiredSession(string sessionId, RetiredSession retired)
    {
        lock (_retiredSessionSync)
        {
            foreach (var entry in _retiredSessions.ToArray())
            {
                if (string.Equals(
                        entry.Value.SuccessorSessionId,
                        sessionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _retiredSessions[entry.Key] = entry.Value with
                    {
                        SuccessorSessionId = retired.SuccessorSessionId,
                        SuccessorPid = retired.SuccessorPid
                    };
                }
            }

            _retiredSessionOrder.Enqueue(sessionId);
            while (_retiredSessionOrder.Count > MaximumRetiredSessions)
            {
                var expired = _retiredSessionOrder.Dequeue();
                _retiredSessions.TryRemove(expired, out _);
            }
        }
    }

    private async Task<T> EndSessionAsync<T>(
        string sessionId,
        Func<Task>? releaseSessionResources,
        Func<SessionState, Task<T>> endSession,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(endSession);

        var session = BeginEndingSession(sessionId, cancellationToken);

        try
        {
            if (releaseSessionResources is not null)
            {
                await releaseSessionResources().ConfigureAwait(false);
            }

            return await endSession(session).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            session.Controller.Dispose();
        }
    }

    private SessionState BeginEndingSession(string sessionId, CancellationToken cancellationToken)
    {
        var session = GetSessionEntry(sessionId);
        if (session.TryBeginEnding(cancellationToken))
        {
            return session;
        }

        throw new InvalidOperationException($"Session '{sessionId}' is already ending.");
    }

    private SessionState GetSession(string sessionId)
    {
        var session = GetSessionEntry(sessionId);
        session.ThrowIfEnding();
        return session;
    }

    private SessionState GetSessionEntry(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        if (_retiredSessions.TryGetValue(sessionId, out var retired))
        {
            throw new InvalidOperationException(
                $"stale_session: process_replaced: session '{sessionId}' for process {retired.PreviousPid} " +
                $"was replaced by session '{retired.SuccessorSessionId}' for process {retired.SuccessorPid}. " +
                "Use the successorSessionId and reacquire window handles and element IDs; all identities from the retired session are stale.");
        }

        throw new InvalidOperationException($"Unknown sessionId '{sessionId}'.");
    }

    private static SessionInfo ToSessionInfo(
        SessionState session,
        ProcessInstanceState processState,
        bool controllerAttached)
    {
        var (handle, title) = session.GetActiveWindow();
        var wpfCapability = processState == ProcessInstanceState.Current && controllerAttached
            ? session.Controller.GetWpfBackendCapabilityState()
            : null;
        var capabilityStates = ProjectBackendCapabilityStates(
            processState,
            controllerAttached,
            wpfCapability);
        var capabilities = capabilityStates
            .Where(capability => string.Equals(capability.State, "ready", StringComparison.Ordinal))
            .Select(capability => capability.Backend)
            .ToArray();

        return new SessionInfo(
            SessionId: session.SessionId,
            Pid: session.Pid,
            ProcessName: session.ProcessName,
            ActiveWindowHandle: handle,
            ActiveWindowTitle: title,
            CreatedAtUtc: session.CreatedAtUtc,
            BackendCapabilities: capabilities,
            BackendCapabilityStates: capabilityStates,
            InteractionPolicy: session.InteractionPolicy.ToContract());
    }

    internal static IReadOnlyList<BackendCapabilityState> ProjectBackendCapabilityStates(
        ProcessInstanceState processState,
        bool controllerAttached,
        BackendCapabilityState? currentWpfCapability = null)
    {
        if (processState == ProcessInstanceState.Current && controllerAttached)
        {
            return
            [
                new BackendCapabilityState("uia", "ready"),
                currentWpfCapability ?? throw new ArgumentNullException(nameof(currentWpfCapability))
            ];
        }

        var failure = processState switch
        {
            ProcessInstanceState.Current => FailureDiagnostics.AttachmentFailure(),
            ProcessInstanceState.ExitedOrReused => FailureDiagnostics.TargetExited(),
            ProcessInstanceState.Unavailable => FailureDiagnostics.Create(
                code: "process_state_unavailable",
                stage: FailureDiagnostics.Stages.TargetShutdown,
                detail: "The target process state could not be observed.",
                retryable: true,
                recoveryActions: [FailureDiagnostics.Recovery.Retry]),
            _ => throw new ArgumentOutOfRangeException(nameof(processState), processState, null)
        };

        return
        [
            new BackendCapabilityState("uia", "unavailable") { Failure = failure },
            new BackendCapabilityState("wpf", "unavailable") { Failure = failure }
        ];
    }

    private static async Task RefreshBackendCapabilitiesAsync(SessionState session, CancellationToken cancellationToken)
    {
        try
        {
            _ = await session.Controller.RunExclusiveAsync(
                () => session.Controller.RefreshWpfBackendCapabilityAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static async Task InitializeActiveWindowAsync(SessionState session, CancellationToken cancellationToken)
    {
        try
        {
            var window = await session.Controller.RunExclusiveAsync(
                () => session.Controller.GetWindowMetadataAsync(cancellationToken: cancellationToken),
                cancellationToken);
            session.RecordWindowSelection(window.Handle, window.Title, preserveAsFallback: true);
        }
        catch
        {
        }
    }

    private static async Task<GetActiveWindowResponse> InitializeActiveWindowRequiredAsync(
        SessionState session,
        CancellationToken cancellationToken)
    {
        var window = await session.Controller.RunExclusiveAsync(
            () => session.Controller.GetWindowMetadataAsync(cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (window.Handle == 0)
        {
            throw new InvalidOperationException(
                $"active_window_unavailable: replacement process {session.Pid} has no valid main window.");
        }

        session.RecordWindowSelection(window.Handle, window.Title, preserveAsFallback: true);
        return window;
    }

    private static SessionWindowSelection ReconcileActiveWindow(SessionState session)
        => session.ReconcileActiveWindow(
            () => ObserveTopLevelWindows(session.ProcessIdentity),
            handle => IsWindowHandleValid(handle, session.ProcessIdentity));

    private static SessionTopLevelWindowGraph ObserveTopLevelWindows(ProcessInstanceIdentity expectedProcess)
    {
        if (!OperatingSystem.IsWindows() ||
            expectedProcess.Pid <= 0 ||
            !ProcessTargetResolver.IsCurrent(expectedProcess))
        {
            return SessionTopLevelWindowGraph.Unavailable;
        }

        try
        {
            var windows = new List<SessionTopLevelWindowObservation>();
            var zOrder = 0;
            EnumWindowsProc callback = (hwnd, _) =>
            {
                var currentZOrder = zOrder++;
                try
                {
                    GetWindowThreadProcessId(hwnd, out var processId);
                    if (processId == (uint)expectedProcess.Pid)
                    {
                        windows.Add(new SessionTopLevelWindowObservation(
                            Handle: hwnd.ToInt64(),
                            Title: GetNativeWindowTitle(hwnd),
                            OwnerHandle: GetWindow(hwnd, GW_OWNER).ToInt64(),
                            IsVisible: IsWindowVisible(hwnd),
                            IsEnabled: IsWindowEnabled(hwnd),
                            ZOrder: currentZOrder));
                    }
                }
                catch
                {
                }

                return true;
            };

            return EnumWindows(callback, IntPtr.Zero)
                ? SessionTopLevelWindowGraph.Create(windows)
                : SessionTopLevelWindowGraph.Unavailable;
        }
        catch
        {
            return SessionTopLevelWindowGraph.Unavailable;
        }
    }

    private static string GetNativeWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        return GetWindowText(hwnd, title, title.Capacity) > 0
            ? title.ToString()
            : string.Empty;
    }

    private static bool IsWindowHandleValid(long handle, ProcessInstanceIdentity expectedProcess)
    {
        if (!OperatingSystem.IsWindows())
        {
            return handle != 0;
        }

        try
        {
            if (handle == 0)
            {
                return false;
            }

            var hwnd = new IntPtr(handle);
            if (!IsWindow(hwnd))
            {
                return false;
            }

            if (expectedProcess.Pid > 0)
            {
                if (!ProcessTargetResolver.IsCurrent(expectedProcess))
                {
                    return false;
                }

                _ = GetWindowThreadProcessId(hwnd, out var actualPid);
                if (actualPid != (uint)expectedProcess.Pid)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryObserveProcessRunning(ProcessInstanceIdentity identity, out bool isRunning)
    {
        if (identity.Pid <= 0)
        {
            isRunning = false;
            return true;
        }

        switch (ProcessTargetResolver.Observe(identity))
        {
            case ProcessInstanceState.Current:
                isRunning = true;
                return true;
            case ProcessInstanceState.ExitedOrReused:
                isRunning = false;
                return true;
            default:
                isRunning = false;
                return false;
        }
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private const uint GW_OWNER = 4;
}
