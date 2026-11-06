using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal enum InputDesktopAccess
{
    Unknown,
    Opened,
    AccessDenied
}

/// <summary>Raw facts about the server's session and desktop. Null means the fact could not be read.</summary>
internal sealed record InteractiveDesktopObservation(
    int? SessionId,
    string? WindowStationName,
    bool? WindowStationVisible,
    string? SessionConnectState,
    bool? SessionLocked,
    string? ServerDesktopName,
    InputDesktopAccess InputDesktopAccess,
    string? InputDesktopName,
    bool? ScreensaverRunning,
    IReadOnlyList<string> UnverifiedChecks);

/// <summary>
/// Decides whether physical input and screen capture can work in the server's session.
/// Physical input (SendInput), foreground activation, and screen capture all act on the input
/// desktop of an active, unlocked, interactive session. Without one they fail or return black
/// images, so callers get a precise reason instead.
/// </summary>
public static class InteractiveDesktop
{
    internal const string ErrorCode = "interactive_desktop_unavailable";
    internal const string ErrorStage = "interactive_desktop";
    internal const string InteractiveWindowStation = "WinSta0";

    internal static class Reasons
    {
        internal const string ServiceSession = "service_session";
        internal const string NonInteractiveWindowStation = "non_interactive_window_station";
        internal const string SessionDisconnected = "session_disconnected";
        internal const string SessionNotActive = "session_not_active";
        internal const string SessionLocked = "session_locked";
        internal const string SecureDesktopActive = "secure_desktop_active";
        internal const string InputDesktopMismatch = "input_desktop_mismatch";
        internal const string ScreensaverRunning = "screensaver_running";
    }

    internal static class Checks
    {
        internal const string SessionId = "session_id";
        internal const string WindowStation = "window_station";
        internal const string SessionConnectState = "session_connect_state";
        internal const string SessionLock = "session_lock";
        internal const string ServerDesktop = "server_desktop";
        internal const string InputDesktop = "input_desktop";
        internal const string Screensaver = "screensaver";
    }

    public static InteractiveDesktopStatus GetStatus() => Evaluate(Observe());

    /// <summary>
    /// Throws <c>interactive_desktop_unavailable</c> when the session demonstrably cannot take
    /// physical input or produce screen captures for <paramref name="operation"/>.
    /// </summary>
    internal static void EnsureAvailable(string operation)
    {
        var status = GetStatus();
        if (!status.Available)
        {
            throw CreateUnavailableException(operation, status);
        }
    }

    internal static InteractiveDesktopStatus Evaluate(InteractiveDesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var (reason, detail) = FindUnavailableReason(observation);
        return new InteractiveDesktopStatus(
            Available: reason is null,
            UnavailableReason: reason,
            Detail: detail,
            SessionId: observation.SessionId,
            SessionConnectState: observation.SessionConnectState,
            SessionLocked: observation.SessionLocked,
            WindowStation: observation.WindowStationName,
            ServerDesktop: observation.ServerDesktopName,
            InputDesktop: observation.InputDesktopName,
            ScreensaverRunning: observation.ScreensaverRunning,
            UnverifiedChecks: observation.UnverifiedChecks.Count == 0 ? null : observation.UnverifiedChecks);
    }

    internal static ActionableFailureException CreateUnavailableException(
        string operation,
        InteractiveDesktopStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(status);
        if (status.Available || status.UnavailableReason is null)
        {
            throw new ArgumentException("The status does not describe an unavailable desktop.", nameof(status));
        }

        var (retryable, recoveryActions) = GetRecovery(status.UnavailableReason);
        return new ActionableFailureException(
            new FailureInfo(
                ErrorCode,
                ErrorStage,
                $"operation={operation} reason={status.UnavailableReason}. {status.Detail}")
            {
                Retryable = retryable,
                RecoveryActions = recoveryActions
            });
    }

