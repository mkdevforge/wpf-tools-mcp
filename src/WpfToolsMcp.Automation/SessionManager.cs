using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal readonly record struct SessionWindowSelection(long Handle, string Title);

internal sealed class SessionWindowSelectionHistory
{
    private sealed record Entry(long Handle, string Title, bool PreserveAsFallback);

    private const int DefaultCapacity = 16;
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
    {
        if (handle == 0)
        {
            return;
        }

        lock (_sync)
        {
            var existingIndex = _entries.FindIndex(entry => entry.Handle == handle);
            var wasPreserved = existingIndex >= 0 && _entries[existingIndex].PreserveAsFallback;
            if (existingIndex >= 0)
            {
                _entries.RemoveAt(existingIndex);
            }

            var entry = new Entry(handle, title ?? "", preserveAsFallback || wasPreserved);
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

    internal SessionWindowSelection GetActive()
    {
        lock (_sync)
        {
            return _active;
        }
    }

    internal SessionWindowSelection Reconcile(Func<long, bool> isWindowValid)
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
                if (isWindowValid(entry.Handle))
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
}

public sealed class SessionManager : IDisposable
{
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
            EffectiveInteractionPolicy interactionPolicy)
        {
            SessionId = sessionId;
            Controller = controller;
            Pid = pid;
            ProcessName = processName;
            InteractionPolicy = interactionPolicy;
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        }

        public string SessionId { get; }
        public AutomationController Controller { get; }
        public int Pid { get; }
        public string ProcessName { get; }
        public EffectiveInteractionPolicy InteractionPolicy { get; }
        public string CreatedAtUtc { get; }

        public void RecordWindowSelection(long handle, string title, bool preserveAsFallback = false) =>
            _windowSelections.RecordSelection(handle, title, preserveAsFallback);

        public SessionWindowSelection GetActiveWindow() => _windowSelections.GetActive();

        public SessionWindowSelection ReconcileActiveWindow(Func<long, bool> isWindowValid) =>
            _windowSelections.Reconcile(isWindowValid);

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
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

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
    }

    public async Task<ListSessionsResponse> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values
            .Where(s => !s.IsEnding)
            .OrderBy(s => s.CreatedAtUtc, StringComparer.Ordinal)
            .ToArray();

        foreach (var session in sessions)
        {
            await RefreshBackendCapabilitiesAsync(session, cancellationToken).ConfigureAwait(false);
            _ = ReconcileActiveWindow(session);
        }

        var activeSessions = sessions
            .Where(session =>
                !session.IsEnding &&
                _sessions.TryGetValue(session.SessionId, out var current) &&
                ReferenceEquals(session, current))
            .ToArray();

