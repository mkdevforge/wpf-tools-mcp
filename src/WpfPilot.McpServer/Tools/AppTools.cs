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
        CancellationToken cancellationToken = default) =>
        automation.LaunchAsync(new LaunchAppRequest(exePath, args, workingDirectory), cancellationToken);

    [McpServerTool(Name = "attach_to_app"), Description("Attach to an already running process.")]
    public static Task<AttachToAppResponse> AttachToApp(
        AutomationController automation,
        [Description("Process ID")] int? pid = null,
        [Description("Process name")] string? processName = null,
        CancellationToken cancellationToken = default)
    {
        if (pid is not null && !string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Provide either pid or processName, not both.");
        }

        return automation.AttachAsync(new AttachToAppRequest(pid, processName), cancellationToken);
    }

    [McpServerTool(Name = "close_app"), Description("Close the attached application.")]
    public static Task<CloseAppResponse> CloseApp(
        AutomationController automation,
        [Description("Force kill if graceful close fails")] bool force = false,
        [Description("Wait timeout (ms) before forcing")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        automation.CloseAsync(new CloseAppRequest(force, timeoutMs), cancellationToken);

    [McpServerTool(Name = "list_windows"), Description("Enumerate all top-level windows of the attached process.")]
    public static Task<ListWindowsResponse> ListWindows(
        AutomationController automation,
        CancellationToken cancellationToken = default) =>
        automation.ListWindowsAsync(cancellationToken);

    [McpServerTool(Name = "take_screenshot"), Description("Capture a screenshot of the main window or a specified window handle.")]
    public static Task<TakeScreenshotResponse> TakeScreenshot(
        AutomationController automation,
        [Description("Native window handle")] long? windowHandle = null,
        [Description("Optional element locator for element-only screenshot")] ElementLocator? locator = null,
        [Description("Capture mode: screen | printWindow | auto")] string? captureMode = null,
        CancellationToken cancellationToken = default) =>
        automation.TakeScreenshotAsync(
            new TakeScreenshotRequest(windowHandle, locator, ParseCaptureMode(captureMode)),
            cancellationToken);

    private static ScreenshotCaptureMode ParseCaptureMode(string? captureMode)
    {
        if (string.IsNullOrWhiteSpace(captureMode))
        {
            return ScreenshotCaptureMode.Screen;
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
}