    private static (string? Reason, string? Detail) FindUnavailableReason(InteractiveDesktopObservation observation)
    {
        if (observation.SessionId == 0)
        {
            return (
                Reasons.ServiceSession,
                "The server runs in session 0, a service session without an interactive desktop. " +
                "Run the server in a logged-on user's desktop session.");
        }

        if (observation.WindowStationName is { } windowStation &&
            (!string.Equals(windowStation, InteractiveWindowStation, StringComparison.OrdinalIgnoreCase) ||
             observation.WindowStationVisible == false))
        {
            return (
                Reasons.NonInteractiveWindowStation,
                $"The server runs on window station '{windowStation}', which is not the visible interactive " +
                $"'{InteractiveWindowStation}' (for example under SSH or a service). " +
                "Run the server in a logged-on user's desktop session.");
        }

        if (observation.SessionConnectState is { } connectState &&
            !string.Equals(connectState, "active", StringComparison.Ordinal))
        {
            return string.Equals(connectState, "disconnected", StringComparison.Ordinal)
                ? (
                    Reasons.SessionDisconnected,
                    "The server's session is disconnected, for example after a Remote Desktop connection closed. " +
                    "Reconnect to the session, or move it to the console before disconnecting.")
                : (
                    Reasons.SessionNotActive,
                    $"The server's session is in state '{connectState}', not active.");
        }

        if (observation.SessionLocked == true)
        {
            return (
                Reasons.SessionLocked,
                "The server's session is locked. Unlock it; on unattended machines disable the lock screen and screen saver.");
        }

        if (observation.InputDesktopAccess == InputDesktopAccess.AccessDenied)
        {
            return (
                Reasons.SecureDesktopActive,
                "Input goes to a secure desktop the server cannot access, such as the lock screen or a UAC prompt. " +
                "Dismiss the prompt or unlock the session.");
        }

        if (observation.InputDesktopName is { } inputDesktop &&
            observation.ServerDesktopName is { } serverDesktop &&
            !string.Equals(inputDesktop, serverDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return (
                Reasons.InputDesktopMismatch,
                $"Input goes to desktop '{inputDesktop}', but the server runs on desktop '{serverDesktop}'. " +
                "Switch back to the server's desktop.");
        }

        if (observation.ScreensaverRunning == true)
        {
            return (
                Reasons.ScreensaverRunning,
                "A screen saver is running and would consume input. Dismiss it; on unattended machines disable it.");
        }

        return (null, null);
    }

    private static (bool Retryable, IReadOnlyList<string> RecoveryActions) GetRecovery(string reason) =>
        reason switch
        {
            Reasons.ServiceSession or Reasons.NonInteractiveWindowStation =>
                (false, ["run_in_interactive_session"]),
            Reasons.SessionDisconnected or Reasons.SessionNotActive =>
                (true, ["reconnect_session"]),
            Reasons.SessionLocked =>
                (true, ["unlock_session"]),
            Reasons.SecureDesktopActive =>
                (true, ["dismiss_secure_desktop", "unlock_session"]),
            Reasons.InputDesktopMismatch =>
                (true, ["switch_to_server_desktop"]),
            Reasons.ScreensaverRunning =>
                (true, ["dismiss_screensaver"]),
            _ => (true, ["retry"])
        };

    internal static InteractiveDesktopObservation Observe()
    {
        var unverified = new List<string>();

        int? sessionId = null;
        if (ProcessIdToSessionId((uint)Environment.ProcessId, out var rawSessionId))
        {
            sessionId = (int)rawSessionId;
        }
        else
        {
            LogUnverified(Checks.SessionId, Marshal.GetLastPInvokeError());
            unverified.Add(Checks.SessionId);
        }

        string? windowStationName = null;
        bool? windowStationVisible = null;
        var windowStation = GetProcessWindowStation();
        if (windowStation != IntPtr.Zero &&
            TryGetUserObjectName(windowStation, out var stationName))
        {
            windowStationName = stationName;
            windowStationVisible = TryGetUserObjectFlags(windowStation, out var flags)
                ? (flags & WSF_VISIBLE) != 0
                : null;
        }
        else
        {
            LogUnverified(Checks.WindowStation, Marshal.GetLastPInvokeError());
            unverified.Add(Checks.WindowStation);
        }

        var connectState = TryGetSessionConnectState();
        if (connectState is null)
        {
            unverified.Add(Checks.SessionConnectState);
        }

        var locked = TryGetSessionLocked(sessionId);
        if (locked is null)
        {
            unverified.Add(Checks.SessionLock);
        }

        string? serverDesktopName = null;
        var threadDesktop = GetThreadDesktop(GetCurrentThreadId());
        if (threadDesktop != IntPtr.Zero && TryGetUserObjectName(threadDesktop, out var desktopName))
        {
            serverDesktopName = desktopName;
        }
        else
        {
            LogUnverified(Checks.ServerDesktop, Marshal.GetLastPInvokeError());
            unverified.Add(Checks.ServerDesktop);
        }

        var inputAccess = InputDesktopAccess.Unknown;
        string? inputDesktopName = null;
        var inputDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (inputDesktop == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ERROR_ACCESS_DENIED)
            {
                inputAccess = InputDesktopAccess.AccessDenied;
            }
            else
            {
                LogUnverified(Checks.InputDesktop, error);
                unverified.Add(Checks.InputDesktop);
            }
        }
        else
        {
            try
            {
                inputAccess = InputDesktopAccess.Opened;
                if (TryGetUserObjectName(inputDesktop, out var name))
                {
                    inputDesktopName = name;
                }
                else
                {
                    LogUnverified(Checks.InputDesktop, Marshal.GetLastPInvokeError());
                    unverified.Add(Checks.InputDesktop);
                }
            }
            finally
            {
                _ = CloseDesktop(inputDesktop);
            }
        }

        bool? screensaverRunning = null;
        var running = false;
        if (SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, ref running, 0))
        {
            screensaverRunning = running;
        }
        else
        {
            LogUnverified(Checks.Screensaver, Marshal.GetLastPInvokeError());
            unverified.Add(Checks.Screensaver);
        }

