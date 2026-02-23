using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;
using WpfPilot.McpServer.Subscriptions;

namespace WpfPilot.McpServer.Tools;

[McpServerToolType]
public static class AppTools
{
    [McpServerTool(Name = "launch_app"), Description("Start a WPF application.")]
    public static Task<LaunchAppResponse> LaunchApp(
        SessionManager sessions,
        [Description("Executable path")] string exePath,
        [Description("Optional arguments")] string[]? args = null,
        [Description("Optional working directory")] string? workingDirectory = null,
        [Description("How long to wait for the app main window before considering fallback logic (ms)")] int waitForMainWindowMs = 15000,
        [Description("If launch cannot resolve a main window, try attaching to an existing instance")] bool reuseExistingInstance = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.LaunchAppAsync(
                new LaunchAppRequest(
                    exePath,
                    args,
                    workingDirectory,
                    waitForMainWindowMs,
                    reuseExistingInstance),
                cancellationToken));

    [McpServerTool(Name = "attach_to_app"), Description("Attach to an already running process.")]
    public static Task<AttachToAppResponse> AttachToApp(
        SessionManager sessions,
        [Description("Process ID")] int? pid = null,
        [Description("Process name (supports dotted names and optional .exe suffix)")] string? processName = null,
        CancellationToken cancellationToken = default)
    {
        if (pid is not null && !string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Provide either pid or processName, not both.");
        }

        return McpToolErrors.RunAsync(() =>
            sessions.AttachToAppAsync(new AttachToAppRequest(pid, processName), cancellationToken));
    }

    [McpServerTool(Name = "close_session"), Description("Close and dispose a session (and close the attached application).")]
    public static Task<CloseAppResponse> CloseSession(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Force kill if graceful close fails")] bool force = false,
        [Description("Wait timeout (ms) before forcing")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            subscriptions.UnsubscribeAllForSession(sessionId);
            return sessions.CloseSessionAsync(sessionId, new CloseAppRequest(force, timeoutMs), cancellationToken);
        });

    [McpServerTool(Name = "list_sessions"), Description("List active sessions.")]
    public static Task<ListSessionsResponse> ListSessions(
        SessionManager sessions,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => Task.FromResult(sessions.ListSessions()));

    [McpServerTool(Name = "list_displays"), Description("List connected displays and the virtual screen bounds (multi-monitor diagnostics).")]
    public static Task<ListDisplaysResponse> ListDisplays(
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => Task.FromResult(DisplayDiagnostics.ListDisplays()));

    [McpServerTool(Name = "list_windows"), Description("Enumerate all top-level windows of the attached process.")]
    public static Task<ListWindowsResponse> ListWindows(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.ListWindowsAsync(cancellationToken), cancellationToken);
        });

    [McpServerTool(Name = "take_screenshot"), Description("Capture a screenshot of the main window or a specified window handle.")]
    public static Task<TakeScreenshotResponse> TakeScreenshot(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional element locator for element-only screenshot")] ElementLocator? locator = null,
        [Description("Optional elementId for element-only screenshot")] string? elementId = null,
        [Description("Inspection backend selection")] InspectionBackend backend = InspectionBackend.Auto,
        [Description("Capture mode: screen | printWindow | auto")] string? captureMode = null,
        [Description("Capture area: client | window")] string? area = null,
        [Description("When taking element screenshots, clip to area: none | intersect")] string? clip = null,
        [Description("Image format: png | jpeg")] string? format = null,
        [Description("JPEG quality 1-100 (only used when format=jpeg)")] int? jpegQuality = null,
        [Description("Optional output file path (auto-generated when omitted)")] string? outputPath = null,
        [Description("Include highlight overlays in the capture (defaults to false)")] bool includeOverlay = false,
        [Description("Scroll element into view before capturing (defaults to true)")] bool autoScroll = true,
        [Description("Include base64 payload in response (defaults to false)")] bool returnBase64 = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, effectiveWindowHandle) = sessions.GetController(sessionId, windowHandle);
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            return automation.RunExclusiveAsync(
                () => automation.TakeScreenshotAsync(
                    new TakeScreenshotRequest(
                        WindowHandle: hasElementId ? windowHandle : effectiveWindowHandle,
                        Locator: locator,
                        ElementId: elementId,
                        Backend: backend,
                        CaptureMode: ParseCaptureMode(captureMode),
                        Area: ParseCaptureArea(area),
                        Clip: ParseClipMode(clip),
                        Format: ParseImageFormat(format),
                        JpegQuality: jpegQuality ?? 90,
                        OutputPath: outputPath,
                        IncludeOverlay: includeOverlay,
                        AutoScroll: autoScroll,
                        ReturnBase64: returnBase64),
                    cancellationToken),
                cancellationToken);
        });

    private static ScreenshotCaptureMode ParseCaptureMode(string? captureMode)
    {
        if (string.IsNullOrWhiteSpace(captureMode))
        {
            return ScreenshotCaptureMode.Auto;
        }

        var value = captureMode.Trim();
        if (value.Equals("screen", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("bitblt", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("gdi", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCaptureMode.Screen;
        }

        if (value.Equals("printWindow", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("print_window", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("printwindow", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("pw", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCaptureMode.PrintWindow;
        }

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCaptureMode.Auto;
        }

        throw new ArgumentException($"Unknown captureMode '{captureMode}'. Valid values: screen, printWindow, auto.");
    }

    private static ScreenshotCaptureArea ParseCaptureArea(string? area)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            return ScreenshotCaptureArea.Client;
        }

        var value = area.Trim();
        if (value.Equals("client", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("content", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCaptureArea.Client;
        }

        if (value.Equals("window", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotCaptureArea.Window;
        }

        throw new ArgumentException($"Unknown area '{area}'. Valid values: client, window.");
    }

    private static ScreenshotClipMode ParseClipMode(string? clip)
    {
        if (string.IsNullOrWhiteSpace(clip))
        {
            return ScreenshotClipMode.Intersect;
        }

        var value = clip.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotClipMode.None;
        }

        if (value.Equals("intersect", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("clip", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotClipMode.Intersect;
        }

        throw new ArgumentException($"Unknown clip '{clip}'. Valid values: none, intersect.");
    }

    private static ScreenshotImageFormat ParseImageFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ScreenshotImageFormat.Png;
        }

        var value = format.Trim();
        if (value.Equals("png", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotImageFormat.Png;
        }

        if (value.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("jpg", StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotImageFormat.Jpeg;
        }

        throw new ArgumentException($"Unknown format '{format}'. Valid values: png, jpeg.");
    }
}
