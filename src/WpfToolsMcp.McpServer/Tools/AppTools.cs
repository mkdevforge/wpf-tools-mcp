using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Subscriptions;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class AppTools
{
    [McpServerTool(Name = "launch_app", UseStructuredContent = true, OutputSchemaType = typeof(LaunchAppResponse)), Description("Start a WPF application. Existing-instance fallback returns structured candidates when ambiguous.")]
    public static Task<CallToolResult> LaunchApp(
        SessionManager sessions,
        [Description("Executable path")] string exePath,
        [Description("Optional arguments")] string[]? args = null,
        [Description("Optional working directory")] string? workingDirectory = null,
        [Description("How long to wait for the app main window before considering fallback logic (ms)")] int waitForMainWindowMs = 15000,
        [Description("If launch cannot resolve a main window, try attaching to an existing instance")] bool reuseExistingInstance = true,
        [Description("Session interaction policy")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunLaunchAppAsync(() =>
            sessions.LaunchAppAsync(
                new LaunchAppRequest(
                    exePath,
                    args,
                    workingDirectory,
                    waitForMainWindowMs,
                    reuseExistingInstance,
                    interactionPolicy),
                cancellationToken));

    [McpServerTool(Name = "attach_to_app", UseStructuredContent = true, OutputSchemaType = typeof(AttachToAppResponse)), Description("Attach to one unambiguous process, or replace an exited session while preserving durable policy. Ambiguous names return structured candidates.")]
    public static Task<CallToolResult> AttachToApp(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Process ID")] int? pid = null,
        [Description("Process name (supports dotted names and optional .exe suffix)")] string? processName = null,
        [Description("Opaque process instance ID returned by an ambiguous_process candidate")] string? processInstanceId = null,
        [Description("Exited session to replace; omit all target selectors to reuse its process name")] string? sessionId = null,
        [Description("Session interaction policy")] InteractionPolicy? interactionPolicy = null,
        CancellationToken cancellationToken = default)
    {
        sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var selectors = (pid is not null ? 1 : 0) +
                        (!string.IsNullOrWhiteSpace(processName) ? 1 : 0) +
                        (!string.IsNullOrWhiteSpace(processInstanceId) ? 1 : 0);
        if (selectors > 1 || (string.IsNullOrWhiteSpace(sessionId) && selectors != 1))
        {
            throw new ArgumentException(
                "Provide exactly one of pid, processName, or processInstanceId; an exited sessionId may omit the target selector.");
        }

        return McpToolErrors.RunAttachToAppAsync(() =>
            sessions.AttachToAppAsync(
                new AttachToAppRequest(pid, processName, interactionPolicy, sessionId, processInstanceId),
                string.IsNullOrWhiteSpace(sessionId)
                    ? null
                    : () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));
    }

    [McpServerTool(Name = "detach_session", UseStructuredContent = true), Description("Remove an inspection session and release its resources without closing or terminating the target application.")]
    public static Task<DetachSessionResponse> DetachSession(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.DetachSessionAsync(
                sessionId,
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "close_app", UseStructuredContent = true), Description("Request a graceful application close, remove the inspection session, and report the close request and observed process outcome separately.")]
    public static Task<CloseAppResponse> CloseApp(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Wait timeout (ms) for process exit")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.CloseApplicationAsync(
                sessionId,
                timeoutMs,
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "terminate_app", UseStructuredContent = true), Description("Forcefully terminate the target application, remove the inspection session, and report the observed process outcome.")]
    public static Task<CloseAppResponse> TerminateApp(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Wait timeout (ms) for process exit")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.TerminateApplicationAsync(
                sessionId,
                timeoutMs,
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "close_session", UseStructuredContent = true), Description("Compatibility path that removes the session and closes the application, optionally force-terminating it. Prefer detach_session, close_app, or terminate_app for explicit intent.")]
    public static Task<CloseAppResponse> CloseSession(
        SessionManager sessions,
        SubscriptionManager subscriptions,
        [Description("Session ID")] string sessionId,
        [Description("Force kill if graceful close fails")] bool force = false,
        [Description("Wait timeout (ms) before forcing")] int timeoutMs = 5000,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
            sessions.CloseSessionAsync(
                sessionId,
                new CloseAppRequest(force, timeoutMs),
                () => subscriptions.UnsubscribeAllForSessionAsync(sessionId),
                cancellationToken));

    [McpServerTool(Name = "list_sessions", UseStructuredContent = true), Description("List active sessions; BackendCapabilities lists confirmed-ready backends, and BackendCapabilityStates reports ready/unavailable/not_initialized.")]
    public static Task<ListSessionsResponse> ListSessions(
        SessionManager sessions,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => sessions.ListSessionsAsync(cancellationToken));

    [McpServerTool(Name = "list_displays", UseStructuredContent = true), Description("List connected displays and the virtual screen bounds (multi-monitor diagnostics).")]
    public static Task<ListDisplaysResponse> ListDisplays(
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => Task.FromResult(DisplayDiagnostics.ListDisplays()));

    [McpServerTool(Name = "list_windows", UseStructuredContent = true), Description("Enumerate visible top-level windows of the attached process, including native dialogs and owner, modal, and framework context.")]
    public static Task<ListWindowsResponse> ListWindows(
        SessionManager sessions,
        [Description("Session ID")] string sessionId,
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() =>
        {
            var (automation, _) = sessions.GetController(sessionId);
            return automation.RunExclusiveAsync(() => automation.ListWindowsAsync(cancellationToken), cancellationToken);
        });

    public static Task<TakeScreenshotResponse> TakeScreenshot(
        SessionManager sessions,
        string sessionId,
        long? windowHandle = null,
        ElementLocator? locator = null,
        string? elementId = null,
        InspectionBackend backend = InspectionBackend.Auto,
        string? captureMode = null,
        string? area = null,
        string? clip = null,
        string? format = null,
        int? jpegQuality = null,
        string? outputPath = null,
        bool includeOverlay = false,
        bool autoScroll = true,
        bool fullyVisible = true,
        bool annotate = false,
        string annotationColor = "#3B82F6",
        int annotationThickness = 3,
        string? annotationLabel = null,
        bool returnBase64 = false,
        ScreenshotCorrelationOptions? correlation = null,
        CancellationToken cancellationToken = default) =>
        TakeScreenshotWithViewport(
            sessions,
            sessionId,
            windowHandle,
            locator,
            elementId,
            backend,
            captureMode,
            area,
            clip,
            format,
            jpegQuality,
            outputPath,
            includeOverlay,
            autoScroll,
            fullyVisible,
            annotate,
            annotationColor,
            annotationThickness,
            annotationLabel,
            returnBase64,
            includeViewport: false,
            correlation: correlation,
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "take_screenshot", UseStructuredContent = true), Description("Capture a screenshot of the main window or a specified window handle.")]
    public static Task<TakeScreenshotResponse> TakeScreenshotWithViewport(
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
        [Description("Require the full element bounds to be visible after auto-scroll (defaults to true)")] bool fullyVisible = true,
        [Description("Annotate the resolved element bounds onto the screenshot (defaults to false)")] bool annotate = false,
        [Description("Annotation stroke color (e.g. #3B82F6)")] string annotationColor = "#3B82F6",
        [Description("Annotation stroke thickness (px)")] int annotationThickness = 3,
        [Description("Optional annotation label")] string? annotationLabel = null,
        [Description("Include base64 payload in response (defaults to false)")] bool returnBase64 = false,
        [Description("Include the window's client, outer, DPI, monitor, and state conditions in the response")] bool includeViewport = false,
        [Description("Optionally correlate a capture-local point or region with bounded WPF/UIA candidates and annotations")] ScreenshotCorrelationOptions? correlation = null,
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
                        FullyVisible: fullyVisible,
                        Annotate: annotate,
                        AnnotationColor: annotationColor,
                        AnnotationThickness: annotationThickness,
                        AnnotationLabel: annotationLabel,
                        ReturnBase64: returnBase64)
                    {
                        IncludeViewport = includeViewport,
                        Correlation = correlation
                    },
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
