using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed class SessionManager : IDisposable
{
    private sealed class SessionState
    {
        private readonly object _sync = new();

        public SessionState(string sessionId, AutomationController controller, int pid, string processName)
        {
            SessionId = sessionId;
            Controller = controller;
            Pid = pid;
            ProcessName = processName;
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        }

        public string SessionId { get; }
        public AutomationController Controller { get; }
        public int Pid { get; }
        public string ProcessName { get; }
        public string CreatedAtUtc { get; }

        public long ActiveWindowHandle { get; private set; }
        public string ActiveWindowTitle { get; private set; } = "";

        public void SetActiveWindow(long handle, string title)
        {
            lock (_sync)
            {
                ActiveWindowHandle = handle;
                ActiveWindowTitle = title ?? "";
            }
        }

        public (long Handle, string Title) GetActiveWindow()
        {
            lock (_sync)
            {
                return (ActiveWindowHandle, ActiveWindowTitle);
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

    public ListSessionsResponse ListSessions()
    {
        var sessions = _sessions.Values
            .OrderBy(s => s.CreatedAtUtc, StringComparer.Ordinal)
            .Select(ToSessionInfo)
            .ToArray();

        return new ListSessionsResponse(sessions);
    }

    public async Task<LaunchAppResponse> LaunchAppAsync(LaunchAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var controller = new AutomationController();
        try
        {
            var launched = await controller.LaunchAsync(request, cancellationToken);
            var sessionId = CreateSessionId();
            var session = new SessionState(sessionId, controller, launched.Pid, launched.ProcessName);

            if (!_sessions.TryAdd(sessionId, session))
            {
                throw new InvalidOperationException("Failed to register new session.");
            }

            await InitializeActiveWindowAsync(session, cancellationToken);
            return new LaunchAppResponse(sessionId, launched.Pid, launched.ProcessName);
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
            var session = new SessionState(sessionId, controller, attached.Pid, attached.ProcessName);

            if (!_sessions.TryAdd(sessionId, session))
            {
                throw new InvalidOperationException("Failed to register new session.");
            }

            await InitializeActiveWindowAsync(session, cancellationToken);
            return new AttachToAppResponse(sessionId, attached.Pid, attached.ProcessName);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    public async Task<CloseAppResponse> CloseSessionAsync(string sessionId, CloseAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        if (!_sessions.TryRemove(sessionId, out var session))
        {
            throw new InvalidOperationException($"Unknown sessionId '{sessionId}'.");
        }

        try
        {
            return await session.Controller.RunExclusiveAsync(() => session.Controller.CloseAsync(request, cancellationToken), cancellationToken);
        }
        finally
        {
            session.Controller.Dispose();
        }
    }

    public async Task<FocusWindowResponse> SetActiveWindowAsync(
        string sessionId,
        FocusWindowRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        var session = GetSession(sessionId);
        var response = await session.Controller.RunExclusiveAsync(() => session.Controller.FocusWindowAsync(request, cancellationToken), cancellationToken);
        session.SetActiveWindow(response.Handle, response.Title);
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
                var (handle, title) = session.GetActiveWindow();

                if (handle != 0 && IsWindowHandleValid(handle, session.Pid))
                {
                    var response = new GetActiveWindowResponse(handle, title);
                    trace?.SetSummary($"handle={response.Handle} title={response.Title}");
                    return response;
                }

                if (handle != 0 && !IsWindowHandleValid(handle, session.Pid))
                {
                    session.SetActiveWindow(0, "");
                }

                var focused = await session.Controller.FocusWindowAsync(new FocusWindowRequest(), cancellationToken);
                session.SetActiveWindow(focused.Handle, focused.Title);

                var result = new GetActiveWindowResponse(focused.Handle, focused.Title);
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
        var (activeHandle, _) = session.GetActiveWindow();

        long? effectiveHandle = windowHandleOverride ?? (activeHandle != 0 ? activeHandle : null);

        if (windowHandleOverride is null &&
            effectiveHandle is long handle &&
            handle != 0 &&
            !IsWindowHandleValid(handle, session.Pid))
        {
            session.SetActiveWindow(0, "");
            effectiveHandle = null;
        }

        return (session.Controller, effectiveHandle);
    }

    private static string CreateSessionId() => Guid.NewGuid().ToString("N");

    private SessionState GetSession(string sessionId)
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

        var capabilities = new List<string> { "uia" };
        if (session.Controller.IsAgentConnected)
        {
            capabilities.Add("wpf");
        }

        return new SessionInfo(
            SessionId: session.SessionId,
            Pid: session.Pid,
            ProcessName: session.ProcessName,
            ActiveWindowHandle: handle,
            ActiveWindowTitle: title,
            CreatedAtUtc: session.CreatedAtUtc,
            BackendCapabilities: capabilities);
    }

    private static async Task InitializeActiveWindowAsync(SessionState session, CancellationToken cancellationToken)
    {
        try
        {
            var focused = await session.Controller.RunExclusiveAsync(
                () => session.Controller.FocusWindowAsync(new FocusWindowRequest(), cancellationToken),
                cancellationToken);
            session.SetActiveWindow(focused.Handle, focused.Title);
        }
        catch
        {
            session.SetActiveWindow(0, "");
        }
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
                if (actualPid != 0 && actualPid != (uint)expectedPid)
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

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