        return new InteractiveDesktopObservation(
            sessionId,
            windowStationName,
            windowStationVisible,
            connectState,
            locked,
            serverDesktopName,
            inputAccess,
            inputDesktopName,
            screensaverRunning,
            unverified);
    }

    private static string? TryGetSessionConnectState()
    {
        if (!WTSQuerySessionInformation(
                WTS_CURRENT_SERVER_HANDLE,
                WTS_CURRENT_SESSION,
                WTSConnectState,
                out var buffer,
                out var bytes))
        {
            LogUnverified(Checks.SessionConnectState, Marshal.GetLastPInvokeError());
            return null;
        }

        try
        {
            if (buffer == IntPtr.Zero || bytes < sizeof(int))
            {
                LogUnverified(Checks.SessionConnectState, 0);
                return null;
            }

            return Marshal.ReadInt32(buffer) switch
            {
                0 => "active",
                1 => "connected",
                2 => "connect_query",
                3 => "shadow",
                4 => "disconnected",
                5 => "idle",
                6 => "listen",
                7 => "reset",
                8 => "down",
                9 => "init",
                var other => $"unknown_{other}"
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool? TryGetSessionLocked(int? expectedSessionId)
    {
        if (!WTSQuerySessionInformation(
                WTS_CURRENT_SERVER_HANDLE,
                WTS_CURRENT_SESSION,
                WTSSessionInfoEx,
                out var buffer,
                out var bytes))
        {
            LogUnverified(Checks.SessionLock, Marshal.GetLastPInvokeError());
            return null;
        }

        try
        {
            // WTSINFOEX { DWORD Level; WTSINFOEX_LEVEL Data; }. The union holds LARGE_INTEGER fields,
            // so Data is 8-byte aligned: SessionId at 8, SessionState at 12, SessionFlags at 16.
            if (buffer == IntPtr.Zero || bytes < 20 || Marshal.ReadInt32(buffer, 0) != 1)
            {
                LogUnverified(Checks.SessionLock, 0);
                return null;
            }

            var reportedSessionId = Marshal.ReadInt32(buffer, 8);
            if (expectedSessionId is { } expected && reportedSessionId != expected)
            {
                Trace.TraceWarning(
                    $"interactive_desktop: WTSSessionInfoEx reported session {reportedSessionId}, expected {expected}; lock state unverified.");
                return null;
            }

            return Marshal.ReadInt32(buffer, 16) switch
            {
                WTS_SESSIONSTATE_LOCK => true,
                WTS_SESSIONSTATE_UNLOCK => false,
                _ => null
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool TryGetUserObjectName(IntPtr handle, out string name)
    {
        name = string.Empty;
        _ = GetUserObjectInformation(handle, UOI_NAME, IntPtr.Zero, 0, out var needed);
        if (needed == 0 || needed > 4096)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetUserObjectInformation(handle, UOI_NAME, buffer, needed, out _))
            {
                return false;
            }

            name = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return name.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetUserObjectFlags(IntPtr handle, out uint flags)
    {
        flags = 0;
        var size = Marshal.SizeOf<USEROBJECTFLAGS>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetUserObjectInformation(handle, UOI_FLAGS, buffer, (uint)size, out _))
            {
                return false;
            }

            flags = Marshal.PtrToStructure<USEROBJECTFLAGS>(buffer).dwFlags;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void LogUnverified(string check, int win32Error) =>
        Trace.TraceWarning(
            $"interactive_desktop: check '{check}' unverified" +
            (win32Error != 0 ? $" ({new Win32Exception(win32Error).Message})." : "."));

    private const int ERROR_ACCESS_DENIED = 5;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_FLAGS = 1;
    private const int UOI_NAME = 2;
    private const uint WSF_VISIBLE = 0x0001;
    private const uint SPI_GETSCREENSAVERRUNNING = 0x0072;
    private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    private const int WTS_CURRENT_SESSION = -1;
    private const int WTSConnectState = 8;
    private const int WTSSessionInfoEx = 25;
    private const int WTS_SESSIONSTATE_LOCK = 0;
    private const int WTS_SESSIONSTATE_UNLOCK = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct USEROBJECTFLAGS
    {
        public int fInherit;
        public int fReserved;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        IntPtr info,
        uint length,
        out uint lengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
