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
        AutomationController automation,
        [Description("Executable path")] string exePath,
        [Description("Optional arguments")] string[]? args = null,
        [Description("Optional working directory")] string? workingDirectory = null,
        [Description("How long to wait for the app main window before considering fallback logic (ms)")] int waitForMainWindowMs = 15000,
        [Description("If launch cannot resolve a main window, try attaching to an existing instance")] bool reuseExistingInstance = true,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(
                () => automation.LaunchAsync(
                    new LaunchAppRequest(
                        exePath,
                        args,
                        workingDirectory,
                        waitForMainWindowMs,
                        reuseExistingInstance),
                    cancellationToken),
                cancellationToken));

    [McpServerTool(Name = "attach_to_app"), Description("Attach to an already running process.")]
    public static Task<AttachToAppResponse> AttachToApp(
        AutomationController automation,
        [Description("Process ID")] int? pid = null,
        [Description("Process name (supports dotted names and optional .exe suffix)")] string? processName = null,
        CancellationToken cancellationToken = default)
    {
        if (pid is not null && !string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Provide either pid or processName, not both.");
        }

        return McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(
                () => automation.AttachAsync(new AttachToAppRequest(pid, processName), cancellationToken),
                cancellationToken));
    }

    [McpServerTool(Name = "close_app"), Description("Close the attached application.")]
    public static Task<CloseAppResponse> CloseApp(
        AutomationController automation,
        [Description("Force kill if graceful close fails")] bool force = false,
        [Description("Wait timeout (ms) before forcing")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(
                () => automation.CloseAsync(new CloseAppRequest(force, timeoutMs), cancellationToken),
                cancellationToken));

    [McpServerTool(Name = "reset_state"), Description("Reset controller state and clear any stale attachment.")]
    public static Task<ResetStateResponse> ResetState(
        AutomationController automation,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(
                () => automation.ResetStateAsync(cancellationToken),
                cancellationToken));

    [McpServerTool(Name = "list_windows"), Description("Enumerate all top-level windows of the attached process.")]
    public static Task<ListWindowsResponse> ListWindows(
        AutomationController automation,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(() => automation.ListWindowsAsync(cancellationToken), cancellationToken));

    [McpServerTool(Name = "take_screenshot"), Description("Capture a screenshot of the main window or a specified window handle.")]
    public static Task<TakeScreenshotResponse> TakeScreenshot(
        AutomationController automation,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional element locator for element-only screenshot")] ElementLocator? locator = null,
        [Description("Capture mode: screen | printWindow | auto")] string? captureMode = null,
        [Description("Image format: png | jpeg")] string? format = null,
        [Description("JPEG quality 1-100 (only used when format=jpeg)")] int? jpegQuality = null,
        [Description("Optional output file path (auto-generated when omitted)")] string? outputPath = null,
        [Description("Include base64 payload in response (defaults to false)")] bool returnBase64 = false,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            automation.RunExclusiveAsync(
                () => automation.TakeScreenshotAsync(
                    new TakeScreenshotRequest(
                        windowHandle,
                        locator,
                        ParseCaptureMode(captureMode),
                        ParseImageFormat(format),
                        jpegQuality ?? 90,
                        outputPath,
                        returnBase64),
                    cancellationToken),
                cancellationToken));

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
