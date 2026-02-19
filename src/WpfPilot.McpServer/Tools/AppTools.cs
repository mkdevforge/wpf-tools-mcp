using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfPilot.Automation;
using WpfPilot.Contracts;

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
        [Description("Session ID")] string sessionId,
        [Description("Force kill if graceful close fails")] bool force = false,
        [Description("Wait timeout (ms) before forcing")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.CloseSessionAsync(sessionId, new CloseAppRequest(force, timeoutMs), cancellationToken));

    [McpServerTool(Name = "list_sessions"), Description("List active sessions.")]
    public static Task<ListSessionsResponse> ListSessions(
        SessionManager sessions,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => Task.FromResult(sessions.ListSessions()));

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
        [Description("Capture mode: screen | printWindow | auto")] string? captureMode = null,
        [Description("Image format: png | jpeg")] string? format = null,
        [Description("JPEG quality 1-100 (only used when format=jpeg)")] int? jpegQuality = null,
        [Description("Optional output file path (auto-generated when omitted)")] string? outputPath = null,
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
                        CaptureMode: ParseCaptureMode(captureMode),
                        Format: ParseImageFormat(format),
                        JpegQuality: jpegQuality ?? 90,
                        OutputPath: outputPath,
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