        return new ListSessionsResponse(activeSessions.Select(ToSessionInfo).ToArray());
    }

    public async Task<LaunchAppResponse> LaunchAppAsync(LaunchAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var controller = new AutomationController();
        try
        {
            var launched = await controller.LaunchAsync(request, cancellationToken);
            var sessionId = CreateSessionId();
            var interactionPolicy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var session = new SessionState(
                sessionId,
                controller,
                launched.Pid,
                launched.ProcessName,
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
    {
        ArgumentNullException.ThrowIfNull(request);

        var controller = new AutomationController();
        try
        {
            var attached = await controller.AttachAsync(request, cancellationToken);
            var sessionId = CreateSessionId();
            var interactionPolicy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var session = new SessionState(
                sessionId,
                controller,
                attached.Pid,
                attached.ProcessName,
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
                interactionPolicy.ToContract());
        }
        catch
        {
            controller.Dispose();
            throw;
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
            var processWasRunningObserved = TryObserveProcessRunning(session.Pid, out var processWasRunning);
            if (releaseSessionResources is not null)
            {
                await releaseSessionResources().ConfigureAwait(false);
            }

            var processStillRunningObserved = TryObserveProcessRunning(session.Pid, out var processStillRunning);
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
                    var response = new GetActiveWindowResponse(handle, title);
                    trace?.SetSummary($"handle={response.Handle} title={response.Title}");
                    return response;
                }

                var window = await session.Controller.GetWindowMetadataAsync(cancellationToken: cancellationToken);
                session.RecordWindowSelection(window.Handle, window.Title, preserveAsFallback: true);

                var result = new GetActiveWindowResponse(window.Handle, window.Title);
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

    public (AutomationController Controller, long? WindowHandle) GetController(string sessionId, long? windowHandleOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = GetSession(sessionId);
        if (windowHandleOverride is long requestedHandle)
        {
            return (session.Controller, requestedHandle);
        }

        var (activeHandle, _) = ReconcileActiveWindow(session);
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

        throw new InvalidOperationException($"Unknown sessionId '{sessionId}'.");
    }

    private static SessionInfo ToSessionInfo(SessionState session)
    {
        var (handle, title) = session.GetActiveWindow();

        var uiaState = session.Controller.IsAttached ? "ready" : "unavailable";
        var wpfState = session.Controller.IsAttached
            ? session.Controller.WpfBackendCapabilityState
            : "unavailable";

        var capabilities = new List<string>();
        if (session.Controller.IsAttached)
        {
            capabilities.Add("uia");
        }

        if (session.Controller.IsAttached && session.Controller.IsAgentConnected)
        {
            capabilities.Add("wpf");
        }

        var capabilityStates = new[]
        {
            new BackendCapabilityState("uia", uiaState),
            new BackendCapabilityState("wpf", wpfState)
        };

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

    private static async Task RefreshBackendCapabilitiesAsync(SessionState session, CancellationToken cancellationToken)
    {
        try
        {
            _ = await session.Controller.RunExclusiveAsync(
                () => session.Controller.RefreshWpfBackendCapabilityAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
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

    private static SessionWindowSelection ReconcileActiveWindow(SessionState session)
    {
        var active = session.ReconcileActiveWindow(handle => IsWindowHandleValid(handle, session.Pid));
        if (!TryGetOwnedModalPopup(active.Handle, session.Pid, out var modalPopup))
        {
            return active;
        }

        session.RecordWindowSelection(modalPopup.Handle, modalPopup.Title);
        return modalPopup;
    }

    private static bool TryGetOwnedModalPopup(
        long ownerHandle,
        int expectedPid,
        out SessionWindowSelection modalPopup)
    {
        modalPopup = default;
        if (!OperatingSystem.IsWindows() || ownerHandle == 0 || expectedPid <= 0)
        {
            return false;
        }

        try
        {
            var owner = new IntPtr(ownerHandle);
            var popup = GetLastActivePopup(owner);
            if (popup == IntPtr.Zero || popup == owner || !IsWindow(popup) || !IsWindowVisible(popup))
            {
                return false;
            }

            _ = GetWindowThreadProcessId(popup, out var popupPid);
            if (popupPid != (uint)expectedPid || !IsOwnedBy(popup, owner))
            {
                return false;
            }

            var immediateOwner = GetWindow(popup, GW_OWNER);
            if (immediateOwner == IntPtr.Zero || IsWindowEnabled(immediateOwner))
            {
                return false;
            }

            modalPopup = new SessionWindowSelection(popup.ToInt64(), GetNativeWindowTitle(popup));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedBy(IntPtr candidate, IntPtr expectedOwner)
    {
        var current = candidate;
        for (var depth = 0; depth < 16; depth++)
        {
            current = GetWindow(current, GW_OWNER);
            if (current == expectedOwner)
            {
                return true;
            }

            if (current == IntPtr.Zero)
            {
                return false;
            }
        }

        return false;
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

    private static bool IsWindowHandleValid(long handle, int expectedPid)
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

            if (expectedPid > 0)
            {
                _ = GetWindowThreadProcessId(hwnd, out var actualPid);
                if (actualPid != (uint)expectedPid)
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

    private static bool TryObserveProcessRunning(int pid, out bool isRunning)
    {
        isRunning = false;
        if (pid <= 0)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            isRunning = !process.HasExited;
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr GetLastActivePopup(IntPtr hWnd);

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
