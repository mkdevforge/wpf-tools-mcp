using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController : IDisposable
{
    private Application? _application;
    private UIA3Automation? _automation;
    private readonly SemaphoreSlim _toolMutex = new(1, 1);
    private LastHighlightRequest? _lastHighlight;

    private static readonly int UiDelayMs = GetEnvInt("WPF_TOOLS_MCP_UI_DELAY_MS", defaultValue: 0, minValue: 0, maxValue: 1000);
    private static readonly int UiDelayScrollMs = GetEnvInt("WPF_TOOLS_MCP_UI_SCROLL_DELAY_MS", defaultValue: 15, minValue: 0, maxValue: 1000);
    private static readonly int UiDelayWindowSettleMs = GetEnvInt("WPF_TOOLS_MCP_UI_WINDOW_SETTLE_MS", defaultValue: 25, minValue: 0, maxValue: 5000);
    private static readonly bool ScreenshotDebugEnabled = GetEnvFlag("WPF_TOOLS_MCP_DEBUG_SCREENSHOT");

    private sealed record LastHighlightRequest(Rect Bounds, string Color, int Thickness, DateTime ExpiresAtUtc);

    public bool IsAttached => IsApplicationRunning(_application);

    public void Dispose()
    {
        Cleanup();
        _toolMutex.Dispose();
    }

    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _toolMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _toolMutex.Release();
        }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _toolMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _toolMutex.Release();
        }
    }

    private static int GetEnvInt(string name, int defaultValue, int minValue, int maxValue)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return defaultValue;
            }

            return Math.Clamp(value, minValue, maxValue);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool GetEnvFlag(string name)
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (bool.TryParse(raw, out var parsed))
            {
                return parsed;
            }

            return raw.Trim() switch
            {
                "1" => true,
                "yes" => true,
                "on" => true,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    public Task<LaunchAppResponse> LaunchAsync(LaunchAppRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExePath);
        EnsureNotAttached();

        if (Path.IsPathRooted(request.ExePath) && !File.Exists(request.ExePath))
        {
            throw new FileNotFoundException($"Executable not found: '{request.ExePath}'.", request.ExePath);
        }

        var waitMs = Math.Clamp(request.WaitForMainWindowMs, 1000, 120000);
        var waitTimeout = TimeSpan.FromMilliseconds(waitMs);
        Exception? lastLaunchError = null;

        foreach (var launchStrategy in CreateLaunchStartInfos(request))
        {
            try
            {
                _application = Application.Launch(launchStrategy.StartInfo);
                if (TryInitializeApplication(_application, waitTimeout, out var launchInitError))
                {
                    var launchResponse = new LaunchAppResponse(SessionId: "", _application.ProcessId, _application.Name);
                    return Task.FromResult(launchResponse);
                }

                if (!request.ReuseExistingInstance)
                {
                    throw launchInitError ?? new InvalidOperationException(
                        $"Failed to initialize launched application (strategy: {launchStrategy.Name}).");
                }

                _application.Dispose();
                _application = null;

                if (TryAttachToExistingInstance(request.ExePath, waitTimeout, out var attachError))
                {
                    var attachResponse = new LaunchAppResponse(SessionId: "", _application!.ProcessId, _application.Name);
                    return Task.FromResult(attachResponse);
                }

                throw new InvalidOperationException(
                    $"Launch strategy '{launchStrategy.Name}' failed to resolve a main window and fallback attach to an existing instance was unsuccessful.",
                    attachError ?? launchInitError);
            }
            catch (Exception ex)
            {
                lastLaunchError = ex;
                Cleanup();
            }
        }

        throw new InvalidOperationException(
            "Launch failed for all launch strategies (shellExecute and directProcess).",
            lastLaunchError);
    }

    private static IReadOnlyList<(ProcessStartInfo StartInfo, string Name)> CreateLaunchStartInfos(LaunchAppRequest request)
    {
        var shellStartInfo = CreateLaunchStartInfo(request, useShellExecute: true);
        var directStartInfo = CreateLaunchStartInfo(request, useShellExecute: false);

        directStartInfo.RedirectStandardOutput = true;
        directStartInfo.RedirectStandardError = true;
        directStartInfo.RedirectStandardInput = true;
        ApplyWindowsGuiEnvironmentDefaults(directStartInfo);

        return
        [
            (shellStartInfo, "shellExecute"),
            (directStartInfo, "directProcess")
        ];
    }

    private static ProcessStartInfo CreateLaunchStartInfo(LaunchAppRequest request, bool useShellExecute)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExePath,
            UseShellExecute = useShellExecute,
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }
        else if (Path.IsPathRooted(request.ExePath))
        {
            var exeDirectory = Path.GetDirectoryName(request.ExePath);
            if (!string.IsNullOrWhiteSpace(exeDirectory))
            {
                startInfo.WorkingDirectory = exeDirectory;
            }
        }

        if (request.Args is not null)
        {
            foreach (var arg in request.Args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        return startInfo;
    }

    private static void ApplyWindowsGuiEnvironmentDefaults(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowsDirectory = GetEnvironmentVariableFromAnyScope("WINDIR") ??
                               GetEnvironmentVariableFromAnyScope("SystemRoot");

        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            try
            {
                windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }
            catch
            {
                windowsDirectory = null;
            }
        }

        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return;
        }

        if (!startInfo.Environment.TryGetValue("WINDIR", out var windirValue) || string.IsNullOrWhiteSpace(windirValue))
        {
            startInfo.Environment["WINDIR"] = windowsDirectory;
        }

        if (!startInfo.Environment.TryGetValue("SystemRoot", out var systemRootValue) || string.IsNullOrWhiteSpace(systemRootValue))
        {
            startInfo.Environment["SystemRoot"] = windowsDirectory;
        }

        var tempDirectory = Path.GetTempPath();
        if (!startInfo.Environment.TryGetValue("TEMP", out var tempValue) || string.IsNullOrWhiteSpace(tempValue))
        {
            startInfo.Environment["TEMP"] = tempDirectory;
        }

        if (!startInfo.Environment.TryGetValue("TMP", out var tmpValue) || string.IsNullOrWhiteSpace(tmpValue))
        {
            startInfo.Environment["TMP"] = tempDirectory;
        }
    }

    private static string? GetEnvironmentVariableFromAnyScope(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name) ??
                   Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ??
                   Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }
        catch
        {
            return null;
        }
    }

    private bool TryInitializeApplication(Application application, TimeSpan waitTimeout, out Exception? error)
    {
        error = null;
        try
        {
            application.WaitWhileMainHandleIsMissing(waitTimeout);
            application.WaitWhileBusy(waitTimeout);
            _automation?.Dispose();
            _automation = new UIA3Automation();
            _ = FindMainWindow(application, _automation, waitTimeout);
            return true;
        }
        catch (Exception ex)
        {
            _automation?.Dispose();
            _automation = null;
            error = ex;
            return false;
        }
    }

    private bool TryAttachToExistingInstance(string exePath, TimeSpan waitTimeout, out Exception? error)
    {
        error = null;
        var processName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var perAttemptTimeoutMs = Math.Clamp((int)waitTimeout.TotalMilliseconds / 4, 500, 3000);
        var perAttemptTimeout = TimeSpan.FromMilliseconds(perAttemptTimeoutMs);
        var deadline = DateTime.UtcNow + waitTimeout;

        while (DateTime.UtcNow <= deadline)
        {
            var candidates = Process.GetProcessesByName(processName)
                .OrderByDescending(p => p.Id)
                .ToArray();

            foreach (var process in candidates)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    Application? attached = null;
                    try
                    {
                        attached = Application.Attach(process.Id);
                        if (!TryInitializeApplication(attached, perAttemptTimeout, out var initError))
                        {
                            error = initError;
                            attached.Dispose();
                            continue;
                        }

                        _application = attached;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                        attached?.Dispose();
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            Thread.Sleep(200);
        }

        return false;
    }

    public Task<AttachToAppResponse> AttachAsync(AttachToAppRequest request, CancellationToken cancellationToken = default)
    {
        EnsureNotAttached();

        if (request.Pid is not null && !string.IsNullOrWhiteSpace(request.ProcessName))
        {
            throw new ArgumentException("Provide either pid or processName, not both.");
        }

        try
        {
            if (request.Pid is int pid)
            {
                _application = Application.Attach(pid);
            }
            else if (!string.IsNullOrWhiteSpace(request.ProcessName))
            {
                var resolvedPid = ResolveProcessIdByName(request.ProcessName);
                _application = Application.Attach(resolvedPid);
            }
            else
            {
                throw new ArgumentException("Either pid or processName must be provided.");
            }

            _application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(10));
            _application.WaitWhileBusy(TimeSpan.FromSeconds(10));
            _automation = new UIA3Automation();
            _ = FindMainWindow(_application, _automation);

            var response = new AttachToAppResponse(SessionId: "", _application.ProcessId, _application.Name);
            return Task.FromResult(response);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    private static int ResolveProcessIdByName(string processName)
    {
        var candidateNames = BuildProcessNameCandidates(processName);
        var candidates = new List<(int Pid, DateTime StartTimeUtc)>();
        var seenPids = new HashSet<int>();

        foreach (var candidateName in candidateNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(candidateName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited || !seenPids.Add(process.Id))
                    {
                        continue;
                    }

                    candidates.Add((process.Id, SafeGetStartTimeUtc(process)));
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Unable to find process with name: {processName}. Tried: {string.Join(", ", candidateNames)}");
        }

        return candidates
            .OrderByDescending(c => c.StartTimeUtc)
            .ThenByDescending(c => c.Pid)
            .First()
            .Pid;
    }

    private static IReadOnlyList<string> BuildProcessNameCandidates(string processName)
    {
        var trimmed = processName.Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<string>();
        }

        var fileName = Path.GetFileName(trimmed);
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidateName(fileName);
        AddCandidateName(withoutExtension);

        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            AddCandidateName(Path.GetFileNameWithoutExtension($"{fileName}.exe"));
        }

        return names.ToArray();

        void AddCandidateName(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add(value.Trim());
            }
        }
    }

    private static DateTime SafeGetStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public Task<CloseAppResponse> CloseAsync(CloseAppRequest request, CancellationToken cancellationToken = default)
    {
        if (_application is null)
        {
            throw new InvalidOperationException("No application is attached. Call launch_app or attach_to_app first.");
        }

        var trace = BeginTraceSpan("close_session");
        try
        {
        var timeout = request.TimeoutMs <= 0 ? 5000 : request.TimeoutMs;
        var application = _application;

        if (!IsApplicationRunning(application))
        {
            Cleanup();
            var response = new CloseAppResponse(Closed: true);
            trace?.SetSummary($"closed={response.Closed}");
            return Task.FromResult(response);
        }

        var closedGracefully = false;
        try
        {
            application.CloseTimeout = TimeSpan.FromMilliseconds(timeout);
            closedGracefully = application.Close(killIfCloseFails: request.Force);
        }
        catch (InvalidOperationException)
        {
            Cleanup();
            return Task.FromResult(new CloseAppResponse(Closed: true));
        }

        if (!closedGracefully && request.Force)
        {
            try
            {
                application.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }

        var closed = !IsApplicationRunning(application);
        Cleanup();
        var result = new CloseAppResponse(closed);
        trace?.SetSummary($"closed={result.Closed} graceful={closedGracefully} force={request.Force}");
        return Task.FromResult(result);
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
    }

    public Task<ListWindowsResponse> ListWindowsAsync(CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("list_windows");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var application = EnsureAttached();
            var automation = EnsureAutomation();

            application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(10));

            var windows = GetAllTopLevelWindows(application, automation)
                .Select(ToWindowInfo)
                .ToArray();

            var response = new ListWindowsResponse(application.ProcessId, application.Name, windows);
            trace?.SetSummary($"windows={windows.Length}");
            return Task.FromResult(response);
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
    }

    public async Task<TakeScreenshotResponse> TakeScreenshotAsync(
        TakeScreenshotRequest request,
        CancellationToken cancellationToken = default,
        bool autoInject = false)
    {
        var trace = BeginTraceSpan("take_screenshot");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            if (request.Locator is not null && !string.IsNullOrWhiteSpace(request.ElementId))
            {
                throw new ArgumentException("Provide either locator or elementId, not both.");
            }

            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            var requestedBackend = request.Backend;
            ElementHandle? elementHandle = null;
            var elementBackend = requestedBackend == InspectionBackend.Auto ? InspectionBackend.Uia : requestedBackend;

            Window window;
            if (hasElementId)
            {
                var elementId = request.ElementId!.Trim();
                elementHandle = RequireHandle(elementId);
                elementBackend = elementHandle.Backend;

                if (requestedBackend != InspectionBackend.Auto && requestedBackend != elementBackend)
                {
                    throw new ArgumentException("backend does not match the elementId backend.");
                }

                if (request.WindowHandle is long requestedHandle && requestedHandle != elementHandle.WindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the elementId window.");
                }

                try
                {
                    window = FindWindowByHandle(application, automation, elementHandle.WindowHandle);
                }
                catch
                {
                    throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
                }
            }
            else
            {
                window = request.WindowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);
            }

            if (requestedBackend == InspectionBackend.Auto &&
                autoInject &&
                !hasElementId &&
                request.Locator is not null)
            {
                var autoClient = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
                if (autoClient is not null)
                {
                    elementBackend = InspectionBackend.Wpf;
                }
            }

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

            var mode = request.CaptureMode;
            var area = request.Area;
            var clip = request.Clip;
            var windowHandleUsed = window.Properties.NativeWindowHandle.Value.ToInt64();
            var includeOverlay = request.IncludeOverlay;
            var autoScroll = request.AutoScroll;
            var fullyVisible = request.FullyVisible;

            AutomationElement element = window;
            Rect? wpfElementBounds = null;
            var hasElementTarget = request.Locator is not null || hasElementId;
            var backendUsed = elementBackend;
            var fallbackUsed = false;

            (Bitmap Bitmap, Rect CapturedBounds, Rect? RequestedBounds, bool WasClipped, ScreenshotCaptureMode CaptureModeUsed)? capture = null;
            var recovered = false;

            try
            {
                if (hasElementId)
                {
                    var elementId = request.ElementId!.Trim();
                    if (elementBackend == InspectionBackend.Uia)
                    {
                        element = ResolveUiaElementById(window, rawWalker, elementId, out _);

                        if (autoScroll)
                        {
                            try
                            {
                                TryScrollIntoView(element);
                            }
                            catch
                            {
                            }

                            if (UiDelayScrollMs > 0)
                            {
                                await Task.Delay(UiDelayScrollMs, cancellationToken);
                            }
                        }
                    }
                    else if (elementBackend == InspectionBackend.Wpf)
                    {
                        var handle = elementHandle ?? RequireHandle(elementId);
                        wpfElementBounds = await ResolveWpfBoundsForHandleAsync(
                            window,
                            handle,
                            autoScroll: autoScroll,
                            cancellationToken,
                            fullyVisible: fullyVisible,
                            throwIfScrollFailed: autoScroll).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(elementBackend), elementBackend, "Unsupported backend.");
                    }
                }
                else if (request.Locator is not null)
                {
                    if (elementBackend == InspectionBackend.Wpf)
                    {
                        var resolved = await ResolveWpfElementRefAsync(
                            request.Locator,
                            windowHandleUsed,
                            visibleOnly: true,
                            includeOffViewport: autoScroll,
                            interactiveOnly: false,
                            interactiveMode: InteractiveMode.Heuristic,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        wpfElementBounds = resolved.Bounds;

                        if (autoScroll && wpfElementBounds is { } wpfBounds)
                        {
                            if (TryGetClientBoundsScreen(window, out var clientBounds) &&
                                !IsRectVisibleEnough(wpfBounds, clientBounds, fullyVisible))
                            {
                                var bring = await BringIntoViewWpfAsync(windowHandleUsed, resolved.XPath, cancellationToken).ConfigureAwait(false);
                                if (bring.BroughtIntoView)
                                {
                                    if (UiDelayScrollMs > 0)
                                    {
                                        await Task.Delay(UiDelayScrollMs, cancellationToken);
                                    }

                                    var after = await ResolveWpfElementRefAsync(
                                        request.Locator,
                                        windowHandleUsed,
                                        visibleOnly: true,
                                        includeOffViewport: true,
                                        interactiveOnly: false,
                                        interactiveMode: InteractiveMode.Heuristic,
                                        cancellationToken: cancellationToken).ConfigureAwait(false);

                                    wpfElementBounds = after.Bounds;
                                }
                            }
                        }
                    }
                    else if (elementBackend == InspectionBackend.Uia)
                    {
                        element = ResolveElement(window, request.Locator, controlWalker, rawWalker);

                        if (autoScroll)
                        {
                            try
                            {
                                TryScrollIntoView(element);
                            }
                            catch
                            {
                            }

                            if (UiDelayScrollMs > 0)
                            {
                                await Task.Delay(UiDelayScrollMs, cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(elementBackend), elementBackend, "Unsupported backend.");
                    }
                }

                Rect? requestedBounds = null;
                if (hasElementTarget)
                {
                    if (elementBackend == InspectionBackend.Wpf)
                    {
                        requestedBounds = wpfElementBounds;
                    }
                    else
                    {
                        requestedBounds = ToRect(element.BoundingRectangle);
                    }
                }

                if (autoScroll && hasElementTarget && requestedBounds is { } beforeBounds)
                {
                    var containerBounds =
                        area == ScreenshotCaptureArea.Client && TryGetClientBoundsScreen(window, out var clientBounds)
                            ? clientBounds
                            : area == ScreenshotCaptureArea.Window && TryGetWindowBoundsScreen(window, out var windowBounds)
                                ? windowBounds
                                : ToRect(window.BoundingRectangle);

                    if (!IsRectVisibleEnough(beforeBounds, containerBounds, fullyVisible))
                    {
                        if (elementBackend == InspectionBackend.Uia)
                        {
                            try
                            {
                                if (hasElementId)
                                {
                                    await ScrollToElementAsync(
                                        new ScrollToElementRequest(
                                            WindowHandle: windowHandleUsed,
                                            ElementId: request.ElementId!.Trim(),
                                            AutoWait: false),
                                        cancellationToken).ConfigureAwait(false);
                                }
                                else if (request.Locator is not null)
                                {
                                    await ScrollToElementAsync(
                                        new ScrollToElementRequest(
                                            WindowHandle: windowHandleUsed,
                                            Locator: request.Locator,
                                            AutoWait: false),
                                        cancellationToken).ConfigureAwait(false);
                                }
                            }
                            catch
                            {
                            }

                            if (UiDelayScrollMs > 0)
                            {
                                await Task.Delay(UiDelayScrollMs, cancellationToken);
                            }

                            if (!hasElementId && request.Locator is not null)
                            {
                                try
                                {
                                    element = ResolveElement(window, request.Locator, controlWalker, rawWalker);
                                }
                                catch
                                {
                                }
                            }

                            if (hasElementTarget)
                            {
                                requestedBounds = ToRect(element.BoundingRectangle);
                            }
                            if (requestedBounds is { } afterBounds &&
                                !IsRectVisibleEnough(afterBounds, containerBounds, fullyVisible))
                            {
                                throw new InvalidOperationException(
                                    $"element_offscreen_after_scroll: bounds={FormatRect(afterBounds)} container={FormatRect(containerBounds)}.");
                            }
                        }
                        else if (elementBackend == InspectionBackend.Wpf)
                        {
                            if (hasElementId && elementHandle is not null)
                            {
                                requestedBounds = await ResolveWpfBoundsForHandleAsync(
                                    window,
                                    elementHandle,
                                    autoScroll: true,
                                    cancellationToken,
                                    fullyVisible: fullyVisible,
                                    throwIfScrollFailed: true).ConfigureAwait(false);
                            }
                            else if (request.Locator is not null)
                            {
                                var resolved = await ResolveWpfElementRefAsync(
                                    request.Locator,
                                    windowHandleUsed,
                                    visibleOnly: true,
                                    includeOffViewport: true,
                                    interactiveOnly: false,
                                    interactiveMode: InteractiveMode.Heuristic,
                                    cancellationToken: cancellationToken).ConfigureAwait(false);

                                if (!string.IsNullOrWhiteSpace(resolved.ElementIdWpf))
                                {
                                    _ = await BringIntoViewWpfAsync(
                                        new ElementHandle(
                                            InspectionBackend.Wpf,
                                            windowHandleUsed,
                                            resolved.XPath,
                                            resolved.ElementIdWpf,
                                            null,
                                            resolved.Type,
                                            resolved.AutomationId,
                                            resolved.Name,
                                            resolved.ClassName),
                                        cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    _ = await BringIntoViewWpfAsync(windowHandleUsed, resolved.XPath, cancellationToken).ConfigureAwait(false);
                                }

                                if (UiDelayScrollMs > 0)
                                {
                                    await Task.Delay(UiDelayScrollMs, cancellationToken);
                                }

                                var after = await ResolveWpfElementRefAsync(
                                    request.Locator,
                                    windowHandleUsed,
                                    visibleOnly: true,
                                    includeOffViewport: true,
                                    interactiveOnly: false,
                                    interactiveMode: InteractiveMode.Heuristic,
                                    cancellationToken: cancellationToken).ConfigureAwait(false);

                                requestedBounds = after.Bounds;
                                if (requestedBounds is { } afterBounds &&
                                    !IsRectVisibleEnough(afterBounds, containerBounds, fullyVisible))
                                {
                                    throw new InvalidOperationException(
                                        $"element_offscreen_after_scroll: bounds={FormatRect(afterBounds)} container={FormatRect(containerBounds)}.");
                                }
                            }
                        }
                    }
                }

                try
                {
                    capture = CaptureScreenshotWithMetadata(window, requestedBounds, mode, area, clip, includeOverlay: false);
                }
                catch (InvalidOperationException ex) when (autoScroll &&
                                                          hasElementTarget &&
                                                          ex.Message.Contains("outside the capture area", StringComparison.OrdinalIgnoreCase))
                {
                    // Best-effort retry: try scrolling again (more robust than ScrollItem-only) and re-read bounds.
                    if (elementBackend == InspectionBackend.Uia)
                    {
                        try
                        {
                            if (hasElementId)
                            {
                                await ScrollToElementAsync(
                                    new ScrollToElementRequest(
                                        WindowHandle: windowHandleUsed,
                                        ElementId: request.ElementId!.Trim(),
                                        AutoWait: false),
                                    cancellationToken).ConfigureAwait(false);
                            }
                            else if (request.Locator is not null)
                            {
                                await ScrollToElementAsync(
                                    new ScrollToElementRequest(
                                        WindowHandle: windowHandleUsed,
                                        Locator: request.Locator,
                                        AutoWait: false),
                                    cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                        }

                        if (UiDelayScrollMs > 0)
                        {
                            await Task.Delay(UiDelayScrollMs, cancellationToken);
                        }

                        if (hasElementTarget)
                        {
                            requestedBounds = ToRect(element.BoundingRectangle);
                        }
                    }

                    capture = CaptureScreenshotWithMetadata(window, requestedBounds, mode, area, clip, includeOverlay: false);
                }
            }
            catch (Exception ex)
            {
                if (requestedBackend == InspectionBackend.Auto &&
                    autoInject &&
                    !hasElementId &&
                    request.Locator is not null &&
                    elementBackend == InspectionBackend.Wpf &&
                    IsEligibleAutoScreenshotFallback(ex))
                {
                    var fallbackResponse = await TakeScreenshotAsync(
                        request with { Backend = InspectionBackend.Uia },
                        cancellationToken,
                        autoInject: false).ConfigureAwait(false);

                    trace?.SetSummary($"{fallbackResponse.Format} {fallbackResponse.Width}x{fallbackResponse.Height} {Path.GetFileName(fallbackResponse.Path)} backend={InspectionBackend.Uia} fallback=true");
                    return fallbackResponse;
                }

                if (requestedBackend == InspectionBackend.Auto &&
                    !hasElementId &&
                    request.Locator is not null &&
                    elementBackend == InspectionBackend.Uia &&
                    IsAgentConnected &&
                    IsEligibleAutoScreenshotFallback(ex))
                {
                    try
                    {
                        var resolved = await ResolveWpfElementRefAsync(
                            request.Locator,
                            windowHandleUsed,
                            visibleOnly: true,
                            includeOffViewport: autoScroll,
                            interactiveOnly: false,
                            interactiveMode: InteractiveMode.Heuristic,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        wpfElementBounds = resolved.Bounds;
                        backendUsed = InspectionBackend.Wpf;
                        fallbackUsed = true;

                        if (autoScroll && wpfElementBounds is { } fallbackBounds)
                        {
                            if (TryGetClientBoundsScreen(window, out var clientBounds) &&
                                !IsRectVisibleEnough(fallbackBounds, clientBounds, fullyVisible))
                            {
                                var bring = await BringIntoViewWpfAsync(windowHandleUsed, resolved.XPath, cancellationToken).ConfigureAwait(false);
                                if (bring.BroughtIntoView)
                                {
                                    if (UiDelayScrollMs > 0)
                                    {
                                        await Task.Delay(UiDelayScrollMs, cancellationToken);
                                    }

                                    var after = await ResolveWpfElementRefAsync(
                                        request.Locator,
                                        windowHandleUsed,
                                        visibleOnly: true,
                                        includeOffViewport: false,
                                        interactiveOnly: false,
                                        interactiveMode: InteractiveMode.Heuristic,
                                        cancellationToken: cancellationToken).ConfigureAwait(false);

                                    wpfElementBounds = after.Bounds;
                                }
                            }
                        }

                        capture = CaptureScreenshotWithMetadata(window, wpfElementBounds, mode, area, clip, includeOverlay: false);
                        recovered = true;
                    }
                    catch (Exception fallbackEx)
                    {
                        throw new InvalidOperationException(
                            "take_screenshot failed using UIA and WPF fallback. " +
                            $"UIA error: {ex.GetBaseException().Message}. " +
                            $"WPF error: {fallbackEx.GetBaseException().Message}",
                            fallbackEx);
                    }
                }

                if (!recovered)
                {
                    throw;
                }
            }

            if (capture is null)
            {
                throw new InvalidOperationException("Failed to capture screenshot.");
            }

            var (bitmap, capturedBounds, requestedBoundsUsed, wasClipped, captureModeUsed) = capture.Value;

            using var bitmapToSave = bitmap;

            if (includeOverlay)
            {
                DrawActiveHighlightOverlay(bitmapToSave, capturedBounds);
            }

            if (request.Annotate && requestedBoundsUsed is { } annotationBounds)
            {
                try
                {
                    AnnotateBitmap(
                        bitmapToSave,
                        capturedBounds,
                        annotationBounds,
                        request.AnnotationColor,
                        request.AnnotationThickness,
                        request.AnnotationLabel);
                }
                catch
                {
                    // Ignore annotation failures; screenshot capture itself succeeded.
                }
            }

            var outputPath = ResolveScreenshotOutputPath(request.OutputPath, request.Format);
            SaveBitmapWithWic(bitmapToSave, outputPath, request.Format, request.JpegQuality);

            string? base64 = null;
            if (request.ReturnBase64)
            {
                var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
                base64 = Convert.ToBase64String(bytes);
            }

            var response = new TakeScreenshotResponse(
                Path: outputPath,
                Width: bitmapToSave.Width,
                Height: bitmapToSave.Height,
                Format: GetImageFormatName(request.Format),
                CapturedBounds: capturedBounds,
                RequestedBounds: requestedBoundsUsed,
                WasClipped: wasClipped,
                WindowHandleUsed: windowHandleUsed,
                CaptureModeUsed: captureModeUsed,
                Base64: base64);

            trace?.SetSummary($"{response.Format} {response.Width}x{response.Height} {Path.GetFileName(response.Path)} backend={backendUsed} fallback={fallbackUsed}");
            return response;
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
    }

    private static bool IsEligibleAutoScreenshotFallback(Exception ex)
    {
        ex = ex.GetBaseException();

        if (ex is ArgumentException)
        {
            return false;
        }

        if (ex is not InvalidOperationException)
        {
            return false;
        }

        var message = ex.Message ?? "";
        if (message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Contains("Locator did not match any element", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Element is outside the window client area", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Failed to compute crop rectangle", StringComparison.OrdinalIgnoreCase)
               || message.Contains("PrintWindow capture failed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Failed to crop element", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Requested bounds are outside the capture area", StringComparison.OrdinalIgnoreCase)
               || message.Contains("wpf_resolve:", StringComparison.OrdinalIgnoreCase);
    }

    private static void AnnotateBitmap(
        Bitmap bitmap,
        Rect capturedBounds,
        Rect annotationBounds,
        string color,
        int thickness,
        string? label)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return;
        }

        thickness = Math.Clamp(thickness, 1, 20);

        if (!TryParseColor(color, out var parsed))
        {
            parsed = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
        }

        static Rect Intersect(Rect a, Rect b)
        {
            var left = Math.Max(a.X, b.X);
            var top = Math.Max(a.Y, b.Y);
            var right = Math.Min(a.X + a.Width, b.X + b.Width);
            var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        var visible = Intersect(annotationBounds, capturedBounds);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            return;
        }

        var x = visible.X - capturedBounds.X;
        var y = visible.Y - capturedBounds.Y;
        var w = visible.Width;
        var h = visible.Height;

        // Clamp to bitmap bounds defensively.
        if (x >= bitmap.Width || y >= bitmap.Height)
        {
            return;
        }

        w = Math.Min(w, bitmap.Width - x);
        h = Math.Min(h, bitmap.Height - y);

        if (w <= 0 || h <= 0)
        {
            return;
        }

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(parsed, thickness)
        {
            Alignment = PenAlignment.Inset
        };

        // DrawRectangle uses inclusive coordinates; subtract 1 to stay inside the bitmap.
        var drawW = Math.Max(1, w - 1);
        var drawH = Math.Max(1, h - 1);
        graphics.DrawRectangle(pen, x, y, drawW, drawH);

        if (!string.IsNullOrWhiteSpace(label))
        {
            using var font = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
            var text = label.Trim();
            var textSize = graphics.MeasureString(text, font);

            var pad = 4;
            var boxW = (int)Math.Ceiling(textSize.Width) + pad * 2;
            var boxH = (int)Math.Ceiling(textSize.Height) + pad * 2;

            var boxX = Math.Clamp(x, 0, Math.Max(0, bitmap.Width - boxW));
            var boxY = Math.Clamp(y - boxH - 2, 0, Math.Max(0, bitmap.Height - boxH));

            using var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            using var fg = new SolidBrush(Color.White);

            graphics.FillRectangle(bg, boxX, boxY, boxW, boxH);
            graphics.DrawString(text, font, fg, boxX + pad, boxY + pad);
        }
    }

    private static bool TryParseColor(string value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#'))
        {
            try
            {
                color = ColorTranslator.FromHtml(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        var hex = trimmed.AsSpan(1);
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = Color.FromArgb(
                0xFF,
                (rgb >> 16) & 0xFF,
                (rgb >> 8) & 0xFF,
                rgb & 0xFF);
            return true;
        }

        if (hex.Length == 8 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            color = Color.FromArgb(
                (argb >> 24) & 0xFF,
                (argb >> 16) & 0xFF,
                (argb >> 8) & 0xFF,
                argb & 0xFF);
            return true;
        }

        return false;
    }

    private static (Bitmap Bitmap, Rect CapturedBounds, Rect? RequestedBounds, bool WasClipped, ScreenshotCaptureMode CaptureModeUsed)
        CaptureScreenshotWithMetadata(
            Window window,
            Rect? requestedBounds,
            ScreenshotCaptureMode requestedMode,
            ScreenshotCaptureArea area,
            ScreenshotClipMode clip,
            bool includeOverlay)
    {
        static Rect Intersect(Rect a, Rect b)
        {
            var left = Math.Max(a.X, b.X);
            var top = Math.Max(a.Y, b.Y);
            var right = Math.Min(a.X + a.Width, b.X + b.Width);
            var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        static Rect ClampToVirtualScreen(Rect bounds, Rect virtualScreen, ref bool clipped)
        {
            if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
            {
                return bounds;
            }

            var clamped = Intersect(bounds, virtualScreen);
            clipped |= clamped != bounds;
            return clamped;
        }

        static bool IsEmpty(Rect bounds) => bounds.Width <= 0 || bounds.Height <= 0;

        var containerBounds =
            area == ScreenshotCaptureArea.Client && TryGetClientBoundsScreen(window, out var clientBounds)
                ? clientBounds
                : area == ScreenshotCaptureArea.Window && TryGetWindowBoundsScreen(window, out var windowBounds)
                    ? windowBounds
                    : ToRect(window.BoundingRectangle);

        if (IsEmpty(containerBounds))
        {
            throw new InvalidOperationException("Window has no bounds.");
        }

        Rect screenBoundsToCapture;
        var screenWasClipped = false;

        Rect printWindowBoundsToCapture;
        var printWindowWasClipped = false;

        var virtualScreen = DisplayDiagnostics.GetVirtualScreenBounds();

        if (requestedBounds is null)
        {
            screenBoundsToCapture = containerBounds;
            printWindowBoundsToCapture = containerBounds;
        }
        else
        {
            screenBoundsToCapture = requestedBounds;
            if (clip == ScreenshotClipMode.Intersect)
            {
                var clipped = Intersect(screenBoundsToCapture, containerBounds);
                screenWasClipped = clipped != screenBoundsToCapture;
                screenBoundsToCapture = clipped;
            }

            var clippedPrintWindow = Intersect(requestedBounds, containerBounds);
            printWindowWasClipped = clippedPrintWindow != requestedBounds;
            printWindowBoundsToCapture = clippedPrintWindow;
        }

        screenBoundsToCapture = ClampToVirtualScreen(screenBoundsToCapture, virtualScreen, ref screenWasClipped);

        if (requestedMode == ScreenshotCaptureMode.Screen)
        {
            if (IsEmpty(screenBoundsToCapture))
            {
                throw new InvalidOperationException("Requested bounds are outside the capture area.");
            }

            if (!includeOverlay)
            {
                HighlightOverlay.Hide();
            }

            var bitmap = CaptureScreenRegion(screenBoundsToCapture);
            return (bitmap, screenBoundsToCapture, requestedBounds, screenWasClipped, ScreenshotCaptureMode.Screen);
        }

        Bitmap? TryCapturePrintWindowFull()
        {
            if (area == ScreenshotCaptureArea.Client)
            {
                return TryCaptureClientAreaWithPrintWindow(window);
            }

            return TryCaptureWindowWithPrintWindow(window);
        }

        Bitmap? TryCapturePrintWindowCropped(Rect boundsToCrop)
        {
            if (IsEmpty(boundsToCrop))
            {
                return null;
            }

            if (area == ScreenshotCaptureArea.Client)
            {
                using var clientBitmap = TryCaptureClientAreaWithPrintWindow(window);
                if (clientBitmap is null)
                {
                    return null;
                }

                return TryCropBoundsFromClientBitmap(window, boundsToCrop, clientBitmap);
            }

            using var windowBitmap = TryCaptureWindowWithPrintWindow(window);
            if (windowBitmap is not null)
            {
                var cropped = TryCropBoundsFromWindowBitmap(window, boundsToCrop, windowBitmap);
                if (cropped is not null)
                {
                    return cropped;
                }
            }

            using var fallbackClientBitmap = TryCaptureClientAreaWithPrintWindow(window);
            if (fallbackClientBitmap is not null)
            {
                var cropped = TryCropBoundsFromClientBitmap(window, boundsToCrop, fallbackClientBitmap);
                if (cropped is not null)
                {
                    return cropped;
                }
            }

            return null;
        }

        if (requestedMode == ScreenshotCaptureMode.PrintWindow)
        {
            Bitmap? bitmap;
            Rect capturedBounds;
            bool wasClipped;

            if (requestedBounds is null)
            {
                bitmap = TryCapturePrintWindowFull();
                capturedBounds = containerBounds;
                wasClipped = false;
            }
            else
            {
                capturedBounds = printWindowBoundsToCapture;
                wasClipped = printWindowWasClipped;
                bitmap = TryCapturePrintWindowCropped(capturedBounds);
            }

            if (bitmap is null)
            {
                throw new InvalidOperationException("PrintWindow capture failed.");
            }

            return (bitmap, capturedBounds, requestedBounds, wasClipped, ScreenshotCaptureMode.PrintWindow);
        }

        if (requestedMode != ScreenshotCaptureMode.Auto)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode), requestedMode, "Unsupported capture mode.");
        }

        // Auto: prefer PrintWindow when it can satisfy the request; fall back to screen capture.
        if (requestedBounds is not null && clip == ScreenshotClipMode.None && printWindowWasClipped)
        {
            // PrintWindow cannot capture outside the window/client area; use screen capture to honor clip=None.
            if (IsEmpty(screenBoundsToCapture))
            {
                throw new InvalidOperationException("Requested bounds are outside the capture area.");
            }

            if (!includeOverlay)
            {
                HighlightOverlay.Hide();
            }

            var screen = CaptureScreenRegion(screenBoundsToCapture);
            return (screen, screenBoundsToCapture, requestedBounds, screenWasClipped, ScreenshotCaptureMode.Screen);
        }

        try
        {
            if (requestedBounds is null)
            {
                var printWindow = TryCapturePrintWindowFull();
                if (printWindow is not null)
                {
                    return (printWindow, containerBounds, requestedBounds, WasClipped: false, ScreenshotCaptureMode.PrintWindow);
                }
            }
            else
            {
                var bounds = printWindowBoundsToCapture;
                if (!IsEmpty(bounds))
                {
                    var printWindow = TryCapturePrintWindowCropped(bounds);
                    if (printWindow is not null)
                    {
                        return (printWindow, bounds, requestedBounds, printWindowWasClipped, ScreenshotCaptureMode.PrintWindow);
                    }
                }
            }
        }
        catch
        {
            // Ignore and fall back to screen capture.
        }

        if (IsEmpty(screenBoundsToCapture))
        {
            throw new InvalidOperationException("Requested bounds are outside the capture area.");
        }

        if (!includeOverlay)
        {
            HighlightOverlay.Hide();
        }

        var screenBitmap = CaptureScreenRegion(screenBoundsToCapture);
        return (screenBitmap, screenBoundsToCapture, requestedBounds, screenWasClipped, ScreenshotCaptureMode.Screen);
    }

    private static Bitmap CaptureScreenRegion(Rect bounds)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Screen capture is only supported on Windows.");
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("Capture bounds must be > 0.");
        }

        try
        {
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                var hdcDest = graphics.GetHdc();
                try
                {
                    var hdcSrc = GetDC(IntPtr.Zero);
                    if (hdcSrc == IntPtr.Zero)
                    {
                        throw new InvalidOperationException($"GetDC failed: {Marshal.GetLastWin32Error()}");
                    }

                    try
                    {
                        if (!BitBlt(
                                hdcDest,
                                xDest: 0,
                                yDest: 0,
                                width: bounds.Width,
                                height: bounds.Height,
                                hdcSrc,
                                xSrc: bounds.X,
                                ySrc: bounds.Y,
                                rop: SRCCOPY | CAPTUREBLT))
                        {
                            throw new InvalidOperationException($"BitBlt failed: {Marshal.GetLastWin32Error()}");
                        }
                    }
                    finally
                    {
                        _ = ReleaseDC(IntPtr.Zero, hdcSrc);
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdcDest);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Screen capture failed.", ex);
        }
    }

    private static Rect ComputeElementCapturedBoundsInClient(Window window, AutomationElement element)
    {
        return ComputeElementCapturedBoundsInClient(window, ToRect(element.BoundingRectangle));
    }

    private static Rect ComputeElementCapturedBoundsInClient(Window window, Rect elementBounds)
    {
        if (!TryGetClientBoundsScreen(window, out var clientBounds))
        {
            return elementBounds;
        }

        var left = Math.Max(elementBounds.X, clientBounds.X);
        var top = Math.Max(elementBounds.Y, clientBounds.Y);
        var right = Math.Min(elementBounds.X + elementBounds.Width, clientBounds.X + clientBounds.Width);
        var bottom = Math.Min(elementBounds.Y + elementBounds.Height, clientBounds.Y + clientBounds.Height);

        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        return new Rect(left, top, width, height);
    }

    private static bool TryGetClientBoundsScreen(Window window, out Rect bounds)
    {
        bounds = new Rect(0, 0, 0, 0);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!TryGetClientTopLeftScreen(hwnd, out var clientTopLeft))
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var clientRect))
        {
            return false;
        }

        bounds = new Rect(clientTopLeft.X, clientTopLeft.Y, clientRect.Width, clientRect.Height);
        return true;
    }

    private static bool TryGetWindowBoundsScreen(Window window, out Rect bounds)
    {
        bounds = new Rect(0, 0, 0, 0);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        bounds = new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
        return true;
    }

    private static string ResolveScreenshotOutputPath(string? outputPath, ScreenshotImageFormat format)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(fullPath)))
            {
                fullPath = $"{fullPath}.{GetImageFileExtension(format)}";
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return fullPath;
        }

        var screenshotDirectory = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(screenshotDirectory))
        {
            screenshotDirectory = Path.Combine(Path.GetTempPath(), "wpf-tools-mcp", "screenshots");
        }

        Directory.CreateDirectory(screenshotDirectory);
        var filename = $"screenshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.{GetImageFileExtension(format)}";
        return Path.Combine(screenshotDirectory, filename);
    }

    private static string GetImageFileExtension(ScreenshotImageFormat format) =>
        format switch
        {
            ScreenshotImageFormat.Png => "png",
            ScreenshotImageFormat.Jpeg => "jpg",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported image format.")
        };

    private static string GetImageFormatName(ScreenshotImageFormat format) =>
        format switch
        {
            ScreenshotImageFormat.Png => "png",
            ScreenshotImageFormat.Jpeg => "jpeg",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported image format.")
        };

    private static void SaveBitmapWithWic(Bitmap bitmap, string outputPath, ScreenshotImageFormat format, int jpegQuality)
    {
        using var normalized = EnsureArgbBitmap(bitmap);
        var pixelBytes = CopyBitmapBytes(normalized, out var stride);
        var bitmapSource = BitmapSource.Create(
            normalized.Width,
            normalized.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null,
            pixelBytes,
            stride);

        BitmapEncoder encoder = format switch
        {
            ScreenshotImageFormat.Png => new PngBitmapEncoder(),
            ScreenshotImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported image format.")
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private static Bitmap EnsureArgbBitmap(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb);
        }

        var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(converted);
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        return converted;
    }

    private static byte[] CopyBitmapBytes(Bitmap bitmap, out int stride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var sourceStride = bitmapData.Stride;
            stride = Math.Abs(sourceStride);
            var raw = new byte[stride * bitmap.Height];
            Marshal.Copy(bitmapData.Scan0, raw, 0, raw.Length);
            if (sourceStride >= 0)
            {
                return raw;
            }

            var flipped = new byte[raw.Length];
            for (var row = 0; row < bitmap.Height; row++)
            {
                Buffer.BlockCopy(raw, (bitmap.Height - row - 1) * stride, flipped, row * stride, stride);
            }

            return flipped;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    public async Task<FocusWindowResponse> FocusWindowAsync(
        FocusWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("set_active_window");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            if (request.WindowHandle is not null && !string.IsNullOrWhiteSpace(request.Title))
            {
                throw new ArgumentException("Provide either windowHandle or title, not both.");
            }

            var window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : !string.IsNullOrWhiteSpace(request.Title)
                    ? FindWindowByTitle(application, automation, request.Title!)
                    : FindMainWindow(application, automation);

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            var response = new FocusWindowResponse(
                Focused: true,
                Handle: window.Properties.NativeWindowHandle.Value.ToInt64(),
                Title: window.Title);

            trace?.SetSummary($"handle={response.Handle} title={response.Title}");
            return response;
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
    }

    public async Task<ClickElementResponse> ClickElementAsync(
        ClickElementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("click_element");
        try
        {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("click_element requires exactly one of: locator OR elementId.");
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
        var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
        var stableMs = Math.Clamp(request.StableMs, 0, 5000);

        Window window;
        AutomationElement element;

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        if (hasElementId)
        {
            var elementId = request.ElementId!.Trim();
            var handle = RequireHandle(elementId);

            if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            try
            {
                window = FindWindowByHandle(application, automation, handle.WindowHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
            }

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            if (handle.Backend == InspectionBackend.Wpf)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(elementId, "click_element", cancellationToken).ConfigureAwait(false);

                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var clickPoint = GetRectCenterPoint(bounds);
                switch (request.ClickType)
                {
                    case ClickType.Single:
                        Mouse.LeftClick(clickPoint);
                        break;
                    case ClickType.Double:
                        Mouse.LeftDoubleClick(clickPoint);
                        break;
                    case ClickType.Right:
                        Mouse.RightClick(clickPoint);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(request), $"Unknown clickType '{request.ClickType}'.");
                }

                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=mouse_wpf");
                return new ClickElementResponse(Clicked: true, MethodUsed: "mouse");
            }

            if (handle.Backend != InspectionBackend.Uia)
            {
                throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
            }

            element = ResolveUiaElementById(window, rawWalker, elementId, out _);
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                window,
                request.Locator!,
                request.AutoWait ? timeoutMs : 0,
                pollIntervalMs,
                request.AutoWait ? stableMs : 0,
                visibleOnly: true,
                includeOffViewport: true,
                interactiveOnly: false,
                interactiveMode: InteractiveMode.Heuristic,
                cancellationToken).ConfigureAwait(false);

            if (wpfTarget is not null)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(wpfTarget.ElementId, "click_element", cancellationToken).ConfigureAwait(false);

                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    wpfTarget.Handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var clickPoint = GetRectCenterPoint(bounds);
                switch (request.ClickType)
                {
                    case ClickType.Single:
                        Mouse.LeftClick(clickPoint);
                        break;
                    case ClickType.Double:
                        Mouse.LeftDoubleClick(clickPoint);
                        break;
                    case ClickType.Right:
                        Mouse.RightClick(clickPoint);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(request), $"Unknown clickType '{request.ClickType}'.");
                }

                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=mouse_wpf");
                return new ClickElementResponse(Clicked: true, MethodUsed: "mouse");
            }

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            element = request.AutoWait
                ? await ResolveUiaElementWithWaitAsync(
                    window,
                    request.Locator!,
                    controlWalker,
                    rawWalker,
                    timeoutMs,
                    pollIntervalMs,
                    ActionKind.Click,
                    cancellationToken)
                : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.Click);
        }

        TryScrollIntoView(element);
        EnsureEnabledOrThrow(element, "click_element");

        if (request.AutoWait)
        {
            if (stableMs > 0)
            {
                await WaitForResolvedElementStateAsync(
                    element,
                    WaitForState.Stable,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }

            await WaitForResolvedElementStateAsync(
                element,
                WaitForState.Visible,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                expectedValue: null,
                expectedText: null,
                cancellationToken);
        }

        if (request.ClickType == ClickType.Single &&
            request.ClickMode != ClickMode.MouseAlways)
        {
            var shouldTryInvoke = request.ClickMode == ClickMode.InvokePreferred ||
                                  (request.ClickMode == ClickMode.Auto && ShouldAutoPreferInvoke(element));

            if (shouldTryInvoke && element.Patterns.Invoke.PatternOrDefault is { } invoke)
            {
                try
                {
                    invoke.Invoke();
                }
                catch (COMException ex)
                {
                    throw (InvalidOperationException)WrapUiaActionException(ex, "click_element", element);
                }
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }
                trace?.SetSummary("method=invoke");
                return new ClickElementResponse(Clicked: true, MethodUsed: "invoke");
            }

            if (element.Patterns.Toggle.PatternOrDefault is { } toggle)
            {
                try
                {
                    toggle.Toggle();
                }
                catch (COMException ex)
                {
                    throw (InvalidOperationException)WrapUiaActionException(ex, "click_element", element);
                }
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }
                trace?.SetSummary("method=toggle");
                return new ClickElementResponse(Clicked: true, MethodUsed: "toggle");
            }

            if (element.Patterns.SelectionItem.PatternOrDefault is { } selectionItem)
            {
                try
                {
                    selectionItem.Select();
                }
                catch (COMException ex)
                {
                    throw (InvalidOperationException)WrapUiaActionException(ex, "click_element", element);
                }
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }
                trace?.SetSummary("method=selectionItem");
                return new ClickElementResponse(Clicked: true, MethodUsed: "selectionItem");
            }
        }

        var point = GetClickPoint(element);
        switch (request.ClickType)
        {
            case ClickType.Single:
                Mouse.LeftClick(point);
                break;
            case ClickType.Double:
                Mouse.LeftDoubleClick(point);
                break;
            case ClickType.Right:
                Mouse.RightClick(point);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), $"Unknown clickType '{request.ClickType}'.");
        }

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }
        trace?.SetSummary("method=mouse");
        return new ClickElementResponse(Clicked: true, MethodUsed: "mouse");
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
    }

    private static bool ShouldAutoPreferInvoke(AutomationElement element)
    {
        return element.ControlType == ControlType.Button ||
               element.ControlType == ControlType.Hyperlink ||
               element.ControlType == ControlType.MenuItem ||
               element.ControlType == ControlType.SplitButton;
    }

    private static async Task PrepareWindowForInteractionAsync(
        Window window,
        int settleDelayMs,
        CancellationToken cancellationToken)
    {
        var windowPattern = window.Patterns.Window.PatternOrDefault;
        if (windowPattern is not null && windowPattern.WindowVisualState == WindowVisualState.Minimized)
        {
            try
            {
                windowPattern.SetWindowVisualState(WindowVisualState.Normal);
            }
            catch
            {
            }

            await Task.Delay(Math.Max(UiDelayWindowSettleMs, 100), cancellationToken);
        }

        try
        {
            window.SetForeground();
        }
        catch
        {
        }

        try
        {
            window.Focus();
        }
        catch
        {
        }

        await Task.Delay(settleDelayMs, cancellationToken);
    }

    public async Task<InvokeResponse> InvokeAsync(
        InvokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("invoke");
        try
        {
            var hasLocator = request.Locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            if (hasLocator == hasElementId)
            {
                throw new ArgumentException("invoke requires exactly one of: locator OR elementId.");
            }

            var application = EnsureAttached();
            var automation = EnsureAutomation();

            var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
            var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
            var stableMs = Math.Clamp(request.StableMs, 0, 5000);

            Window window;
            AutomationElement element;
            ElementHandle? wpfSourceHandle = null;

            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            if (hasElementId)
            {
                var elementId = request.ElementId!.Trim();
                var handle = RequireHandle(elementId);

                if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the elementId window.");
                }

                try
                {
                    window = FindWindowByHandle(application, automation, handle.WindowHandle);
                }
                catch
                {
                    throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
                }

                await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

                if (handle.Backend == InspectionBackend.Wpf)
                {
                    wpfSourceHandle = handle;
                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, elementId, handle, out _);
                }
                else if (handle.Backend == InspectionBackend.Uia)
                {
                    element = ResolveUiaElementById(window, rawWalker, elementId, out _);
                }
                else
                {
                    throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
                }
            }
            else
            {
                window = request.WindowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

                var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                    window,
                    request.Locator!,
                    request.AutoWait ? timeoutMs : 0,
                    pollIntervalMs,
                    request.AutoWait ? stableMs : 0,
                    visibleOnly: true,
                    includeOffViewport: true,
                    interactiveOnly: false,
                    interactiveMode: InteractiveMode.Heuristic,
                    cancellationToken).ConfigureAwait(false);

                if (wpfTarget is not null)
                {
                    wpfSourceHandle = wpfTarget.Handle;
                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, wpfTarget.ElementId, wpfTarget.Handle, out _);
                }
                else
                {
                    element = request.AutoWait
                        ? await ResolveUiaElementWithWaitAsync(
                            window,
                            request.Locator!,
                            controlWalker,
                            rawWalker,
                            timeoutMs,
                            pollIntervalMs,
                            ActionKind.Invoke,
                            cancellationToken)
                        : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.Invoke);
                }
            }

            TryScrollIntoView(element);
            EnsureEnabledOrThrow(element, "invoke");

            if (request.AutoWait)
            {
                if (stableMs > 0)
                {
                    await WaitForResolvedElementStateAsync(
                        element,
                        WaitForState.Stable,
                        timeoutMs,
                        pollIntervalMs,
                        stableMs,
                        expectedValue: null,
                        expectedText: null,
                        cancellationToken);
                }

                await WaitForResolvedElementStateAsync(
                    element,
                    WaitForState.Visible,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }

            var invoke = element.Patterns.Invoke.PatternOrDefault;
            if (invoke is null)
            {
                throw CreateInvokePatternNotSupportedException(element, wpfSourceHandle);
            }

            try
            {
                invoke.Invoke();
            }
            catch (COMException ex)
            {
                throw (InvalidOperationException)WrapUiaActionException(ex, "invoke", element);
            }
            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            var response = new InvokeResponse(Invoked: true);
            trace?.SetSummary("invoked=true");
            return response;
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
    }

    private static InvalidOperationException CreateInvokePatternNotSupportedException(
        AutomationElement element,
        ElementHandle? wpfSourceHandle)
    {
        var uiaDescription =
            $"ControlType={element.ControlType}, AutomationId={GetAutomationId(element)}, Name={GetName(element)}";

        if (wpfSourceHandle is null)
        {
            return new InvalidOperationException($"InvokePattern not supported for element ({uiaDescription}).");
        }

        var wpfDescription =
            $"WpfType={wpfSourceHandle.Type}, AutomationId={wpfSourceHandle.AutomationId}, Name={wpfSourceHandle.Name}";
        return new InvalidOperationException(
            $"InvokePattern not supported for WPF element ({wpfDescription}); resolved UIA peer ({uiaDescription}).");
    }

    public async Task<TypeTextResponse> TypeTextAsync(
        TypeTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("type_text");
        try
        {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("type_text requires exactly one of: locator OR elementId.");
        }

        if (request.Text is null)
        {
            throw new ArgumentException("text cannot be null.");
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
        var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
        var stableMs = Math.Clamp(request.StableMs, 0, 5000);

        Window window;
        AutomationElement element;

        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        if (hasElementId)
        {
            var elementId = request.ElementId!.Trim();
            var handle = RequireHandle(elementId);

            if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            try
            {
                window = FindWindowByHandle(application, automation, handle.WindowHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
            }

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            if (handle.Backend == InspectionBackend.Wpf)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(elementId, "type_text", cancellationToken).ConfigureAwait(false);

                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var point = GetRectCenterPoint(bounds);
                Mouse.LeftClick(point);
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type(VirtualKeyShort.DELETE);
                Keyboard.Type(request.Text);

                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=keyboard_wpf");
                return new TypeTextResponse(Typed: true, MethodUsed: "keyboard");
            }

            if (handle.Backend != InspectionBackend.Uia)
            {
                throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
            }

            element = ResolveUiaElementById(window, rawWalker, elementId, out _);
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                window,
                request.Locator!,
                request.AutoWait ? timeoutMs : 0,
                pollIntervalMs,
                request.AutoWait ? stableMs : 0,
                visibleOnly: true,
                includeOffViewport: true,
                interactiveOnly: false,
                interactiveMode: InteractiveMode.Heuristic,
                cancellationToken).ConfigureAwait(false);

            if (wpfTarget is not null)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(wpfTarget.ElementId, "type_text", cancellationToken).ConfigureAwait(false);

                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    wpfTarget.Handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var point = GetRectCenterPoint(bounds);
                Mouse.LeftClick(point);
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type(VirtualKeyShort.DELETE);
                Keyboard.Type(request.Text);

                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=keyboard_wpf");
                return new TypeTextResponse(Typed: true, MethodUsed: "keyboard");
            }

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            element = request.AutoWait
                ? await ResolveUiaElementWithWaitAsync(
                    window,
                    request.Locator!,
                    controlWalker,
                    rawWalker,
                    timeoutMs,
                    pollIntervalMs,
                    ActionKind.TypeText,
                    cancellationToken)
                : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.TypeText);
        }

        TryScrollIntoView(element);
        EnsureEnabledOrThrow(element, "type_text");

        if (request.AutoWait)
        {
            if (stableMs > 0)
            {
                await WaitForResolvedElementStateAsync(
                    element,
                    WaitForState.Stable,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }

            await WaitForResolvedElementStateAsync(
                element,
                WaitForState.Visible,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                expectedValue: null,
                expectedText: null,
                cancellationToken);
        }

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern is not null && valuePattern.IsReadOnly == false)
        {
            try
            {
                valuePattern.SetValue(request.Text);
            }
            catch (COMException ex)
            {
                throw (InvalidOperationException)WrapUiaActionException(ex, "type_text", element);
            }
            if (request.AutoWait)
            {
                await WaitForValuePatternTextAsync(
                    valuePattern,
                    expected: request.Text,
                    timeoutMs,
                    pollIntervalMs,
                    cancellationToken);
            }
            else if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }
            trace?.SetSummary("method=valuePattern");
            return new TypeTextResponse(Typed: true, MethodUsed: "valuePattern");
        }

         element.Focus();
        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Keyboard.Type(request.Text);

        if (request.AutoWait)
        {
            var afterValuePattern = element.Patterns.Value.PatternOrDefault;
            if (afterValuePattern is not null && afterValuePattern.IsReadOnly == false)
            {
                await WaitForValuePatternTextAsync(
                    afterValuePattern,
                    expected: request.Text,
                    timeoutMs,
                    pollIntervalMs,
                    cancellationToken);
            }
        }
        else if (UiDelayMs > 0)
         {
             await Task.Delay(UiDelayMs, cancellationToken);
         }
        trace?.SetSummary("method=keyboard");
        return new TypeTextResponse(Typed: true, MethodUsed: "keyboard");
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
    }

    public async Task<SetValueResponse> SetValueAsync(
        SetValueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("set_value");
        try
        {
            var hasLocator = request.Locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            if (hasLocator == hasElementId)
            {
                throw new ArgumentException("set_value requires exactly one of: locator OR elementId.");
            }

            var application = EnsureAttached();
            var automation = EnsureAutomation();

            var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
            var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
            var stableMs = Math.Clamp(request.StableMs, 0, 5000);
            var hasNumericValue = request.Value.HasValue;
            var hasTextValue = request.Text is not null;
            if (hasNumericValue == hasTextValue)
            {
                throw new ArgumentException("set_value requires exactly one of: value OR text.");
            }

            var valueText = hasTextValue
                ? request.Text!
                : request.Value!.Value.ToString(CultureInfo.InvariantCulture);
            var numericValue = request.Value.GetValueOrDefault();

            Window window;
            AutomationElement element;

            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            if (hasElementId)
            {
                var elementId = request.ElementId!.Trim();
                var handle = RequireHandle(elementId);

                if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the elementId window.");
                }

                try
                {
                    window = FindWindowByHandle(application, automation, handle.WindowHandle);
                }
                catch
                {
                    throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
                }

                await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);
                if (handle.Backend == InspectionBackend.Wpf)
                {
                    var wpfSet = await TrySetWpfValueAsync(elementId, handle, request, cancellationToken).ConfigureAwait(false);
                    if (wpfSet is not null)
                    {
                        trace?.SetSummary($"method={wpfSet.MethodUsed}");
                        return wpfSet;
                    }

                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, elementId, handle, out _);
                }
                else if (handle.Backend == InspectionBackend.Uia)
                {
                    element = ResolveUiaElementById(window, rawWalker, elementId, out _);
                }
                else
                {
                    throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
                }
            }
            else
            {
                window = request.WindowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

                var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                    window,
                    request.Locator!,
                    request.AutoWait ? timeoutMs : 0,
                    pollIntervalMs,
                    request.AutoWait ? stableMs : 0,
                    visibleOnly: true,
                    includeOffViewport: true,
                    interactiveOnly: false,
                    interactiveMode: InteractiveMode.Heuristic,
                    cancellationToken).ConfigureAwait(false);

                if (wpfTarget is not null)
                {
                    var wpfSet = await TrySetWpfValueAsync(wpfTarget.ElementId, wpfTarget.Handle, request, cancellationToken).ConfigureAwait(false);
                    if (wpfSet is not null)
                    {
                        trace?.SetSummary($"method={wpfSet.MethodUsed}");
                        return wpfSet;
                    }

                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, wpfTarget.ElementId, wpfTarget.Handle, out _);
                }
                else
                {
                    element = request.AutoWait
                        ? await ResolveUiaElementWithWaitAsync(
                            window,
                            request.Locator!,
                            controlWalker,
                            rawWalker,
                            timeoutMs,
                            pollIntervalMs,
                            ActionKind.SetValue,
                            cancellationToken)
                        : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.SetValue);
                }
            }

            TryScrollIntoView(element);
            EnsureEnabledOrThrow(element, "set_value");

            var triedDrag = false;
            var preferDrag = hasNumericValue &&
                             (element.ControlType == ControlType.Thumb ||
                              HasMultipleThumbDescendants(element, rawWalker, maxNodesToScan: 5000));

            if (request.AutoWait)
            {
                if (stableMs > 0)
                {
                    await WaitForResolvedElementStateAsync(
                        element,
                        WaitForState.Stable,
                        timeoutMs,
                        pollIntervalMs,
                        stableMs,
                        expectedValue: null,
                        expectedText: null,
                        cancellationToken);
                }

                await WaitForResolvedElementStateAsync(
                    element,
                    WaitForState.Visible,
                    timeoutMs,
                    pollIntervalMs,
                     stableMs,
                     expectedValue: null,
                     expectedText: null,
                     cancellationToken);
            }

            if (preferDrag)
            {
                triedDrag = true;
                if (await TrySetValueByDraggingAsync(
                        element,
                        rawWalker,
                        numericValue,
                        request.AutoWait,
                        timeoutMs,
                        pollIntervalMs,
                        steps: 16,
                        cancellationToken).ConfigureAwait(false))
                {
                    trace?.SetSummary("method=drag");
                    return new SetValueResponse(Set: true, MethodUsed: "drag");
                }
            }

            var rangeValue = element.Patterns.RangeValue.PatternOrDefault;
            if (hasNumericValue && rangeValue is not null && rangeValue.IsReadOnly == false)
            {
                try
                {
                    rangeValue.SetValue(numericValue);
                }
                catch (COMException ex)
                {
                    if (!triedDrag &&
                        await TrySetValueByDraggingAsync(
                            element,
                            rawWalker,
                            numericValue,
                            request.AutoWait,
                            timeoutMs,
                            pollIntervalMs,
                            steps: 16,
                            cancellationToken).ConfigureAwait(false))
                    {
                        trace?.SetSummary("method=drag");
                        return new SetValueResponse(Set: true, MethodUsed: "drag");
                    }
                    throw (InvalidOperationException)WrapUiaActionException(ex, "set_value", element);
                }
                if (request.AutoWait)
                {
                    try
                    {
                        await WaitForRangeValueAsync(rangeValue, expected: numericValue, timeoutMs, pollIntervalMs, cancellationToken);
                    }
                    catch
                    {
                        if (!triedDrag &&
                            await TrySetValueByDraggingAsync(
                                element,
                                rawWalker,
                                numericValue,
                                request.AutoWait,
                                timeoutMs,
                                pollIntervalMs,
                                steps: 16,
                                cancellationToken).ConfigureAwait(false))
                        {
                            trace?.SetSummary("method=drag");
                            return new SetValueResponse(Set: true, MethodUsed: "drag");
                        }

                        throw;
                    }
                }
                else if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=rangeValue");
                return new SetValueResponse(Set: true, MethodUsed: "rangeValue");
            }

            var valuePattern = element.Patterns.Value.PatternOrDefault;
            if (valuePattern is not null && valuePattern.IsReadOnly == false)
            {
                try
                {
                    valuePattern.SetValue(valueText);
                }
                catch (COMException ex)
                {
                    throw (InvalidOperationException)WrapUiaActionException(ex, "set_value", element);
                }
                if (request.AutoWait)
                {
                    await WaitForValuePatternTextAsync(
                        valuePattern,
                        expected: valueText,
                        timeoutMs,
                        pollIntervalMs,
                        cancellationToken);
                }
                else if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=valuePattern");
                return new SetValueResponse(Set: true, MethodUsed: "valuePattern");
            }

            throw new InvalidOperationException(
                $"set_value unsupported for element (ControlType={element.ControlType}, AutomationId={GetAutomationId(element)}, Name={GetName(element)}): supports neither writable RangeValuePattern nor writable ValuePattern for the requested input.");
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
    }

    public async Task<SelectItemResponse> SelectItemAsync(
        SelectItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("select_item");
        try
        {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("select_item requires exactly one of: locator OR elementId.");
        }

        var hasItemElementId = !string.IsNullOrWhiteSpace(request.ItemElementId);
        var hasItemLocator = request.ItemLocator is not null;
        var hasText = !string.IsNullOrWhiteSpace(request.Text);
        var hasIndex = request.Index is not null;

        if (hasItemElementId && (hasItemLocator || hasText || hasIndex))
        {
            throw new ArgumentException("Provide either itemElementId OR itemLocator OR text OR index, not a combination.");
        }

        if (hasItemLocator && (hasText || hasIndex))
        {
            throw new ArgumentException("Provide either itemLocator or text/index, not both.");
        }

        if (!hasItemElementId && !hasItemLocator && !(hasText ^ hasIndex))
        {
            throw new ArgumentException("select_item requires exactly one of: itemElementId OR itemLocator OR text OR index.");
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
        var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
        var stableMs = Math.Clamp(request.StableMs, 0, 5000);

        Window window;
        AutomationElement container;

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        if (hasElementId)
        {
            var elementId = request.ElementId!.Trim();
            var handle = RequireHandle(elementId);

            if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            try
            {
                window = FindWindowByHandle(application, automation, handle.WindowHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
            }

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

            if (handle.Backend == InspectionBackend.Wpf)
            {
                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var point = GetRectCenterPoint(bounds);
                container = automation.FromPoint(point)
                    ?? throw new InvalidOperationException("No UIA element found at point.");

                try
                {
                    if (container.Properties.ProcessId.Value != application.ProcessId)
                    {
                        throw new InvalidOperationException("Point resolved to a different process.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to validate picked element: {ex.Message}");
                }
            }
            else if (handle.Backend == InspectionBackend.Uia)
            {
                container = ResolveUiaElementById(window, rawWalker, elementId, out _);
            }
            else
            {
                throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
            }
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);
            container = request.AutoWait
                ? await ResolveUiaElementWithWaitAsync(
                    window,
                    request.Locator!,
                    controlWalker,
                    rawWalker,
                    timeoutMs,
                    pollIntervalMs,
                    ActionKind.SelectItem,
                    cancellationToken)
                : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.SelectItem);
        }

        TryScrollIntoView(container);
        EnsureEnabledOrThrow(container, "select_item");

        if (request.AutoWait)
        {
            if (stableMs > 0)
            {
                await WaitForResolvedElementStateAsync(
                    container,
                    WaitForState.Stable,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }

            await WaitForResolvedElementStateAsync(
                container,
                WaitForState.Visible,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                expectedValue: null,
                expectedText: null,
                cancellationToken);
        }

        if (hasItemElementId)
        {
            var itemElementId = request.ItemElementId!.Trim();
            var itemHandle = RequireHandle(itemElementId);

            if (itemHandle.WindowHandle != window.Properties.NativeWindowHandle.Value.ToInt64())
            {
                throw new ArgumentException("itemElementId window does not match container window.");
            }

            AutomationElement item;
            if (itemHandle.Backend == InspectionBackend.Wpf)
            {
                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    itemHandle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var point = GetRectCenterPoint(bounds);
                item = automation.FromPoint(point)
                    ?? throw new InvalidOperationException("No UIA element found at point.");

                try
                {
                    if (item.Properties.ProcessId.Value != application.ProcessId)
                    {
                        throw new InvalidOperationException("Point resolved to a different process.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to validate picked element: {ex.Message}");
                }
            }
            else if (itemHandle.Backend == InspectionBackend.Uia)
            {
                item = ResolveUiaElementById(window, rawWalker, itemElementId, out _);
            }
            else
            {
                throw new InvalidOperationException($"itemElementId '{itemElementId}' has unsupported backend '{itemHandle.Backend}'.");
            }
            TryScrollIntoView(item);
            SelectItemElement(item);

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }
            trace?.SetSummary("selected=true");
            return new SelectItemResponse(Selected: true);
        }

        if (hasItemLocator)
        {
            var itemLocator = request.ItemLocator!;
            var item = !string.IsNullOrWhiteSpace(itemLocator.XPath)
                ? ResolveElement(window, itemLocator, controlWalker, rawWalker)
                : await ResolveElementWithinRootOrScrollAsync(container, itemLocator, controlWalker, cancellationToken);

            TryScrollIntoView(item);
            SelectItemElement(item);

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }
            trace?.SetSummary("selected=true");
            return new SelectItemResponse(Selected: true);
        }

        if (container.ControlType == ControlType.ComboBox)
        {
            var comboBox = container.AsComboBox();
            if (hasIndex)
            {
                comboBox.Select(request.Index!.Value);
            }
            else
            {
                comboBox.Select(request.Text!);
            }

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }
            trace?.SetSummary("selected=true");
            return new SelectItemResponse(Selected: true);
        }

        var allItems = EnumerateSelectableItems(container, controlWalker).ToArray();
        if (allItems.Length == 0)
        {
            throw new InvalidOperationException(
                $"No selectable items found under locator (ControlType={container.ControlType}, AutomationId={GetAutomationId(container)}, Name={GetName(container)}).");
        }

        var preferredItems = TryFilterItemsToSelectionContainer(container, allItems);

        AutomationElement? selectedItem = null;
        if (hasIndex)
        {
            var items = preferredItems is not null && preferredItems.Length > 0 ? preferredItems : allItems;
            var index = request.Index!.Value;
            if (index < 0 || index >= items.Length)
            {
                throw new InvalidOperationException($"index {index} is out of range (found {items.Length} selectable items).");
            }

            selectedItem = items[index];
        }
        else
        {
            var text = request.Text!;
            if (preferredItems is not null && preferredItems.Length > 0)
            {
                selectedItem = FindUniqueItemByName(preferredItems, text, out var matches);
                if (matches > 1)
                {
                    throw new InvalidOperationException($"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }
            }

            if (selectedItem is null)
            {
                selectedItem = FindUniqueItemByName(allItems, text, out var matches);
                if (matches > 1)
                {
                    throw new InvalidOperationException($"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }

                if (selectedItem is null)
                {
                    selectedItem = await ScrollSearchUniqueItemByNameAsync(
                        container,
                        text,
                        controlWalker,
                        cancellationToken);
                }
            }
        }

        if (selectedItem is null)
        {
            throw new InvalidOperationException("Selected item could not be resolved.");
        }

        TryScrollIntoView(selectedItem);
        SelectItemElement(selectedItem);

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }
        trace?.SetSummary("selected=true");
        return new SelectItemResponse(Selected: true);
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
    }

    public async Task<ScrollToElementResponse> ScrollToElementAsync(
        ScrollToElementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("scroll_to_element");
        try
        {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("scroll_to_element requires exactly one of: locator OR elementId.");
        }

        if (request.ContainerLocator is not null && !string.IsNullOrWhiteSpace(request.ContainerElementId))
        {
            throw new ArgumentException("Provide either containerLocator or containerElementId, not both.");
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
        var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
        var stableMs = Math.Clamp(request.StableMs, 0, 5000);

        Window window;
        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        string? idForWindow = null;
        long? windowHandleFromId = null;
        ElementHandle? elementHandleFromId = null;
        if (hasElementId)
        {
            idForWindow = request.ElementId!.Trim();
            var handle = RequireHandle(idForWindow);
            windowHandleFromId = handle.WindowHandle;
            elementHandleFromId = handle;
        }
        else if (!string.IsNullOrWhiteSpace(request.ContainerElementId))
        {
            idForWindow = request.ContainerElementId!.Trim();
            var handle = RequireHandle(idForWindow);
            windowHandleFromId = handle.WindowHandle;
        }

        if (windowHandleFromId is long resolvedHandle)
        {
            if (request.WindowHandle is long requestedHandle && requestedHandle != resolvedHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            try
            {
                window = FindWindowByHandle(application, automation, resolvedHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{idForWindow}'. Call resolve_element again.");
            }
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);
        }

        await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

        if (hasLocator)
        {
            var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                window,
                request.Locator!,
                request.AutoWait ? timeoutMs : 0,
                pollIntervalMs,
                request.AutoWait ? stableMs : 0,
                visibleOnly: true,
                includeOffViewport: true,
                interactiveOnly: false,
                interactiveMode: InteractiveMode.Heuristic,
                cancellationToken).ConfigureAwait(false);

            if (wpfTarget is not null)
            {
                var beforeBounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    wpfTarget.Handle,
                    autoScroll: false,
                    cancellationToken).ConfigureAwait(false);

                if (TryGetClientBoundsScreen(window, out var clientBounds) && RectIntersects(beforeBounds, clientBounds))
                {
                    var alreadyVisible = new ScrollToElementResponse(Scrolled: false, MethodUsed: "alreadyVisible");
                    trace?.SetSummary($"scrolled={alreadyVisible.Scrolled} method={alreadyVisible.MethodUsed}");
                    return alreadyVisible;
                }

                var bring = await BringIntoViewWpfAsync(wpfTarget.Handle, cancellationToken).ConfigureAwait(false);
                if (UiDelayScrollMs > 0)
                {
                    await Task.Delay(UiDelayScrollMs, cancellationToken);
                }

                var bringResponse = new ScrollToElementResponse(
                    Scrolled: bring.BroughtIntoView,
                    MethodUsed: bring.BroughtIntoView ? "wpf_bringIntoView" : "wpf_bringIntoView_failed");

                trace?.SetSummary($"scrolled={bringResponse.Scrolled} method={bringResponse.MethodUsed}");
                return bringResponse;
            }
        }

        if (hasElementId && elementHandleFromId is not null && elementHandleFromId.Backend == InspectionBackend.Wpf)
        {
            // Best-effort WPF BringIntoView: this supports scrolling WPF elements into view even when UIA patterns are missing.
            var beforeBounds = await ResolveWpfBoundsForHandleAsync(
                window,
                elementHandleFromId,
                autoScroll: false,
                cancellationToken).ConfigureAwait(false);

            if (TryGetClientBoundsScreen(window, out var clientBounds) && RectIntersects(beforeBounds, clientBounds))
            {
                var alreadyVisible = new ScrollToElementResponse(Scrolled: false, MethodUsed: "alreadyVisible");
                trace?.SetSummary($"scrolled={alreadyVisible.Scrolled} method={alreadyVisible.MethodUsed}");
                return alreadyVisible;
            }

            var bring = await BringIntoViewWpfAsync(elementHandleFromId, cancellationToken).ConfigureAwait(false);
            if (UiDelayScrollMs > 0)
            {
                await Task.Delay(UiDelayScrollMs, cancellationToken);
            }

            var bringResponse = new ScrollToElementResponse(
                Scrolled: bring.BroughtIntoView,
                MethodUsed: bring.BroughtIntoView ? "wpf_bringIntoView" : "wpf_bringIntoView_failed");

            trace?.SetSummary($"scrolled={bringResponse.Scrolled} method={bringResponse.MethodUsed}");
            return bringResponse;
        }

        AutomationElement? container = null;
        if (!string.IsNullOrWhiteSpace(request.ContainerElementId))
        {
            var containerElementId = request.ContainerElementId!.Trim();
            var containerHandle = RequireHandle(containerElementId);

            if (containerHandle.Backend == InspectionBackend.Wpf)
            {
                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    containerHandle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);

                var containerPoint = GetRectCenterPoint(bounds);
                container = automation.FromPoint(containerPoint)
                    ?? throw new InvalidOperationException("No UIA element found at point.");

                try
                {
                    if (container.Properties.ProcessId.Value != application.ProcessId)
                    {
                        throw new InvalidOperationException("Point resolved to a different process.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to validate picked element: {ex.Message}");
                }
            }
            else if (containerHandle.Backend == InspectionBackend.Uia)
            {
                container = ResolveUiaElementById(window, rawWalker, containerElementId, out _);
            }
            else
            {
                throw new InvalidOperationException($"containerElementId '{containerElementId}' has unsupported backend '{containerHandle.Backend}'.");
            }

            TryScrollIntoView(container);
        }
        else if (request.ContainerLocator is not null)
        {
            container = ResolveElement(window, request.ContainerLocator, controlWalker, rawWalker, ActionKind.ScrollToElement);
            TryScrollIntoView(container);
        }

        AutomationElement element;
        var scrolledDuringSearch = false;
        string? targetXPath = null;

        if (hasElementId)
        {
            element = ResolveUiaElementById(window, rawWalker, request.ElementId!.Trim(), out var xpathUsed);
            targetXPath = xpathUsed;
        }
        else if (container is not null && string.IsNullOrWhiteSpace(request.Locator!.XPath))
        {
            (element, scrolledDuringSearch) = await ResolveElementWithinContainerOrScrollAsync(
                container,
                request.Locator!,
                controlWalker,
                cancellationToken);
        }
        else
        {
            element = request.AutoWait
                ? await ResolveUiaElementWithWaitAsync(
                    window,
                    request.Locator!,
                    controlWalker,
                    rawWalker,
                    timeoutMs,
                    pollIntervalMs,
                    ActionKind.ScrollToElement,
                    cancellationToken)
                : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.ScrollToElement);
        }

        var elementToScroll = element;
        targetXPath ??= request.Locator?.XPath;
        if (!string.IsNullOrWhiteSpace(targetXPath) && !HasValidBounds(elementToScroll))
        {
            var currentXPath = targetXPath!;
            for (var step = 0; step < 10; step++)
            {
                var parentXPath = TryGetParentXPath(currentXPath);
                if (parentXPath is null)
                {
                    break;
                }

                currentXPath = parentXPath;

                try
                {
                    var parentElement = ResolveElement(window, new ElementLocator(XPath: parentXPath), controlWalker, rawWalker);
                    if (HasValidBounds(parentElement))
                    {
                        elementToScroll = parentElement;
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        var (bringIntoViewMethod, scrolledBringingIntoView) = await ScrollElementIntoViewAsync(
            container,
            elementToScroll,
            controlWalker,
            rawWalker,
            cancellationToken);

        if (request.AutoWait)
        {
            if (stableMs > 0)
            {
                await WaitForResolvedElementStateAsync(
                    elementToScroll,
                    WaitForState.Stable,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }

            await WaitForResolvedElementStateAsync(
                elementToScroll,
                WaitForState.Visible,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                expectedValue: null,
                expectedText: null,
                cancellationToken);
        }

        var methodUsed = scrolledDuringSearch
            ? bringIntoViewMethod == "alreadyVisible"
                ? "scrollSearch"
                : $"scrollSearch+{bringIntoViewMethod}"
            : bringIntoViewMethod;

        var response = new ScrollToElementResponse(
            Scrolled: scrolledDuringSearch || scrolledBringingIntoView,
            MethodUsed: methodUsed);

        trace?.SetSummary($"scrolled={response.Scrolled} method={response.MethodUsed}");
        return response;
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
    }

    public async Task<DragResponse> DragAsync(
        DragRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("drag");
        try
        {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("drag requires exactly one of: locator OR elementId.");
        }

        var hasTargetLocator = request.TargetLocator is not null;
        var hasTargetElementId = !string.IsNullOrWhiteSpace(request.TargetElementId);
        var hasAnyCoordinate = request.ToX is not null || request.ToY is not null;

        if (hasTargetLocator && (hasTargetElementId || hasAnyCoordinate))
        {
            throw new ArgumentException("Provide either targetLocator OR targetElementId OR toX/toY, not a combination.");
        }

        if (hasTargetElementId && (hasTargetLocator || hasAnyCoordinate))
        {
            throw new ArgumentException("Provide either targetLocator OR targetElementId OR toX/toY, not a combination.");
        }

        if (!hasTargetLocator && !hasTargetElementId)
        {
            if (request.ToX is null || request.ToY is null)
            {
                throw new ArgumentException("Provide either targetLocator OR targetElementId OR both toX and toY.");
            }
        }

        var steps = Math.Clamp(request.Steps, 1, 200);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
        var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
        var stableMs = Math.Clamp(request.StableMs, 0, 5000);

        Window window;
        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        string? idForWindow = null;
        long? windowHandleFromId = null;
        ElementHandle? sourceHandleFromId = null;
        ElementHandle? targetHandleFromId = null;

        if (hasElementId)
        {
            var id = request.ElementId!.Trim();
            idForWindow = id;
            sourceHandleFromId = RequireHandle(id);
            windowHandleFromId = sourceHandleFromId.WindowHandle;
        }

        if (hasTargetElementId)
        {
            var id = request.TargetElementId!.Trim();
            idForWindow ??= id;
            targetHandleFromId = RequireHandle(id);
            windowHandleFromId ??= targetHandleFromId.WindowHandle;
        }

        if (hasElementId && hasTargetElementId)
        {
            var sourceHandle = RequireHandle(request.ElementId!.Trim());
            var targetHandle = RequireHandle(request.TargetElementId!.Trim());
            if (sourceHandle.WindowHandle != targetHandle.WindowHandle)
            {
                throw new ArgumentException("elementId and targetElementId must refer to the same window.");
            }
        }

        if (windowHandleFromId is long resolvedHandle)
        {
            if (request.WindowHandle is long requestedHandle && requestedHandle != resolvedHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            try
            {
                window = FindWindowByHandle(application, automation, resolvedHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{idForWindow}'. Call resolve_element again.");
            }
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);
        }

        await PrepareWindowForInteractionAsync(window, settleDelayMs: UiDelayWindowSettleMs, cancellationToken);

        Point start;
        if (hasElementId)
        {
            var handle = sourceHandleFromId ?? RequireHandle(request.ElementId!.Trim());
            if (handle.Backend == InspectionBackend.Wpf)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(request.ElementId!.Trim(), "drag", cancellationToken).ConfigureAwait(false);

                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);
                start = GetRectCenterPoint(bounds);
            }
            else if (handle.Backend == InspectionBackend.Uia)
            {
                var source = ResolveUiaElementById(window, rawWalker, request.ElementId!.Trim(), out _);
                TryScrollIntoView(source);
                EnsureEnabledOrThrow(source, "drag");

                if (request.AutoWait)
                {
                    if (stableMs > 0)
                    {
                        await WaitForResolvedElementStateAsync(
                            source,
                            WaitForState.Stable,
                            timeoutMs,
                            pollIntervalMs,
                            stableMs,
                            expectedValue: null,
                            expectedText: null,
                            cancellationToken);
                    }

                    await WaitForResolvedElementStateAsync(
                        source,
                        WaitForState.Visible,
                        timeoutMs,
                        pollIntervalMs,
                        stableMs,
                        expectedValue: null,
                        expectedText: null,
                        cancellationToken);
                }

                start = GetDragPoint(source);
            }
            else
            {
                throw new InvalidOperationException($"elementId '{request.ElementId!.Trim()}' has unsupported backend '{handle.Backend}'.");
            }
        }
        else
        {
            var source = request.AutoWait
                ? await ResolveUiaElementWithWaitAsync(
                    window,
                    request.Locator!,
                    controlWalker,
                    rawWalker,
                    timeoutMs,
                    pollIntervalMs,
                    ActionKind.Drag,
                    cancellationToken)
                : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.Drag);
            TryScrollIntoView(source);
            EnsureEnabledOrThrow(source, "drag");

            if (request.AutoWait)
            {
                if (stableMs > 0)
                {
                    await WaitForResolvedElementStateAsync(
                        source,
                        WaitForState.Stable,
                        timeoutMs,
                        pollIntervalMs,
                        stableMs,
                        expectedValue: null,
                        expectedText: null,
                        cancellationToken);
                }

                await WaitForResolvedElementStateAsync(
                    source,
                    WaitForState.Visible,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }

            start = GetDragPoint(source);
        }

        Point end;
        if (hasTargetElementId)
        {
            var handle = targetHandleFromId ?? RequireHandle(request.TargetElementId!.Trim());
            if (handle.Backend == InspectionBackend.Wpf)
            {
                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    handle,
                    autoScroll: request.AutoWait,
                    cancellationToken).ConfigureAwait(false);
                end = GetRectCenterPoint(bounds);
            }
            else if (handle.Backend == InspectionBackend.Uia)
            {
                var target = ResolveUiaElementById(window, rawWalker, request.TargetElementId!.Trim(), out _);
                TryScrollIntoView(target);
                if (request.AutoWait)
                {
                    if (stableMs > 0)
                    {
                        await WaitForResolvedElementStateAsync(
                            target,
                            WaitForState.Stable,
                            timeoutMs,
                            pollIntervalMs,
                            stableMs,
                            expectedValue: null,
                            expectedText: null,
                            cancellationToken);
                    }

                    await WaitForResolvedElementStateAsync(
                        target,
                        WaitForState.Visible,
                        timeoutMs,
                        pollIntervalMs,
                        stableMs,
                        expectedValue: null,
                        expectedText: null,
                        cancellationToken);
                }
                end = GetDragPoint(target);
            }
            else
            {
                throw new InvalidOperationException($"targetElementId '{request.TargetElementId!.Trim()}' has unsupported backend '{handle.Backend}'.");
            }
        }
        else if (request.TargetLocator is not null)
        {
            var target = request.AutoWait
                ? await ResolveUiaElementWithWaitAsync(
                    window,
                    request.TargetLocator,
                    controlWalker,
                    rawWalker,
                    timeoutMs,
                    pollIntervalMs,
                    ActionKind.Drag,
                    cancellationToken)
                : ResolveElement(window, request.TargetLocator, controlWalker, rawWalker, ActionKind.Drag);
            TryScrollIntoView(target);
            if (request.AutoWait)
            {
                if (stableMs > 0)
                {
                    await WaitForResolvedElementStateAsync(
                        target,
                        WaitForState.Stable,
                        timeoutMs,
                        pollIntervalMs,
                        stableMs,
                        expectedValue: null,
                        expectedText: null,
                        cancellationToken);
                }

                await WaitForResolvedElementStateAsync(
                    target,
                    WaitForState.Visible,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: null,
                    expectedText: null,
                    cancellationToken);
            }
            end = GetDragPoint(target);
        }
        else
        {
            end = new Point(request.ToX!.Value, request.ToY!.Value);
        }

        var button = ParseMouseButton(request.Button);

        Mouse.MoveTo(start);
        await Task.Delay(1, cancellationToken);

        try
        {
            Mouse.Down(button);
            await Task.Delay(1, cancellationToken);

            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var t = step / (double)steps;
                var x = start.X + (int)Math.Round((end.X - start.X) * t, MidpointRounding.AwayFromZero);
                var y = start.Y + (int)Math.Round((end.Y - start.Y) * t, MidpointRounding.AwayFromZero);

                Mouse.MoveTo(new Point(x, y));
                if (step < steps)
                {
                    await Task.Delay(1, cancellationToken);
                }
            }
        }
        finally
        {
            try
            {
                Mouse.Up(button);
            }
            catch
            {
            }
        }

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }
        var response = new DragResponse(Dragged: true, MethodUsed: "mouse");
        trace?.SetSummary($"dragged={response.Dragged} method={response.MethodUsed}");
        return response;
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
    }

    public async Task<WaitForResponse> WaitForAsync(
        WaitForRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("wait_for");
        try
        {
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("wait_for requires exactly one of: locator OR elementId.");
        }

        var state = ParseWaitForState(request.State);

        if (state == WaitForState.ValueEquals && request.ExpectedValue is null)
        {
            throw new ArgumentException("expectedValue is required when state=value_equals.");
        }

        if (state == WaitForState.NameContains && string.IsNullOrWhiteSpace(request.ExpectedText))
        {
            throw new ArgumentException("expectedText is required when state=name_contains.");
        }

        var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
        var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
        var stableMs = Math.Clamp(request.StableMs, 0, 5000);

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var backendForLocator = request.Backend;
        if (hasLocator && backendForLocator == InspectionBackend.Auto)
        {
            var autoClient = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
            backendForLocator = autoClient is not null ? InspectionBackend.Wpf : InspectionBackend.Uia;
        }

        if (hasElementId)
        {
            var elementId = request.ElementId!.Trim();
            var handle = RequireHandle(elementId);

            if (request.WindowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            if (handle.Backend == InspectionBackend.Wpf)
            {
                var response = await WaitForWpfAsync(
                    stateText: request.State,
                    state,
                    windowHandle: handle.WindowHandle,
                    locator: null,
                    xpath: handle.XPath,
                    timeoutMs,
                    pollIntervalMs,
                    stableMs,
                    expectedValue: request.ExpectedValue,
                    expectedText: request.ExpectedText,
                    throwOnTimeout: request.ThrowOnTimeout,
                    cancellationToken).ConfigureAwait(false);

                trace?.SetSummary($"{request.State} succeeded={response.Succeeded} attempts={response.Attempts}");
                return response;
            }

            if (handle.Backend != InspectionBackend.Uia)
            {
                throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
            }
        }

        if (hasLocator && backendForLocator == InspectionBackend.Wpf)
        {
            var uiaWindow = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            var hwnd = uiaWindow.Properties.NativeWindowHandle.Value.ToInt64();

            var response = await WaitForWpfAsync(
                stateText: request.State,
                state,
                windowHandle: hwnd,
                locator: request.Locator,
                xpath: null,
                timeoutMs,
                pollIntervalMs,
                stableMs,
                expectedValue: request.ExpectedValue,
                expectedText: request.ExpectedText,
                throwOnTimeout: request.ThrowOnTimeout,
                cancellationToken).ConfigureAwait(false);

            trace?.SetSummary($"{request.State} succeeded={response.Succeeded} attempts={response.Attempts}");
            return response;
        }

        Window window;
        string? xpathHint = null;
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();

        if (hasElementId)
        {
            var elementId = request.ElementId!.Trim();
            var handle = RequireHandle(elementId);

            xpathHint = handle.XPath;

            try
            {
                window = FindWindowByHandle(application, automation, handle.WindowHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{elementId}'. Call resolve_element again.");
            }
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            xpathHint = request.Locator?.XPath;
        }

        var start = Stopwatch.GetTimestamp();
        var attempts = 0;
        WaitForObservation? lastObservation = null;

        Rectangle? lastBounds = null;
        long? stableStartTimestamp = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            AutomationElement? element;
            try
            {
                if (hasElementId)
                {
                    element = ResolveUiaElementById(window, rawWalker, request.ElementId!.Trim(), out _);
                }
                else
                {
                    element = TryResolveWithMissingAsNull(window, request.Locator!, controlWalker, rawWalker, ActionKind.Inspect);
                }
            }
            catch
            {
                throw;
            }

            var satisfied = false;
            string? failureReason = null;

            if (element is null)
            {
                if (state == WaitForState.Attached)
                {
                    failureReason = "not_attached";
                }
                else
                {
                    failureReason = "not_attached";
                }
            }
            else
            {
                lastObservation = BuildWaitObservation(window, element, rawWalker, xpathHint);
                (satisfied, failureReason) = CheckWaitForState(
                    element,
                    state,
                    expectedValue: request.ExpectedValue,
                    expectedText: request.ExpectedText,
                    stableMs: stableMs,
                    ref lastBounds,
                    ref stableStartTimestamp);
            }

            if (satisfied)
            {
                var elapsedMs = (int)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, MidpointRounding.AwayFromZero);
                trace?.SetSummary($"{request.State} succeeded=true attempts={attempts}");
                return new WaitForResponse(
                    Succeeded: true,
                    State: request.State,
                    ElapsedMs: elapsedMs,
                    Attempts: attempts,
                    LastObservation: lastObservation);
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                var elapsedMs = (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
                var response = new WaitForResponse(
                    Succeeded: false,
                    State: request.State,
                    ElapsedMs: elapsedMs,
                    Attempts: attempts,
                    LastObservation: lastObservation,
                    FailureReason: failureReason ?? "timeout");

                if (request.ThrowOnTimeout)
                {
                    throw new InvalidOperationException($"timeout: wait_for state='{request.State}' after {timeoutMs}ms ({failureReason ?? "timeout"}).");
                }

                trace?.SetSummary($"{request.State} succeeded=false attempts={attempts} reason={failureReason ?? "timeout"}");
                return response;
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
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
    }

    private async Task<WaitForResponse> WaitForWpfAsync(
        string stateText,
        WaitForState state,
        long windowHandle,
        ElementLocator? locator,
        string? xpath,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        double? expectedValue,
        string? expectedText,
        bool throwOnTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(xpath) && locator is null)
        {
            throw new ArgumentException("wait_for requires either an elementId (WPF handle) or a locator for backend=wpf.");
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);

        var start = Stopwatch.GetTimestamp();
        var attempts = 0;
        WaitForObservation? lastObservation = null;

        Rect? lastBounds = null;
        long? stableStartTimestamp = null;

        string? currentXPath = string.IsNullOrWhiteSpace(xpath) ? null : NormalizeWpfXPath(xpath);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            var satisfied = false;
            string? failureReason = null;

            if (state == WaitForState.ValueEquals)
            {
                if (expectedValue is null)
                {
                    throw new ArgumentException("expectedValue is required when state=value_equals.");
                }

                if (string.IsNullOrWhiteSpace(currentXPath))
                {
                    try
                    {
                        var resolved = await ResolveWpfElementRefAsync(
                            locator!,
                            windowHandle,
                            visibleOnly: false,
                            includeOffViewport: true,
                            interactiveOnly: false,
                            interactiveMode: InteractiveMode.Heuristic,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        currentXPath = NormalizeWpfXPath(resolved.XPath);
                    }
                    catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
                    {
                        failureReason = "not_attached";
                    }
                }

                if (!string.IsNullOrWhiteSpace(currentXPath))
                {
                    try
                    {
                        var computed = await client.CallAsync<GetComputedPropertiesResponse>(
                            "wpf/get_computed_properties",
                            new GetComputedPropertiesRequest(
                                WindowHandle: windowHandle,
                                Locator: new ElementLocator(XPath: currentXPath),
                                PropertyNames: ["Value", "Text"],
                                IncludeSources: false,
                                IncludeDefault: false,
                                IncludeUnset: true,
                                MaxProperties: 4,
                                ValueFormat: "string"),
                            cancellationToken).ConfigureAwait(false);

                        lastObservation = new WaitForObservation(
                            Type: computed.Element.Type,
                            AutomationId: computed.Element.AutomationId,
                            Name: computed.Element.Name,
                            XPath: computed.Element.XPath,
                            Bounds: computed.Element.Bounds,
                            IsEnabled: null,
                            IsOffscreen: null);

                        (satisfied, failureReason) = CheckWpfComputedValueEquals(computed.Properties, expectedValue.Value);
                    }
                    catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
                    {
                        currentXPath = null;
                        lastObservation = null;
                        failureReason = "not_attached";
                    }
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(currentXPath))
                {
                    try
                    {
                        var resolved = await ResolveWpfElementRefAsync(
                            locator!,
                            windowHandle,
                            visibleOnly: false,
                            includeOffViewport: true,
                            interactiveOnly: false,
                            interactiveMode: InteractiveMode.Heuristic,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        currentXPath = NormalizeWpfXPath(resolved.XPath);

                        if (state == WaitForState.Attached)
                        {
                            lastObservation = new WaitForObservation(
                                Type: resolved.Type,
                                AutomationId: resolved.AutomationId,
                                Name: resolved.Name,
                                XPath: resolved.XPath,
                                Bounds: resolved.Bounds,
                                IsEnabled: null,
                                IsOffscreen: null);

                            satisfied = true;
                        }
                    }
                    catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
                    {
                        failureReason = "not_attached";
                    }
                }

                if (!satisfied && !string.IsNullOrWhiteSpace(currentXPath))
                {
                    TreeNode? node = null;
                    try
                    {
                        var tree = await client.CallAsync<GetVisualTreeResponse>(
                            "wpf/get_visual_tree",
                            new GetWpfVisualTreeRequestV2(
                                WindowHandle: windowHandle,
                                RootXPath: currentXPath,
                                Depth: 1,
                                MaxNodes: 1,
                                VisibleOnly: false,
                                InteractiveOnly: false,
                                InteractiveMode: InteractiveMode.Heuristic,
                                Preset: TreePreset.Standard,
                                Fields: null),
                            cancellationToken).ConfigureAwait(false);

                        node = tree.Root;
                    }
                    catch (InvalidOperationException ex) when (IsWaitableWpfXPathNotFound(ex))
                    {
                        currentXPath = null;
                        lastObservation = null;
                        failureReason = "not_attached";
                    }

                    if (node is not null)
                    {
                        lastObservation = new WaitForObservation(
                            Type: node.Type,
                            AutomationId: node.AutomationId,
                            Name: node.Name,
                            XPath: node.XPath,
                            Bounds: node.Bounds,
                            IsEnabled: node.IsEnabled,
                            IsOffscreen: null);

                        if (state == WaitForState.NameContains)
                        {
                            var computed = await client.CallAsync<GetComputedPropertiesResponse>(
                                "wpf/get_computed_properties",
                                new GetComputedPropertiesRequest(
                                    WindowHandle: windowHandle,
                                    Locator: new ElementLocator(XPath: currentXPath),
                                    PropertyNames: ["Name", "Text", "Content", "Header"],
                                    IncludeSources: false,
                                    IncludeDefault: false,
                                    IncludeUnset: true,
                                    MaxProperties: 8,
                                    ValueFormat: "string"),
                                cancellationToken).ConfigureAwait(false);

                            (satisfied, failureReason) = CheckWpfNameContains(
                                node,
                                computed.Properties,
                                expectedText);
                        }
                        else
                        {
                            (satisfied, failureReason) = CheckWaitForStateWpf(
                                node,
                                state,
                                stableMs,
                                expectedText,
                                ref lastBounds,
                                ref stableStartTimestamp);
                        }
                    }
                }
            }

            if (satisfied)
            {
                var elapsedMs = (int)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, MidpointRounding.AwayFromZero);
                return new WaitForResponse(
                    Succeeded: true,
                    State: stateText,
                    ElapsedMs: elapsedMs,
                    Attempts: attempts,
                    LastObservation: lastObservation);
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                var elapsedMs = (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
                var response = new WaitForResponse(
                    Succeeded: false,
                    State: stateText,
                    ElapsedMs: elapsedMs,
                    Attempts: attempts,
                    LastObservation: lastObservation,
                    FailureReason: failureReason ?? "timeout");

                if (throwOnTimeout)
                {
                    throw new InvalidOperationException($"timeout: wait_for state='{stateText}' after {timeoutMs}ms ({failureReason ?? "timeout"}).");
                }

                return response;
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
    }

    private static string NormalizeWpfXPath(string xpath)
    {
        var trimmed = xpath.Trim();
        while (trimmed.Length > 0 && trimmed.EndsWith("/", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Equals("/Window", StringComparison.OrdinalIgnoreCase))
        {
            return "/Window";
        }

        return trimmed;
    }

    private static bool IsWaitableWpfXPathNotFound(InvalidOperationException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("XPath segment", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Satisfied, string? FailureReason) CheckWaitForStateWpf(
        TreeNode node,
        WaitForState state,
        int stableMs,
        string? expectedText,
        ref Rect? lastBounds,
        ref long? stableStartTimestamp)
    {
        switch (state)
        {
            case WaitForState.Attached:
                return (true, null);
            case WaitForState.Visible:
                if (node.Bounds is null || node.Bounds.Width <= 0 || node.Bounds.Height <= 0)
                {
                    return (false, "invalid_bounds");
                }

                return node.IsVisible == true ? (true, null) : (false, "not_visible");
            case WaitForState.Enabled:
                if (node.IsEnabled is null)
                {
                    return (false, "enabled_unknown");
                }

                return node.IsEnabled.Value ? (true, null) : (false, "disabled");
            case WaitForState.Actionable:
                if (node.Bounds is null || node.Bounds.Width <= 0 || node.Bounds.Height <= 0)
                {
                    return (false, "invalid_bounds");
                }

                if (node.IsVisible != true)
                {
                    return (false, "not_visible");
                }

                if (node.IsEnabled != true)
                {
                    return (false, "disabled");
                }

                return (true, null);
            case WaitForState.Stable:
                return CheckStableBounds(node.Bounds, stableMs, ref lastBounds, ref stableStartTimestamp);
            case WaitForState.NameContains:
                if (string.IsNullOrWhiteSpace(expectedText))
                {
                    return (false, "expected_text_missing");
                }

                var name = node.Name ?? "";
                return name.Contains(expectedText, StringComparison.OrdinalIgnoreCase) ? (true, null) : (false, "name_mismatch");
            case WaitForState.ValueEquals:
                throw new InvalidOperationException("ValueEquals is handled separately for WPF.");
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    private static (bool Satisfied, string? FailureReason) CheckWpfComputedValueEquals(
        IReadOnlyList<ComputedPropertyInfo> properties,
        double expectedValue)
    {
        const double epsilon = 0.01;

        foreach (var name in new[] { "Value", "Text" })
        {
            var match = properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match?.Value is null)
            {
                continue;
            }

            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Abs(parsed - expectedValue) <= epsilon ? (true, null) : (false, "value_mismatch");
            }
        }

        return (false, "value_not_numeric");
    }

    private static (bool Satisfied, string? FailureReason) CheckWpfNameContains(
        TreeNode node,
        IReadOnlyList<ComputedPropertyInfo> properties,
        string? expectedText)
    {
        if (string.IsNullOrWhiteSpace(expectedText))
        {
            return (false, "expected_text_missing");
        }

        if ((node.Name ?? string.Empty).Contains(expectedText, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        foreach (var property in properties)
        {
            if (!string.IsNullOrWhiteSpace(property.Value) &&
                property.Value.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }
        }

        return (false, "name_mismatch");
    }

    private enum WaitForState
    {
        Attached,
        Visible,
        Enabled,
        Actionable,
        Stable,
        ValueEquals,
        NameContains
    }

    private static WaitForState ParseWaitForState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return WaitForState.Visible;
        }

        var value = state.Trim();
        if (value.Equals("attached", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.Attached;
        }

        if (value.Equals("visible", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.Visible;
        }

        if (value.Equals("enabled", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.Enabled;
        }

        if (value.Equals("actionable", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.Actionable;
        }

        if (value.Equals("stable", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.Stable;
        }

        if (value.Equals("value_equals", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("valueEquals", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("valueequals", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.ValueEquals;
        }

        if (value.Equals("name_contains", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("nameContains", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("namecontains", StringComparison.OrdinalIgnoreCase))
        {
            return WaitForState.NameContains;
        }

        throw new ArgumentException($"Unknown wait state '{state}'. Valid values: attached, visible, enabled, actionable, stable, value_equals, name_contains.");
    }

    private static AutomationElement? TryResolveWithMissingAsNull(
        Window window,
        ElementLocator locator,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        ActionKind actionKind,
        bool visibleOnly = false,
        bool includeOffViewport = false,
        bool interactiveOnly = false,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic)
    {
        try
        {
            return ResolveElement(window, locator, controlWalker, rawWalker, actionKind, visibleOnly, includeOffViewport, interactiveOnly, interactiveMode);
        }
        catch (InvalidOperationException ex) when (IsWaitableNotFound(ex))
        {
            return null;
        }
    }

    private static bool IsWaitableNotFound(InvalidOperationException ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("did not match any element", StringComparison.OrdinalIgnoreCase)
               || message.Contains("XPath segment not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("XPath index", StringComparison.OrdinalIgnoreCase);
    }

    private static WaitForObservation BuildWaitObservation(
        Window window,
        AutomationElement element,
        ITreeWalker rawWalker,
        string? xpathHint)
    {
        Rect? bounds = null;
        try
        {
            var rect = element.BoundingRectangle;
            if (rect.Width > 0 && rect.Height > 0)
            {
                bounds = ToRect(rect);
            }
        }
        catch
        {
        }

        var xpath = xpathHint;
        if (string.IsNullOrWhiteSpace(xpath))
        {
            try
            {
                xpath = ComputeXPath(window, element, rawWalker);
            }
            catch
            {
                xpath = null;
            }
        }

        bool? isEnabled = null;
        bool? isOffscreen = null;
        try
        {
            isEnabled = element.Properties.IsEnabled.Value;
        }
        catch
        {
        }

        try
        {
            isOffscreen = element.Properties.IsOffscreen.Value;
        }
        catch
        {
        }

        return new WaitForObservation(
            Type: GetXPathLabel(element),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            Bounds: bounds,
            IsEnabled: isEnabled,
            IsOffscreen: isOffscreen);
    }

    private static (bool Satisfied, string? FailureReason) CheckWaitForState(
        AutomationElement element,
        WaitForState state,
        double? expectedValue,
        string? expectedText,
        int stableMs,
        ref Rectangle? lastBounds,
        ref long? stableStartTimestamp)
    {
        switch (state)
        {
            case WaitForState.Attached:
                return (true, null);
            case WaitForState.Visible:
                if (!HasValidBounds(element))
                {
                    return (false, "invalid_bounds");
                }

                try
                {
                    if (element.Properties.IsOffscreen.Value)
                    {
                        return (false, "offscreen");
                    }
                }
                catch
                {
                    return (false, "offscreen_unknown");
                }

                return (true, null);
            case WaitForState.Enabled:
                try
                {
                    return element.Properties.IsEnabled.Value ? (true, null) : (false, "disabled");
                }
                catch
                {
                    return (false, "enabled_unknown");
                }
            case WaitForState.Actionable:
                if (!HasValidBounds(element))
                {
                    return (false, "invalid_bounds");
                }

                try
                {
                    if (element.Properties.IsOffscreen.Value)
                    {
                        return (false, "offscreen");
                    }
                }
                catch
                {
                    return (false, "offscreen_unknown");
                }

                try
                {
                    if (!element.Properties.IsEnabled.Value)
                    {
                        return (false, "disabled");
                    }
                }
                catch
                {
                    return (false, "enabled_unknown");
                }

                try
                {
                    _ = GetClickPoint(element);
                }
                catch
                {
                    return (false, "no_click_point");
                }

                return (true, null);
            case WaitForState.Stable:
                {
                    Rectangle bounds;
                    try
                    {
                        bounds = element.BoundingRectangle;
                    }
                    catch
                    {
                        lastBounds = null;
                        stableStartTimestamp = null;
                        return (false, "invalid_bounds");
                    }

                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        lastBounds = null;
                        stableStartTimestamp = null;
                        return (false, "invalid_bounds");
                    }

                    if (stableMs <= 0)
                    {
                        return (true, null);
                    }

                    if (lastBounds is null ||
                        bounds.Left != lastBounds.Value.Left ||
                        bounds.Top != lastBounds.Value.Top ||
                        bounds.Width != lastBounds.Value.Width ||
                        bounds.Height != lastBounds.Value.Height)
                    {
                        lastBounds = bounds;
                        stableStartTimestamp = Stopwatch.GetTimestamp();
                        return (false, "unstable");
                    }

                    stableStartTimestamp ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(stableStartTimestamp.Value).TotalMilliseconds >= stableMs)
                    {
                        return (true, null);
                    }

                    return (false, "unstable");
                }
            case WaitForState.ValueEquals:
                {
                    if (expectedValue is null)
                    {
                        return (false, "expected_value_missing");
                    }

                    var target = expectedValue.Value;
                    var epsilon = 0.01;

                    var rangeValue = element.Patterns.RangeValue.PatternOrDefault;
                    if (rangeValue is not null)
                    {
                        var current = rangeValue.Value;
                        return Math.Abs(current - target) <= epsilon ? (true, null) : (false, "value_mismatch");
                    }

                    var valuePattern = element.Patterns.Value.PatternOrDefault;
                    if (valuePattern is not null)
                    {
                        var s = valuePattern.Value ?? "";
                        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        {
                            return Math.Abs(parsed - target) <= epsilon ? (true, null) : (false, "value_mismatch");
                        }

                        return (false, "value_not_numeric");
                    }

                    return (false, "no_value_pattern");
                }
            case WaitForState.NameContains:
                {
                    if (string.IsNullOrWhiteSpace(expectedText))
                    {
                        return (false, "expected_text_missing");
                    }

                    var name = GetName(element) ?? "";
                    return name.Contains(expectedText, StringComparison.OrdinalIgnoreCase) ? (true, null) : (false, "name_mismatch");
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    private static async Task<AutomationElement> ResolveUiaElementWithWaitAsync(
        Window window,
        ElementLocator locator,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        return await ResolveUiaElementWithWaitAsync(
            window,
            locator,
            controlWalker,
            rawWalker,
            timeoutMs,
            pollIntervalMs,
            ActionKind.Inspect,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AutomationElement> ResolveUiaElementWithWaitAsync(
        Window window,
        ElementLocator locator,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        int timeoutMs,
        int pollIntervalMs,
        ActionKind actionKind,
        CancellationToken cancellationToken)
    {
        return await ResolveUiaElementWithWaitAsync(
            window,
            locator,
            controlWalker,
            rawWalker,
            timeoutMs,
            pollIntervalMs,
            actionKind,
            visibleOnly: false,
            includeOffViewport: false,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AutomationElement> ResolveUiaElementWithWaitAsync(
        Window window,
        ElementLocator locator,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        int timeoutMs,
        int pollIntervalMs,
        ActionKind actionKind,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var element = TryResolveWithMissingAsNull(
                window,
                locator,
                controlWalker,
                rawWalker,
                actionKind,
                visibleOnly,
                includeOffViewport,
                interactiveOnly,
                interactiveMode);
            if (element is not null)
            {
                return element;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
                if (elapsed.TotalMilliseconds >= timeoutMs)
                {
                    var hint = visibleOnly && !includeOffViewport
                        ? " Retry with includeOffViewport=true, visibleOnly=false for hidden elements, or call scroll_to_element first."
                        : "";
                    throw new InvalidOperationException($"timeout: element not found after {timeoutMs}ms.{hint}");
                }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
    }

    private static async Task WaitForResolvedElementStateAsync(
        AutomationElement element,
        WaitForState state,
        int timeoutMs,
        int pollIntervalMs,
        int stableMs,
        double? expectedValue,
        string? expectedText,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        Rectangle? lastBounds = null;
        long? stableStartTimestamp = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (ok, reason) = CheckWaitForState(
                element,
                state,
                expectedValue,
                expectedText,
                stableMs,
                ref lastBounds,
                ref stableStartTimestamp);

            if (ok)
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                throw new InvalidOperationException($"timeout: wait_for state='{state}' after {timeoutMs}ms ({reason ?? "timeout"}).");
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
    }

    private static async Task WaitForValuePatternTextAsync(
        IValuePattern valuePattern,
        string expected,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valuePattern);
        expected ??= "";

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? current;
            try
            {
                current = valuePattern.Value;
            }
            catch
            {
                current = null;
            }

            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                var elapsedMs = (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
                throw new InvalidOperationException($"timeout: value did not update after {elapsedMs}ms.");
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
    }

    private static async Task WaitForRangeValueAsync(
        IRangeValuePattern rangeValue,
        double expected,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rangeValue);

        var start = Stopwatch.GetTimestamp();
        var epsilon = 0.01;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double current;
            try
            {
                current = rangeValue.Value;
            }
            catch
            {
                current = double.NaN;
            }

            if (!double.IsNaN(current) && Math.Abs(current - expected) <= epsilon)
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                var elapsedMs = (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
                throw new InvalidOperationException($"timeout: range value did not update after {elapsedMs}ms.");
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }
    }

    private static FlaUI.Core.Input.MouseButton ParseMouseButton(string? button)
    {
        if (string.IsNullOrWhiteSpace(button))
        {
            return FlaUI.Core.Input.MouseButton.Left;
        }

        var value = button.Trim();
        if (value.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            return FlaUI.Core.Input.MouseButton.Left;
        }

        if (value.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("secondary", StringComparison.OrdinalIgnoreCase))
        {
            return FlaUI.Core.Input.MouseButton.Right;
        }

        if (value.Equals("middle", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("wheel", StringComparison.OrdinalIgnoreCase))
        {
            return FlaUI.Core.Input.MouseButton.Middle;
        }

        throw new ArgumentException($"Unknown mouse button '{button}'. Valid values: left, right, middle.");
    }

    private static Point GetDragPoint(AutomationElement element)
    {
        var bounds = element.BoundingRectangle;

        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            !IsSaneMouseCoordinate(bounds.Left) ||
            !IsSaneMouseCoordinate(bounds.Top))
        {
            throw new InvalidOperationException("Element has invalid bounds; cannot compute drag coordinates.");
        }

        if (element.TryGetClickablePoint(out var clickable) &&
            IsSaneMousePoint(clickable) &&
            IsPointNearBounds(clickable, bounds, margin: 48))
        {
            return clickable;
        }

        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;

        if (!IsSaneMouseCoordinate(centerX) || !IsSaneMouseCoordinate(centerY))
        {
            throw new InvalidOperationException("Element center point is not a sane screen coordinate.");
        }

        return new Point(centerX, centerY);
    }

    private static async Task<bool> TrySetValueByDraggingAsync(
        AutomationElement element,
        ITreeWalker rawWalker,
        double value,
        bool autoWait,
        int timeoutMs,
        int pollIntervalMs,
        int steps,
        CancellationToken cancellationToken)
    {
        if (steps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), steps, "steps must be > 0.");
        }

        try
        {
            if (!TryFindNearestRangeValueElement(element, rawWalker, out var rangeElement, out var rangeValue))
            {
                return false;
            }

            double min;
            double max;
            try
            {
                min = rangeValue.Minimum;
                max = rangeValue.Maximum;
            }
            catch
            {
                return false;
            }

            if (max <= min)
            {
                return false;
            }

            Rectangle trackBounds;
            try
            {
                trackBounds = rangeElement.BoundingRectangle;
            }
            catch
            {
                return false;
            }

            if (trackBounds.Width <= 0 || trackBounds.Height <= 0)
            {
                return false;
            }

            var orientationElement = rangeElement;
            if (rangeElement.ControlType == ControlType.Thumb)
            {
                try
                {
                    var parent = rawWalker.GetParent(rangeElement);
                    if (parent is not null)
                    {
                        var parentBounds = parent.BoundingRectangle;
                        if (parentBounds.Width > 0 &&
                            parentBounds.Height > 0 &&
                            (parentBounds.Width >= trackBounds.Width || parentBounds.Height >= trackBounds.Height))
                        {
                            trackBounds = parentBounds;
                            orientationElement = parent;
                        }
                    }
                }
                catch
                {
                }
            }

            var horizontal = IsHorizontal(orientationElement, trackBounds);

            var fraction = (value - min) / (max - min);
            fraction = Math.Clamp(fraction, 0, 1);

            const int paddingPx = 4;

            var thumbs = FindThumbCandidates(element, orientationElement, rawWalker);
            if (thumbs.Count == 0)
            {
                return false;
            }

            int targetCoord;
            int targetX;
            int targetY;
            if (horizontal)
            {
                var usableWidth = Math.Max(1, trackBounds.Width - 2 * paddingPx);
                targetX = trackBounds.Left + paddingPx + (int)Math.Round(fraction * usableWidth, MidpointRounding.AwayFromZero);
                targetY = trackBounds.Top + trackBounds.Height / 2;
                targetCoord = targetX;
            }
            else
            {
                var usableHeight = Math.Max(1, trackBounds.Height - 2 * paddingPx);
                targetY = trackBounds.Bottom - paddingPx - (int)Math.Round(fraction * usableHeight, MidpointRounding.AwayFromZero);
                targetX = trackBounds.Left + trackBounds.Width / 2;
                targetCoord = targetY;
            }

            var thumbToDrag = PickClosestThumb(thumbs, horizontal, targetCoord);
            EnsureEnabledOrThrow(thumbToDrag, "set_value");
            TryScrollIntoView(thumbToDrag);

            var start = GetDragPoint(thumbToDrag);
            var end = horizontal ? new Point(targetX, start.Y) : new Point(start.X, targetY);

            Mouse.MoveTo(start);
            await Task.Delay(1, cancellationToken);

            try
            {
                Mouse.Down(FlaUI.Core.Input.MouseButton.Left);
                await Task.Delay(1, cancellationToken);

                for (var step = 1; step <= steps; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var t = step / (double)steps;
                    var x = start.X + (int)Math.Round((end.X - start.X) * t, MidpointRounding.AwayFromZero);
                    var y = start.Y + (int)Math.Round((end.Y - start.Y) * t, MidpointRounding.AwayFromZero);

                    Mouse.MoveTo(new Point(x, y));
                    if (step < steps)
                    {
                        await Task.Delay(1, cancellationToken);
                    }
                }
            }
            finally
            {
                try
                {
                    Mouse.Up(FlaUI.Core.Input.MouseButton.Left);
                }
                catch
                {
                }
            }

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            if (autoWait)
            {
                var verify = thumbToDrag.Patterns.RangeValue.PatternOrDefault ?? rangeValue;
                try
                {
                    await WaitForRangeValueAsync(verify, expected: value, timeoutMs, pollIntervalMs, cancellationToken);
                }
                catch
                {
                    // Best-effort; some custom controls expose unreliable RangeValue patterns even though dragging updates visuals.
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindNearestRangeValueElement(
        AutomationElement start,
        ITreeWalker rawWalker,
        out AutomationElement rangeElement,
        out IRangeValuePattern rangeValue)
    {
        rangeElement = start;
        rangeValue = null!;

        AutomationElement? fallbackElement = null;
        IRangeValuePattern? fallbackPattern = null;

        AutomationElement? current = start;
        for (var i = 0; i < 60 && current is not null; i++)
        {
            try
            {
                var pattern = current.Patterns.RangeValue.PatternOrDefault;
                if (pattern is not null)
                {
                    fallbackElement ??= current;
                    fallbackPattern ??= pattern;

                    if (current.ControlType != ControlType.Thumb)
                    {
                        rangeElement = current;
                        rangeValue = pattern;
                        return true;
                    }
                }
            }
            catch
            {
            }

            try
            {
                current = rawWalker.GetParent(current);
            }
            catch
            {
                current = null;
            }
        }

        if (fallbackElement is not null && fallbackPattern is not null)
        {
            rangeElement = fallbackElement;
            rangeValue = fallbackPattern;
            return true;
        }

        return false;
    }

    private static bool IsHorizontal(AutomationElement element, Rectangle bounds)
    {
        try
        {
            var orientation = element.Properties.Orientation.Value;
            if (orientation == OrientationType.Horizontal)
            {
                return true;
            }

            if (orientation == OrientationType.Vertical)
            {
                return false;
            }
        }
        catch
        {
        }

        return bounds.Width >= bounds.Height;
    }

    private static List<AutomationElement> FindThumbCandidates(AutomationElement element, AutomationElement rangeElement, ITreeWalker rawWalker)
    {
        var thumbs = new List<AutomationElement>(capacity: 4);

        if (element.ControlType == ControlType.Thumb)
        {
            thumbs.Add(element);
            return thumbs;
        }

        FindThumbDescendants(element, rawWalker, thumbs, maxNodesToScan: 5000, maxThumbs: 8);
        if (thumbs.Count == 0 && !ReferenceEquals(element, rangeElement))
        {
            FindThumbDescendants(rangeElement, rawWalker, thumbs, maxNodesToScan: 5000, maxThumbs: 8);
        }

        return thumbs;
    }

    private static void FindThumbDescendants(
        AutomationElement root,
        ITreeWalker rawWalker,
        List<AutomationElement> thumbs,
        int maxNodesToScan,
        int maxThumbs)
    {
        var scanned = 0;
        foreach (var descendant in EnumerateSelfAndDescendantsDepthFirst(root, rawWalker))
        {
            scanned++;
            if (scanned > maxNodesToScan || thumbs.Count >= maxThumbs)
            {
                return;
            }

            if (descendant.ControlType == ControlType.Thumb)
            {
                thumbs.Add(descendant);
            }
        }
    }

    private static bool HasMultipleThumbDescendants(AutomationElement root, ITreeWalker rawWalker, int maxNodesToScan)
    {
        var scanned = 0;
        var count = 0;

        foreach (var descendant in EnumerateSelfAndDescendantsDepthFirst(root, rawWalker))
        {
            scanned++;
            if (scanned > maxNodesToScan)
            {
                return false;
            }

            if (descendant.ControlType == ControlType.Thumb && ++count >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static AutomationElement PickClosestThumb(IReadOnlyList<AutomationElement> thumbs, bool horizontal, int targetCoord)
    {
        AutomationElement? best = null;
        var bestDistance = long.MaxValue;

        foreach (var thumb in thumbs)
        {
            Rectangle bounds;
            try
            {
                bounds = thumb.BoundingRectangle;
            }
            catch
            {
                continue;
            }

            var center = horizontal ? bounds.Left + bounds.Width / 2 : bounds.Top + bounds.Height / 2;
            var distance = Math.Abs((long)center - targetCoord);
            if (best is null || distance < bestDistance)
            {
                best = thumb;
                bestDistance = distance;
            }
        }

        return best ?? thumbs[0];
    }

    private static bool IsSaneMousePoint(Point point) =>
        IsSaneMouseCoordinate(point.X) && IsSaneMouseCoordinate(point.Y);

    private static bool IsSaneMouseCoordinate(int value) =>
        value >= -1_000_000 &&
        value <= 1_000_000;

    private static bool IsPointNearBounds(Point point, Rectangle bounds, int margin)
    {
        return point.X >= bounds.Left - margin &&
               point.X <= bounds.Right + margin &&
               point.Y >= bounds.Top - margin &&
               point.Y <= bounds.Bottom + margin;
    }

    private static AutomationElement ResolveElementWithinRoot(AutomationElement root, ElementLocator locator, ITreeWalker walker)
    {
        return TryResolveElementWithinRoot(root, locator, walker)
            ?? throw new InvalidOperationException("itemLocator did not match any element under the selection container.");
    }

    private static async Task<(AutomationElement Element, bool Scrolled)> ResolveElementWithinContainerOrScrollAsync(
        AutomationElement container,
        ElementLocator locator,
        ITreeWalker walker,
        CancellationToken cancellationToken)
    {
        var resolved = TryResolveElementWithinRoot(container, locator, walker);
        if (resolved is not null)
        {
            return (resolved, false);
        }

        if (!TryGetScrollable(container, walker, out var scrollElement))
        {
            throw new InvalidOperationException(
                "Locator did not match any element under the container and the container is not scrollable. Consider a different containerLocator.");
        }

        var scroll = scrollElement.Patterns.Scroll.PatternOrDefault;
        if (scroll is null || !scroll.VerticallyScrollable)
        {
            throw new InvalidOperationException(
                "Locator did not match any element under the container and the container is not vertically scrollable. Consider a different containerLocator.");
        }

        try
        {
            var horizontal = scroll.HorizontallyScrollable ? scroll.HorizontalScrollPercent : -1d;
            scroll.SetScrollPercent(horizontal, 0);
        }
        catch
        {
        }

        await Task.Delay(UiDelayScrollMs, cancellationToken);

        var maxScrollSteps = 50;
        double? lastPercent = null;
        for (var step = 0; step <= maxScrollSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            resolved = TryResolveElementWithinRoot(container, locator, walker);
            if (resolved is not null)
            {
                return (resolved, true);
            }

            var beforePercent = TryGetScrollPercent(scroll, vertical: true);
            if (beforePercent is not null && beforePercent >= 100)
            {
                break;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            try
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            }
            catch
            {
                break;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            var afterPercent = TryGetScrollPercent(scroll, vertical: true);
            if (afterPercent is not null && lastPercent is not null && Math.Abs(afterPercent.Value - lastPercent.Value) < 0.0001)
            {
                break;
            }

            lastPercent = afterPercent;
        }

        throw new InvalidOperationException(
            "Locator did not match any element under the container (after scrolling). Consider refining the locator.");
    }

    private static async Task<(string MethodUsed, bool Scrolled)> ScrollElementIntoViewAsync(
        AutomationElement? preferredContainer,
        AutomationElement element,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        CancellationToken cancellationToken)
    {
        AutomationElement? scrollElement = null;
        IScrollPattern? scrollPattern = null;

        if (preferredContainer is not null &&
            TryGetScrollableAny(preferredContainer, controlWalker, out scrollElement, out scrollPattern))
        {
        }
        else if (TryGetScrollableAncestor(element, rawWalker, out scrollElement, out scrollPattern))
        {
        }

        var scrollTarget = scrollElement is not null
            ? GetScrollTargetElement(scrollElement, element, rawWalker, controlWalker)
            : element;

        var needsScroll = scrollTarget.IsOffscreen ||
            (scrollElement is not null && IsElementOutsideViewport(scrollElement, scrollTarget));

        if (!needsScroll)
        {
            return ("alreadyVisible", false);
        }

        try
        {
            var scrollItem = element.Patterns.ScrollItem.PatternOrDefault;
            if (scrollItem is not null)
            {
                scrollItem.ScrollIntoView();
                await Task.Delay(UiDelayScrollMs, cancellationToken);

                needsScroll = scrollTarget.IsOffscreen ||
                    (scrollElement is not null && IsElementOutsideViewport(scrollElement, scrollTarget));

                if (!needsScroll)
                {
                    return ("scrollItem", true);
                }
            }
        }
        catch
        {
        }

        try
        {
            if (TryScrollItemIntoViewFromAncestors(element, rawWalker))
            {
                await Task.Delay(UiDelayScrollMs, cancellationToken);

                needsScroll = scrollTarget.IsOffscreen ||
                    (scrollElement is not null && IsElementOutsideViewport(scrollElement, scrollTarget));

                if (!needsScroll)
                {
                    return ("scrollItem", true);
                }
            }
        }
        catch
        {
        }

        if (scrollElement is null || scrollPattern is null)
        {
            if (!TryGetScrollableAncestor(element, rawWalker, out scrollElement, out scrollPattern))
            {
                throw new InvalidOperationException(
                    "Failed to scroll element into view because no scrollable container was found (no ScrollItemPattern and no ScrollPattern).");
            }
        }

        scrollTarget = GetScrollTargetElement(scrollElement, element, rawWalker, controlWalker);
        await ScrollPatternBringIntoViewAsync(scrollElement, scrollPattern, scrollTarget, cancellationToken);
        return ("scrollPattern", true);
    }

    private static AutomationElement GetScrollTargetElement(
        AutomationElement scrollElement,
        AutomationElement element,
        ITreeWalker rawWalker,
        ITreeWalker controlWalker)
    {
        if (HasValidBounds(element))
        {
            return element;
        }

        var current = TryGetParent(rawWalker, controlWalker, element);
        if (current is null)
        {
            return element;
        }

        for (var step = 0; step < 30 && current is not null; step++)
        {
            if (AreSameElement(current, scrollElement))
            {
                break;
            }

            if (HasValidBounds(current) && IsElementOutsideViewport(scrollElement, current))
            {
                return current;
            }

            current = TryGetParent(rawWalker, controlWalker, current);
        }

        return element;
    }

    private static AutomationElement? TryGetParent(ITreeWalker rawWalker, ITreeWalker controlWalker, AutomationElement element)
    {
        try
        {
            return rawWalker.GetParent(element);
        }
        catch
        {
        }

        try
        {
            return controlWalker.GetParent(element);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasValidBounds(AutomationElement element)
    {
        try
        {
            var bounds = element.BoundingRectangle;
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureEnabledOrThrow(AutomationElement element, string actionName)
    {
        bool isEnabled;
        try
        {
            isEnabled = element.Properties.IsEnabled.Value;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"element_enabled_unknown: action={actionName} (ControlType={element.ControlType}, AutomationId={GetAutomationId(element)}, Name={GetName(element)}).",
                ex);
        }

        if (!isEnabled)
        {
            throw new InvalidOperationException(
                $"element_disabled: action={actionName} (ControlType={element.ControlType}, AutomationId={GetAutomationId(element)}, Name={GetName(element)}).");
        }
    }

    private static Exception WrapUiaActionException(Exception exception, string actionName, AutomationElement element)
    {
        if (exception is COMException comException)
        {
            return new InvalidOperationException(
                $"uia_action_failed: action={actionName} hresult=0x{comException.HResult:X8} (ControlType={element.ControlType}, AutomationId={GetAutomationId(element)}, Name={GetName(element)}).",
                comException);
        }

        return exception;
    }

    private static bool TryScrollItemIntoViewFromAncestors(AutomationElement element, ITreeWalker rawWalker)
    {
        AutomationElement? current;
        try
        {
            current = rawWalker.GetParent(element);
        }
        catch
        {
            return false;
        }

        for (var step = 0; step < 200 && current is not null; step++)
        {
            try
            {
                var scrollItem = current.Patterns.ScrollItem.PatternOrDefault;
                if (scrollItem is not null)
                {
                    scrollItem.ScrollIntoView();
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                current = rawWalker.GetParent(current);
            }
            catch
            {
                current = null;
            }
        }

        return false;
    }

    private static async Task ScrollPatternBringIntoViewAsync(
        AutomationElement scrollElement,
        IScrollPattern scrollPattern,
        AutomationElement element,
        CancellationToken cancellationToken)
    {
        var maxScrollSteps = 60;
        const double tolerancePx = 1;
        double? lastVertical = null;
        double? lastHorizontal = null;
        var percentScan = (Attempted: false, Succeeded: false);

        for (var step = 0; step < maxScrollSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsElementOutsideViewport(scrollElement, element))
            {
                return;
            }

            var containerBounds = scrollElement.BoundingRectangle;
            var elementBounds = element.BoundingRectangle;

            if (elementBounds.Width <= 0 || elementBounds.Height <= 0)
            {
                break;
            }

            var vertical = ScrollAmount.NoAmount;
            var horizontal = ScrollAmount.NoAmount;

            if (scrollPattern.VerticallyScrollable)
            {
                var oversizeY = elementBounds.Height > containerBounds.Height + tolerancePx;
                var yNeedsScroll = oversizeY
                    ? elementBounds.Bottom <= containerBounds.Top + tolerancePx ||
                      elementBounds.Top >= containerBounds.Bottom - tolerancePx
                    : elementBounds.Top < containerBounds.Top + tolerancePx ||
                      elementBounds.Bottom > containerBounds.Bottom - tolerancePx;

                if (yNeedsScroll && elementBounds.Top < containerBounds.Top + tolerancePx)
                {
                    vertical = ScrollAmount.LargeDecrement;
                }
                else if (yNeedsScroll && elementBounds.Bottom > containerBounds.Bottom - tolerancePx)
                {
                    vertical = ScrollAmount.LargeIncrement;
                }
            }

            if (scrollPattern.HorizontallyScrollable)
            {
                var oversizeX = elementBounds.Width > containerBounds.Width + tolerancePx;
                var xNeedsScroll = oversizeX
                    ? elementBounds.Right <= containerBounds.Left + tolerancePx ||
                      elementBounds.Left >= containerBounds.Right - tolerancePx
                    : elementBounds.Left < containerBounds.Left + tolerancePx ||
                      elementBounds.Right > containerBounds.Right - tolerancePx;

                if (xNeedsScroll && elementBounds.Left < containerBounds.Left + tolerancePx)
                {
                    horizontal = ScrollAmount.LargeDecrement;
                }
                else if (xNeedsScroll && elementBounds.Right > containerBounds.Right - tolerancePx)
                {
                    horizontal = ScrollAmount.LargeIncrement;
                }
            }

            if (vertical == ScrollAmount.NoAmount && horizontal == ScrollAmount.NoAmount)
            {
                break;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            try
            {
                scrollPattern.Scroll(horizontal, vertical);
            }
            catch
            {
                break;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            if (scrollPattern.VerticallyScrollable)
            {
                var percent = TryGetScrollPercent(scrollPattern, vertical: true);
                if (percent is not null && lastVertical is not null && Math.Abs(percent.Value - lastVertical.Value) < 0.0001)
                {
                    break;
                }

                lastVertical = percent;
            }

            if (scrollPattern.HorizontallyScrollable)
            {
                var percent = TryGetScrollPercent(scrollPattern, vertical: false);
                if (percent is not null && lastHorizontal is not null && Math.Abs(percent.Value - lastHorizontal.Value) < 0.0001)
                {
                    break;
                }

                lastHorizontal = percent;
            }
        }

        if (!IsElementOutsideViewport(scrollElement, element))
        {
            return;
        }

        percentScan = await TryScrollPatternPercentScanAsync(scrollElement, scrollPattern, element, cancellationToken);

        if (IsElementOutsideViewport(scrollElement, element))
        {
            var containerBounds = SafeGetRect(() => scrollElement.BoundingRectangle);
            var elementBounds = SafeGetRect(() => element.BoundingRectangle);
            var elementOffscreen = SafeGetBool(() => element.IsOffscreen);
            var elementType = SafeGetString(() => element.ControlType.ToString());
            var elementAutomationId = SafeGetString(() => GetAutomationId(element));
            var elementName = SafeGetString(() => GetName(element));
            var elementClass = SafeGetString(() => GetClassName(element));
            var verticalPercent = TryGetScrollPercent(scrollPattern, vertical: true);
            var horizontalPercent = TryGetScrollPercent(scrollPattern, vertical: false);

            throw new InvalidOperationException(
                "Failed to scroll element into view. " +
                $"percentScanAttempted={percentScan.Attempted}, " +
                $"percentScanSucceeded={percentScan.Succeeded}, " +
                $"elementType={elementType}, " +
                $"elementAutomationId={elementAutomationId}, " +
                $"elementName={elementName}, " +
                $"elementClass={elementClass}, " +
                $"elementIsOffscreen={elementOffscreen}, " +
                $"elementBounds={FormatRect(elementBounds)}, " +
                $"containerBounds={FormatRect(containerBounds)}, " +
                $"verticalPercent={FormatPercent(verticalPercent)}, " +
                $"horizontalPercent={FormatPercent(horizontalPercent)}.");
        }
    }

    private static async Task<(bool Attempted, bool Succeeded)> TryScrollPatternPercentScanAsync(
        AutomationElement scrollElement,
        IScrollPattern scrollPattern,
        AutomationElement element,
        CancellationToken cancellationToken)
    {
        var attempted = false;

        if (scrollPattern.VerticallyScrollable)
        {
            attempted = true;
            if (await TryScrollPatternPercentScanAxisAsync(scrollElement, scrollPattern, element, vertical: true, toStartPercent: 0, ScrollAmount.LargeIncrement, cancellationToken))
            {
                return (Attempted: true, Succeeded: true);
            }

            if (await TryScrollPatternPercentScanAxisAsync(scrollElement, scrollPattern, element, vertical: true, toStartPercent: 100, ScrollAmount.LargeDecrement, cancellationToken))
            {
                return (Attempted: true, Succeeded: true);
            }
        }

        if (scrollPattern.HorizontallyScrollable)
        {
            attempted = true;
            if (await TryScrollPatternPercentScanAxisAsync(scrollElement, scrollPattern, element, vertical: false, toStartPercent: 0, ScrollAmount.LargeIncrement, cancellationToken))
            {
                return (Attempted: true, Succeeded: true);
            }

            if (await TryScrollPatternPercentScanAxisAsync(scrollElement, scrollPattern, element, vertical: false, toStartPercent: 100, ScrollAmount.LargeDecrement, cancellationToken))
            {
                return (Attempted: true, Succeeded: true);
            }
        }

        return (Attempted: attempted, Succeeded: attempted && !IsElementOutsideViewport(scrollElement, element));
    }

    private static async Task<bool> TryScrollPatternPercentScanAxisAsync(
        AutomationElement scrollElement,
        IScrollPattern scrollPattern,
        AutomationElement element,
        bool vertical,
        double toStartPercent,
        ScrollAmount scrollStep,
        CancellationToken cancellationToken)
    {
        if (!IsElementOutsideViewport(scrollElement, element))
        {
            return true;
        }

        try
        {
            if (vertical)
            {
                scrollPattern.SetScrollPercent(-1, toStartPercent);
            }
            else
            {
                scrollPattern.SetScrollPercent(toStartPercent, -1);
            }
        }
        catch
        {
        }

        await Task.Delay(UiDelayScrollMs, cancellationToken);

        var maxScanSteps = 80;
        double? lastPercent = TryGetScrollPercent(scrollPattern, vertical);

        for (var step = 0; step < maxScanSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsElementOutsideViewport(scrollElement, element))
            {
                return true;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            var scrolled = TryScrollPatternOnce(scrollPattern, vertical, scrollStep);
            if (!scrolled && scrollStep == ScrollAmount.LargeIncrement)
            {
                scrolled = TryScrollPatternOnce(scrollPattern, vertical, ScrollAmount.SmallIncrement);
            }
            else if (!scrolled && scrollStep == ScrollAmount.LargeDecrement)
            {
                scrolled = TryScrollPatternOnce(scrollPattern, vertical, ScrollAmount.SmallDecrement);
            }

            if (!scrolled)
            {
                break;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            var percent = TryGetScrollPercent(scrollPattern, vertical);
            if (percent is not null && lastPercent is not null && Math.Abs(percent.Value - lastPercent.Value) < 0.0001)
            {
                break;
            }

            lastPercent = percent;
        }

        if (!IsElementOutsideViewport(scrollElement, element))
        {
            return true;
        }

        var viewSize = GetScrollViewSize(scrollPattern, vertical);
        var stepPercent = Math.Clamp(viewSize * 0.8, 2, 20);
        var increment = scrollStep is ScrollAmount.LargeIncrement or ScrollAmount.SmallIncrement;

        var maxPercentSteps = 60;
        var current = toStartPercent;
        for (var step = 0; step < maxPercentSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsElementOutsideViewport(scrollElement, element))
            {
                return true;
            }

            if (step > 0)
            {
                current = increment
                    ? Math.Min(100, current + stepPercent)
                    : Math.Max(0, current - stepPercent);
            }

            try
            {
                if (vertical)
                {
                    scrollPattern.SetScrollPercent(-1, current);
                }
                else
                {
                    scrollPattern.SetScrollPercent(current, -1);
                }
            }
            catch
            {
                break;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            if ((increment && current >= 100) || (!increment && current <= 0))
            {
                break;
            }
        }

        return !IsElementOutsideViewport(scrollElement, element);
    }

    private static bool TryScrollPatternOnce(IScrollPattern scrollPattern, bool vertical, ScrollAmount amount)
    {
        try
        {
            if (vertical)
            {
                scrollPattern.Scroll(ScrollAmount.NoAmount, amount);
            }
            else
            {
                scrollPattern.Scroll(amount, ScrollAmount.NoAmount);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double GetScrollViewSize(IScrollPattern scrollPattern, bool vertical)
    {
        try
        {
            var viewSize = vertical ? scrollPattern.VerticalViewSize : scrollPattern.HorizontalViewSize;
            if (double.IsNaN(viewSize) || viewSize <= 0 || viewSize > 100)
            {
                return 10;
            }

            return viewSize;
        }
        catch
        {
            return 10;
        }
    }

    private static System.Drawing.Rectangle SafeGetRect(Func<System.Drawing.Rectangle> factory)
    {
        try
        {
            return factory();
        }
        catch
        {
            return default;
        }
    }

    private static bool? SafeGetBool(Func<bool> factory)
    {
        try
        {
            return factory();
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetString(Func<string?> factory)
    {
        try
        {
            return factory();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatRect(System.Drawing.Rectangle rect) =>
        rect.Width <= 0 && rect.Height <= 0
            ? "empty"
            : $"x={rect.Left},y={rect.Top},w={rect.Width},h={rect.Height}";

    private static string FormatPercent(double? percent) =>
        percent is null ? "unknown" : percent.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool IsElementOutsideViewport(AutomationElement scrollElement, AutomationElement element)
    {
        try
        {
            const double tolerancePx = 1;
            var containerBounds = scrollElement.BoundingRectangle;
            var elementBounds = element.BoundingRectangle;

            if (containerBounds.Width <= 0 || containerBounds.Height <= 0 ||
                elementBounds.Width <= 0 || elementBounds.Height <= 0)
            {
                return element.IsOffscreen;
            }

            if (element.IsOffscreen)
            {
                return true;
            }

            var oversizeX = elementBounds.Width > containerBounds.Width + tolerancePx;
            var oversizeY = elementBounds.Height > containerBounds.Height + tolerancePx;

            var xVisibleEnough = oversizeX
                ? elementBounds.Right > containerBounds.Left + tolerancePx &&
                  elementBounds.Left < containerBounds.Right - tolerancePx
                : elementBounds.Left >= containerBounds.Left - tolerancePx &&
                  elementBounds.Right <= containerBounds.Right + tolerancePx;

            var yVisibleEnough = oversizeY
                ? elementBounds.Bottom > containerBounds.Top + tolerancePx &&
                  elementBounds.Top < containerBounds.Bottom - tolerancePx
                : elementBounds.Top >= containerBounds.Top - tolerancePx &&
                  elementBounds.Bottom <= containerBounds.Bottom + tolerancePx;

            return !(xVisibleEnough && yVisibleEnough);
        }
        catch
        {
            return element.IsOffscreen;
        }
    }

    private static bool TryGetScrollableAny(
        AutomationElement root,
        ITreeWalker walker,
        out AutomationElement scrollElement,
        out IScrollPattern scrollPattern)
    {
        scrollElement = null!;
        scrollPattern = null!;

        foreach (var element in EnumerateSelfAndDescendantsDepthFirst(root, walker))
        {
            try
            {
                var scroll = element.Patterns.Scroll.PatternOrDefault;
                if (scroll is not null && (scroll.VerticallyScrollable || scroll.HorizontallyScrollable))
                {
                    scrollElement = element;
                    scrollPattern = scroll;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static bool TryGetScrollableAncestor(
        AutomationElement element,
        ITreeWalker walker,
        out AutomationElement scrollElement,
        out IScrollPattern scrollPattern)
    {
        scrollElement = null!;
        scrollPattern = null!;

        AutomationElement? current = element;
        for (var step = 0; step < 200 && current is not null; step++)
        {
            try
            {
                var scroll = current.Patterns.Scroll.PatternOrDefault;
                if (scroll is not null && (scroll.VerticallyScrollable || scroll.HorizontallyScrollable))
                {
                    scrollElement = current;
                    scrollPattern = scroll;
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                current = walker.GetParent(current);
            }
            catch
            {
                current = null;
            }
        }

        return false;
    }

    private static AutomationElement? TryResolveElementWithinRoot(AutomationElement root, ElementLocator locator, ITreeWalker walker)
    {
        if (locator is null)
        {
            throw new ArgumentNullException(nameof(locator));
        }

        var descendants = EnumerateSelfAndDescendantsDepthFirst(root, walker).Skip(1).ToArray();

        if (!string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            var matches = descendants
                .Where(e => string.Equals(GetAutomationId(e), locator.AutomationId, StringComparison.Ordinal))
                .ToArray();

            var resolved = SelectMatchForItemLocator(matches, root, locator, "automationId");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.Name))
        {
            var matches = descendants
                .Where(e => string.Equals(GetName(e), locator.Name, StringComparison.Ordinal))
                .ToArray();

            var resolved = SelectMatchForItemLocator(matches, root, locator, "name");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName))
        {
            var matches = descendants
                .Where(e => string.Equals(GetClassName(e), locator.ClassName, StringComparison.Ordinal))
                .ToArray();

            var resolved = SelectMatchForItemLocator(matches, root, locator, "className");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (locator.Index is int index &&
            string.IsNullOrWhiteSpace(locator.AutomationId) &&
            string.IsNullOrWhiteSpace(locator.Name) &&
            string.IsNullOrWhiteSpace(locator.ClassName) &&
            string.IsNullOrWhiteSpace(locator.XPath))
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            if (index >= descendants.Length)
            {
                return null;
            }

            return descendants[index];
        }

        return null;
    }

    private static AutomationElement? SelectMatchForItemLocator(
        IReadOnlyList<AutomationElement> matches,
        AutomationElement rootContainer,
        ElementLocator locator,
        string strategyName)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (locator.Index is int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
            }

            if (index >= matches.Count)
            {
                throw new InvalidOperationException(
                    $"Locator strategy '{strategyName}' index {index} is out of range (found {matches.Count}).");
            }

            return matches[index];
        }

        var selectionItemCandidates = matches.Where(HasSelectionItemPattern).ToArray();
        if (selectionItemCandidates.Length > 1)
        {
            var owned = selectionItemCandidates
                .Where(e => TryGetSelectionContainer(e, out var container) && AreSameElement(container, rootContainer))
                .ToArray();

            if (owned.Length == 1)
            {
                return owned[0];
            }

            if (owned.Length > 1)
            {
                selectionItemCandidates = owned;
            }
        }

        if (selectionItemCandidates.Length == 1)
        {
            return selectionItemCandidates[0];
        }

        throw new InvalidOperationException(
            $"Locator strategy '{strategyName}' is ambiguous (found {matches.Count}). Provide 'index' to disambiguate.");
    }

    private static async Task<AutomationElement> ResolveElementWithinRootOrScrollAsync(
        AutomationElement container,
        ElementLocator locator,
        ITreeWalker walker,
        CancellationToken cancellationToken)
    {
        var resolved = TryResolveElementWithinRoot(container, locator, walker);
        if (resolved is not null)
        {
            return resolved;
        }

        if (!TryGetScrollable(container, walker, out var scrollElement))
        {
            return ResolveElementWithinRoot(container, locator, walker);
        }

        var scroll = scrollElement.Patterns.Scroll.PatternOrDefault;
        if (scroll is null || !scroll.VerticallyScrollable)
        {
            return ResolveElementWithinRoot(container, locator, walker);
        }

        try
        {
            var horizontal = scroll.HorizontallyScrollable ? scroll.HorizontalScrollPercent : -1d;
            scroll.SetScrollPercent(horizontal, 0);
        }
        catch
        {
        }

        await Task.Delay(UiDelayScrollMs, cancellationToken);

        var maxScrollSteps = 50;
        double? lastPercent = null;
        for (var step = 0; step <= maxScrollSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            resolved = TryResolveElementWithinRoot(container, locator, walker);
            if (resolved is not null)
            {
                return resolved;
            }

            var beforePercent = TryGetScrollPercent(scroll, vertical: true);
            if (beforePercent is not null && beforePercent >= 100)
            {
                break;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            try
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            }
            catch
            {
                break;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            var afterPercent = TryGetScrollPercent(scroll, vertical: true);
            if (afterPercent is not null && lastPercent is not null && Math.Abs(afterPercent.Value - lastPercent.Value) < 0.0001)
            {
                break;
            }

            lastPercent = afterPercent;
        }

        return ResolveElementWithinRoot(container, locator, walker);
    }

    private static AutomationElement[]? TryFilterItemsToSelectionContainer(
        AutomationElement container,
        IReadOnlyList<AutomationElement> items)
    {
        if (!SupportsSelectionPattern(container))
        {
            return null;
        }

        var owned = new List<AutomationElement>();
        foreach (var item in items)
        {
            if (!TryGetSelectionContainer(item, out var selectionContainer))
            {
                continue;
            }

            if (AreSameElement(selectionContainer, container))
            {
                owned.Add(item);
            }
        }

        return owned.Count > 0 ? owned.ToArray() : null;
    }

    private static bool SupportsSelectionPattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.Selection.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetSelectionContainer(AutomationElement item, out AutomationElement selectionContainer)
    {
        selectionContainer = null!;

        try
        {
            var selectionItem = item.Patterns.SelectionItem.PatternOrDefault;
            var container = selectionItem?.SelectionContainer.ValueOrDefault;
            if (container is null)
            {
                return false;
            }

            selectionContainer = container;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateSelectableItems(AutomationElement root, ITreeWalker walker) =>
        EnumerateSelfAndDescendantsDepthFirst(root, walker)
            .Skip(1)
            .Where(HasSelectionItemPattern);

    private static bool HasSelectionItemPattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.SelectionItem.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static void SelectItemElement(AutomationElement item)
    {
        try
        {
            var selectionItem = item.Patterns.SelectionItem.PatternOrDefault;
            if (selectionItem is not null)
            {
                selectionItem.Select();
                return;
            }
        }
        catch
        {
        }

        try
        {
            var invoke = item.Patterns.Invoke.PatternOrDefault;
            if (invoke is not null)
            {
                invoke.Invoke();
                return;
            }
        }
        catch
        {
        }

        var point = GetClickPoint(item);
        Mouse.LeftClick(point);
    }

    private static AutomationElement? FindUniqueItemByName(
        IReadOnlyList<AutomationElement> items,
        string text,
        out int matches)
    {
        matches = 0;
        AutomationElement? match = null;

        foreach (var item in items)
        {
            var name = GetName(item);
            if (!string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;
            if (matches == 1)
            {
                match = item;
            }
        }

        return match;
    }

    private static bool TryGetScrollable(AutomationElement root, ITreeWalker walker, out AutomationElement scrollElement)
    {
        scrollElement = null!;

        foreach (var element in EnumerateSelfAndDescendantsDepthFirst(root, walker))
        {
            try
            {
                var scroll = element.Patterns.Scroll.PatternOrDefault;
                if (scroll is not null && scroll.VerticallyScrollable)
                {
                    scrollElement = element;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static async Task<AutomationElement> ScrollSearchUniqueItemByNameAsync(
        AutomationElement container,
        string text,
        ITreeWalker walker,
        CancellationToken cancellationToken)
    {
        if (!TryGetScrollable(container, walker, out var scrollElement))
        {
            throw new InvalidOperationException(
                $"Item text '{text}' not found (scanned current view) and container is not scrollable. Consider itemLocator.");
        }

        var scroll = scrollElement.Patterns.Scroll.PatternOrDefault;
        if (scroll is null || !scroll.VerticallyScrollable)
        {
            throw new InvalidOperationException(
                $"Item text '{text}' not found (scanned current view) and container is not vertically scrollable. Consider itemLocator.");
        }

        try
        {
            var horizontal = scroll.HorizontallyScrollable ? scroll.HorizontalScrollPercent : -1d;
            scroll.SetScrollPercent(horizontal, 0);
        }
        catch
        {
        }

        await Task.Delay(UiDelayScrollMs, cancellationToken);

        var maxScrollSteps = 50;
        var scanned = 0;

        double? lastPercent = null;
        for (var step = 0; step <= maxScrollSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allItems = EnumerateSelectableItems(container, walker).ToArray();
            scanned += allItems.Length;

            var preferredItems = TryFilterItemsToSelectionContainer(container, allItems);

            if (preferredItems is not null && preferredItems.Length > 0)
            {
                var candidate = FindUniqueItemByName(preferredItems, text, out var matches);
                if (matches == 1 && candidate is not null)
                {
                    return candidate;
                }

                if (matches > 1)
                {
                    throw new InvalidOperationException(
                        $"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }
            }

            {
                var candidate = FindUniqueItemByName(allItems, text, out var matches);
                if (matches == 1 && candidate is not null)
                {
                    return candidate;
                }

                if (matches > 1)
                {
                    throw new InvalidOperationException(
                        $"Item text '{text}' is ambiguous (found {matches}). Provide index or itemLocator.");
                }
            }

            var beforePercent = TryGetScrollPercent(scroll, vertical: true);
            if (beforePercent is not null && beforePercent >= 100)
            {
                break;
            }

            try
            {
                scrollElement.Focus();
            }
            catch
            {
            }

            try
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            }
            catch
            {
                break;
            }

            await Task.Delay(UiDelayScrollMs, cancellationToken);

            var afterPercent = TryGetScrollPercent(scroll, vertical: true);
            if (afterPercent is not null && lastPercent is not null && Math.Abs(afterPercent.Value - lastPercent.Value) < 0.0001)
            {
                break;
            }

            lastPercent = afterPercent;
        }

        throw new InvalidOperationException(
            $"Item text '{text}' not found (scanned ~{scanned} items across scroll attempts). Consider itemLocator.");
    }

    private static double? TryGetScrollPercent(IScrollPattern scrollPattern, bool vertical)
    {
        try
        {
            var value = vertical ? scrollPattern.VerticalScrollPercent : scrollPattern.HorizontalScrollPercent;
            if (double.IsNaN(value))
            {
                return null;
            }

            return value;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CaptureWindowScreen(Window window, CaptureSettings captureSettings)
    {
        using var capture = Capture.Element(window, captureSettings);
        var bitmap = capture.Bitmap;
        var croppedClientArea = TryCropToClientArea(window, bitmap);
        return croppedClientArea
            ?? bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), bitmap.PixelFormat);
    }

    private static Bitmap CaptureWindowPrintWindow(Window window) =>
        TryCaptureClientAreaWithPrintWindow(window) ?? throw new InvalidOperationException("PrintWindow capture failed.");

    private static Bitmap CaptureWindowAuto(Window window, CaptureSettings captureSettings)
    {
        var printWindowBitmap = TryCaptureClientAreaWithPrintWindow(window);
        if (printWindowBitmap is not null)
        {
            return printWindowBitmap;
        }

        return CaptureWindowScreen(window, captureSettings);
    }

    private static Bitmap CaptureElementScreen(AutomationElement element, CaptureSettings captureSettings)
    {
        using var capture = Capture.Element(element, captureSettings);
        var bitmap = capture.Bitmap;
        return bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), bitmap.PixelFormat);
    }

    private static Bitmap CaptureElementPrintWindow(Window window, AutomationElement element)
    {
        using var clientBitmap = TryCaptureClientAreaWithPrintWindow(window)
            ?? throw new InvalidOperationException("PrintWindow capture failed.");

        return TryCropElementFromClientBitmap(window, element, clientBitmap)
            ?? throw new InvalidOperationException("Failed to crop element from PrintWindow capture.");
    }

    private static Bitmap CaptureElementAuto(Window window, AutomationElement element, CaptureSettings captureSettings)
    {
        using var clientBitmap = TryCaptureClientAreaWithPrintWindow(window);
        if (clientBitmap is not null)
        {
            var cropped = TryCropElementFromClientBitmap(window, element, clientBitmap);
            if (cropped is not null)
                return cropped;
        }

        return CaptureElementScreen(element, captureSettings);
    }

    private static Bitmap? TryCropToClientArea(Window window, Bitmap bitmap)
    {
        if (!TryGetWindowClientCrop(window, out var crop))
        {
            return null;
        }

        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0)
        {
            return null;
        }

        if (crop.Right > bitmap.Width || crop.Bottom > bitmap.Height)
        {
            return null;
        }

        try
        {
            return bitmap.Clone(crop, bitmap.PixelFormat);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? TryCropElementFromClientBitmap(Window window, AutomationElement element, Bitmap clientBitmap)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            LogScreenshotDebug("TryCropElementFromClientBitmap: window handle is zero.");
            return null;
        }

        if (!TryGetClientTopLeftScreen(hwnd, out var clientTopLeft))
        {
            LogScreenshotDebug("TryCropElementFromClientBitmap: failed to resolve client top-left.");
            return null;
        }

        if (!GetClientRect(hwnd, out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            LogScreenshotDebug("TryCropElementFromClientBitmap: failed to get client rect.");
            return null;
        }

        var bounds = element.BoundingRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            LogScreenshotDebug($"TryCropElementFromClientBitmap: invalid element bounds {FormatRect(bounds)}.");
            return null;
        }

        var relLeft = bounds.Left - clientTopLeft.X;
        var relTop = bounds.Top - clientTopLeft.Y;
        var relRight = relLeft + bounds.Width;
        var relBottom = relTop + bounds.Height;

        var scaleX = clientBitmap.Width / (double)clientRect.Width;
        var scaleY = clientBitmap.Height / (double)clientRect.Height;

        var left = (int)Math.Floor(relLeft * scaleX);
        var top = (int)Math.Floor(relTop * scaleY);
        var right = (int)Math.Ceiling(relRight * scaleX);
        var bottom = (int)Math.Ceiling(relBottom * scaleY);

        var crop = Rectangle.Intersect(
            new Rectangle(0, 0, clientBitmap.Width, clientBitmap.Height),
            new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));

        LogScreenshotDebug(
            $"TryCropElementFromClientBitmap: client={clientRect.Width}x{clientRect.Height}, bitmap={clientBitmap.Width}x{clientBitmap.Height}, " +
            $"scale=({scaleX:F3},{scaleY:F3}), bounds={FormatRect(bounds)}, crop={FormatRect(crop)}.");

        if (crop.Width <= 0 || crop.Height <= 0)
        {
            LogScreenshotDebug("TryCropElementFromClientBitmap: crop rectangle is empty after intersection.");
            return null;
        }

        try
        {
            return clientBitmap.Clone(crop, clientBitmap.PixelFormat);
        }
        catch
        {
            LogScreenshotDebug("TryCropElementFromClientBitmap: bitmap crop clone failed.");
            return null;
        }
    }

    private static Bitmap? TryCropBoundsFromClientBitmap(Window window, Rect elementBounds, Bitmap clientBitmap)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            LogScreenshotDebug("TryCropBoundsFromClientBitmap: window handle is zero.");
            return null;
        }

        if (!TryGetClientTopLeftScreen(hwnd, out var clientTopLeft))
        {
            LogScreenshotDebug("TryCropBoundsFromClientBitmap: failed to resolve client top-left.");
            return null;
        }

        if (!GetClientRect(hwnd, out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            LogScreenshotDebug("TryCropBoundsFromClientBitmap: failed to get client rect.");
            return null;
        }

        if (elementBounds.Width <= 0 || elementBounds.Height <= 0)
        {
            LogScreenshotDebug($"TryCropBoundsFromClientBitmap: invalid element bounds {FormatRect(new Rectangle(elementBounds.X, elementBounds.Y, elementBounds.Width, elementBounds.Height))}.");
            return null;
        }

        var relLeft = elementBounds.X - clientTopLeft.X;
        var relTop = elementBounds.Y - clientTopLeft.Y;
        var relRight = relLeft + elementBounds.Width;
        var relBottom = relTop + elementBounds.Height;

        var scaleX = clientBitmap.Width / (double)clientRect.Width;
        var scaleY = clientBitmap.Height / (double)clientRect.Height;

        var left = (int)Math.Floor(relLeft * scaleX);
        var top = (int)Math.Floor(relTop * scaleY);
        var right = (int)Math.Ceiling(relRight * scaleX);
        var bottom = (int)Math.Ceiling(relBottom * scaleY);

        var crop = Rectangle.Intersect(
            new Rectangle(0, 0, clientBitmap.Width, clientBitmap.Height),
            new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));

        LogScreenshotDebug(
            $"TryCropBoundsFromClientBitmap: client={clientRect.Width}x{clientRect.Height}, bitmap={clientBitmap.Width}x{clientBitmap.Height}, " +
            $"scale=({scaleX:F3},{scaleY:F3}), bounds={FormatRect(new Rectangle(elementBounds.X, elementBounds.Y, elementBounds.Width, elementBounds.Height))}, crop={FormatRect(crop)}.");

        if (crop.Width <= 0 || crop.Height <= 0)
        {
            LogScreenshotDebug("TryCropBoundsFromClientBitmap: crop rectangle is empty after intersection.");
            return null;
        }

        try
        {
            return clientBitmap.Clone(crop, clientBitmap.PixelFormat);
        }
        catch
        {
            LogScreenshotDebug("TryCropBoundsFromClientBitmap: bitmap crop clone failed.");
            return null;
        }
    }

    private static Bitmap? TryCropBoundsFromWindowBitmap(Window window, Rect bounds, Bitmap windowBitmap)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            LogScreenshotDebug("TryCropBoundsFromWindowBitmap: window handle is zero.");
            return null;
        }

        if (!GetWindowRect(hwnd, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
        {
            LogScreenshotDebug("TryCropBoundsFromWindowBitmap: failed to get window rect.");
            return null;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            LogScreenshotDebug(
                $"TryCropBoundsFromWindowBitmap: invalid bounds {FormatRect(new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height))}.");
            return null;
        }

        var relLeft = bounds.X - windowRect.Left;
        var relTop = bounds.Y - windowRect.Top;
        var relRight = relLeft + bounds.Width;
        var relBottom = relTop + bounds.Height;

        var scaleX = windowBitmap.Width / (double)windowRect.Width;
        var scaleY = windowBitmap.Height / (double)windowRect.Height;

        var left = (int)Math.Floor(relLeft * scaleX);
        var top = (int)Math.Floor(relTop * scaleY);
        var right = (int)Math.Ceiling(relRight * scaleX);
        var bottom = (int)Math.Ceiling(relBottom * scaleY);

        var crop = Rectangle.Intersect(
            new Rectangle(0, 0, windowBitmap.Width, windowBitmap.Height),
            new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));

        LogScreenshotDebug(
            $"TryCropBoundsFromWindowBitmap: window={windowRect.Width}x{windowRect.Height}, bitmap={windowBitmap.Width}x{windowBitmap.Height}, " +
            $"scale=({scaleX:F3},{scaleY:F3}), bounds={FormatRect(new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height))}, crop={FormatRect(crop)}.");

        if (crop.Width <= 0 || crop.Height <= 0)
        {
            LogScreenshotDebug("TryCropBoundsFromWindowBitmap: crop rectangle is empty after intersection.");
            return null;
        }

        try
        {
            return windowBitmap.Clone(crop, windowBitmap.PixelFormat);
        }
        catch
        {
            LogScreenshotDebug("TryCropBoundsFromWindowBitmap: bitmap crop clone failed.");
            return null;
        }
    }

    private static Bitmap? TryCaptureWindowWithPrintWindow(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!GetWindowRect(hwnd, out var rect))
        {
            LogScreenshotDebug("TryCaptureWindowWithPrintWindow: GetWindowRect failed.");
            return null;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            LogScreenshotDebug($"TryCaptureWindowWithPrintWindow: invalid window size {width}x{height}.");
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(hwnd, hdc, 0))
            {
                LogScreenshotDebug("TryCaptureWindowWithPrintWindow: PrintWindow(0) returned false.");
                bitmap.Dispose();
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    private static Bitmap? TryCaptureClientAreaWithPrintWindow(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (!GetClientRect(hwnd, out var rect))
        {
            LogScreenshotDebug("TryCaptureClientAreaWithPrintWindow: GetClientRect failed.");
            return null;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            LogScreenshotDebug($"TryCaptureClientAreaWithPrintWindow: invalid client size {width}x{height}.");
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            const uint PW_CLIENTONLY = 0x00000001;
            if (!PrintWindow(hwnd, hdc, PW_CLIENTONLY))
            {
                LogScreenshotDebug("TryCaptureClientAreaWithPrintWindow: PrintWindow(PW_CLIENTONLY) returned false.");
                bitmap.Dispose();
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    private static void LogScreenshotDebug(string message)
    {
        if (!ScreenshotDebugEnabled)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine($"[WpfToolsMcp:screenshot] {message}");
        }
        catch
        {
        }
    }

    private static bool TryGetWindowClientCrop(Window window, out Rectangle crop)
    {
        crop = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var clientRect))
        {
            return false;
        }

        if (!TryGetClientTopLeftScreen(hwnd, out var clientTopLeft))
        {
            return false;
        }

        var x = clientTopLeft.X - windowRect.Left;
        var y = clientTopLeft.Y - windowRect.Top;
        var width = clientRect.Width;
        var height = clientRect.Height;

        crop = new Rectangle(x, y, width, height);
        return true;
    }

    private static bool TryGetClientTopLeftScreen(Window window, out POINT clientTopLeft)
    {
        clientTopLeft = default;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return TryGetClientTopLeftScreen(hwnd, out clientTopLeft);
    }

    private static bool TryGetClientTopLeftScreen(IntPtr hwnd, out POINT clientTopLeft)
    {
        clientTopLeft = new POINT(0, 0);
        return ClientToScreen(hwnd, ref clientTopLeft);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int rop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public async Task<GetVisualTreeResponse> GetVisualTreeAsync(
        InspectionBackend backend = InspectionBackend.Auto,
        long? windowHandle = null,
        ElementLocator? root = null,
        int depth = 4,
        int maxNodes = 500,
        bool visibleOnly = true,
        bool includeOffViewport = false,
        bool interactiveOnly = false,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        TreePreset preset = TreePreset.Minimal,
        IReadOnlyList<string>? fields = null,
        CancellationToken cancellationToken = default,
        bool autoInject = false)
    {
        var trace = BeginTraceSpan("get_visual_tree");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            if (depth <= 0)
            {
                depth = 1;
            }

            maxNodes = Math.Clamp(maxNodes, 1, 5000);
            IReadOnlyList<string>? warnings = null;

            if (backend == InspectionBackend.Wpf)
            {
                var resolvedWindowHandle = windowHandle ?? FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
                var wpfRootXPath = await ResolveWpfRootXPathAsync(
                    root,
                    resolvedWindowHandle,
                    cancellationToken).ConfigureAwait(false);

                var request = new GetWpfVisualTreeRequestV2(
                    WindowHandle: resolvedWindowHandle,
                    RootXPath: wpfRootXPath,
                    Depth: depth,
                    MaxNodes: maxNodes,
                    VisibleOnly: visibleOnly,
                    IncludeOffViewport: includeOffViewport,
                    InteractiveOnly: interactiveOnly,
                    InteractiveMode: interactiveMode,
                    Preset: preset,
                    Fields: fields);

                var response = await GetVisualTreeWpfAsync(request, injectIfMissing: true, cancellationToken).ConfigureAwait(false);
                trace?.SetSummary($"{response.BackendUsed} returned={response.ReturnedNodes} truncated={response.Truncated}");
                return response;
            }

            if (backend == InspectionBackend.Auto)
            {
                var resolvedWindowHandle = windowHandle;
                var wpfRootXPath = root?.XPath;
                var canTryWpf = true;

                if (root is not null && string.IsNullOrWhiteSpace(wpfRootXPath))
                {
                    resolvedWindowHandle ??= FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
                    var client = autoInject
                        ? await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false)
                        : await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);

                    if (client is null)
                    {
                        canTryWpf = false;
                        warnings = [autoInject ? GetAutoAgentFallbackWarning() : "backend=auto: WPF agent not connected; used UIA."];
                    }
                    else
                    {
                        try
                        {
                            wpfRootXPath = await ResolveWpfRootXPathAsync(root, resolvedWindowHandle.Value, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            canTryWpf = false;
                            warnings = [$"backend=auto: WPF root locator could not be resolved; used UIA. {ex.GetBaseException().Message}"];
                        }
                    }
                }

                if (canTryWpf)
                {
                    var request = new GetWpfVisualTreeRequestV2(
                        WindowHandle: resolvedWindowHandle,
                        RootXPath: wpfRootXPath,
                        Depth: depth,
                        MaxNodes: maxNodes,
                        VisibleOnly: visibleOnly,
                        IncludeOffViewport: includeOffViewport,
                        InteractiveOnly: interactiveOnly,
                        InteractiveMode: interactiveMode,
                        Preset: preset,
                        Fields: fields);

                    var wpf = await TryGetVisualTreeWpfAsync(request, cancellationToken, autoInject).ConfigureAwait(false);
                    if (wpf is not null)
                    {
                        trace?.SetSummary($"{wpf.BackendUsed} returned={wpf.ReturnedNodes} truncated={wpf.Truncated}");
                        return wpf;
                    }

                    warnings = [autoInject ? GetAutoAgentFallbackWarning() : "backend=auto: WPF agent not connected; used UIA."];
                }
            }

            var window = windowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var rootElement = root is null ? window : ResolveElement(window, root, controlWalker, rawWalker);
            var rootXPath = ComputeXPath(window, rootElement, rawWalker);

            var fieldSet = TreeFieldSet.Resolve(preset, fields);
            var context = new UiaTreeBuildContext(
                rawWalker,
                fieldSet,
                maxNodes,
                visibleOnly,
                includeOffViewport,
                interactiveOnly,
                interactiveMode,
                TryGetClientBoundsScreen(window, out var clientBounds) ? clientBounds : null,
                cancellationToken);

            var rootNode = BuildUiaTreeNode(rootElement, rootXPath, depth, isRoot: true, context)
                ?? throw new InvalidOperationException("Failed to build UIA tree root.");

            var responseUia = new GetVisualTreeResponse(
                BackendUsed: InspectionBackend.Uia,
                Root: rootNode,
                ReturnedNodes: context.ReturnedNodes,
                ScannedNodes: context.ScannedNodes,
                Truncated: context.Truncated,
                TruncatedReason: context.TruncatedReason,
                Warnings: warnings);

            trace?.SetSummary($"{responseUia.BackendUsed} returned={responseUia.ReturnedNodes} truncated={responseUia.Truncated}");
            return responseUia;
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
    }

    public async Task<FindElementsResponse> FindElementsAsync(
        InspectionBackend backend = InspectionBackend.Auto,
        long? windowHandle = null,
        ElementLocator? root = null,
        FindElementsQuery? query = null,
        bool visibleOnly = true,
        bool includeOffViewport = true,
        bool interactiveOnly = false,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        int maxResults = 25,
        int maxNodes = 5000,
        FindReturnFields returnFields = FindReturnFields.Minimal,
        bool includeElementIds = true,
        CancellationToken cancellationToken = default,
        bool autoInject = false)
    {
        var trace = BeginTraceSpan("find_elements");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            maxResults = Math.Clamp(maxResults, 1, 5000);
            maxNodes = Math.Clamp(maxNodes, 1, 200_000);
            IReadOnlyList<string>? warnings = null;

            if (backend == InspectionBackend.Wpf)
            {
                var resolvedWindowHandle = windowHandle ?? FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
                var wpfRootXPath = await ResolveWpfRootXPathAsync(
                    root,
                    resolvedWindowHandle,
                    cancellationToken).ConfigureAwait(false);

                var request = new FindElementsWpfRequest(
                    WindowHandle: resolvedWindowHandle,
                    RootXPath: wpfRootXPath,
                    Query: query,
                    VisibleOnly: visibleOnly,
                    IncludeOffViewport: includeOffViewport,
                    InteractiveOnly: interactiveOnly,
                    InteractiveMode: interactiveMode,
                    MaxResults: maxResults,
                    MaxNodes: maxNodes,
                    ReturnFields: returnFields);

                var wpf = await FindElementsWpfAsync(request, injectIfMissing: true, cancellationToken).ConfigureAwait(false);
                var responseWpf = includeElementIds ? AttachWpfElementIds(wpf, resolvedWindowHandle) : wpf;

                if (responseWpf.Truncated && responseWpf.ReturnedMatches == 0)
                {
                    var nextWarnings = responseWpf.Warnings is null
                        ? new List<string>(capacity: 1)
                        : new List<string>(responseWpf.Warnings);
                    nextWarnings.Add($"find_elements scanned {responseWpf.ScannedNodes} nodes and returned 0 matches before truncating; try increasing maxNodes (current {maxNodes}) or narrowing root/query.");
                    responseWpf = responseWpf with { Warnings = nextWarnings };
                }
                trace?.SetSummary($"{responseWpf.BackendUsed} matches={responseWpf.ReturnedMatches} truncated={responseWpf.Truncated}");
                return responseWpf;
            }

            if (backend == InspectionBackend.Auto)
            {
                var resolvedWindowHandle = windowHandle ?? FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
                var wpfRootXPath = root?.XPath;
                var canTryWpf = true;

                if (root is not null && string.IsNullOrWhiteSpace(wpfRootXPath))
                {
                    var client = autoInject
                        ? await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false)
                        : await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);

                    if (client is null)
                    {
                        canTryWpf = false;
                        warnings = [autoInject ? GetAutoAgentFallbackWarning() : "backend=auto: WPF agent not connected; used UIA."];
                    }
                    else
                    {
                        try
                        {
                            wpfRootXPath = await ResolveWpfRootXPathAsync(root, resolvedWindowHandle, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            canTryWpf = false;
                            warnings = [$"backend=auto: WPF root locator could not be resolved; used UIA. {ex.GetBaseException().Message}"];
                        }
                    }
                }

                if (canTryWpf)
                {
                    var request = new FindElementsWpfRequest(
                        WindowHandle: resolvedWindowHandle,
                        RootXPath: wpfRootXPath,
                        Query: query,
                        VisibleOnly: visibleOnly,
                        IncludeOffViewport: includeOffViewport,
                        InteractiveOnly: interactiveOnly,
                        InteractiveMode: interactiveMode,
                        MaxResults: maxResults,
                        MaxNodes: maxNodes,
                        ReturnFields: returnFields);

                    var wpf = await TryFindElementsWpfAsync(request, cancellationToken, autoInject).ConfigureAwait(false);
                    if (wpf is not null)
                    {
                        var responseWpf = includeElementIds ? AttachWpfElementIds(wpf, resolvedWindowHandle) : wpf;

                        if (responseWpf.Truncated && responseWpf.ReturnedMatches == 0)
                        {
                            var nextWarnings = responseWpf.Warnings is null
                                ? new List<string>(capacity: 1)
                                : new List<string>(responseWpf.Warnings);
                            nextWarnings.Add($"find_elements scanned {responseWpf.ScannedNodes} nodes and returned 0 matches before truncating; try increasing maxNodes (current {maxNodes}) or narrowing root/query.");
                            responseWpf = responseWpf with { Warnings = nextWarnings };
                        }
                        trace?.SetSummary($"{responseWpf.BackendUsed} matches={responseWpf.ReturnedMatches} truncated={responseWpf.Truncated}");
                        return responseWpf;
                    }

                    warnings = [autoInject ? GetAutoAgentFallbackWarning() : "backend=auto: WPF agent not connected; used UIA."];
                }
            }

            var window = windowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var rootElement = root is null ? window : ResolveElement(window, root, controlWalker, rawWalker);
            var rootXPath = ComputeXPath(window, rootElement, rawWalker);

            var windowHwnd = window.Properties.NativeWindowHandle.Value.ToInt64();
            var viewportBounds = visibleOnly && !includeOffViewport && TryGetClientBoundsScreen(window, out var clientBounds) ? clientBounds : null;
            var response = FindElementsUia(
                rootElement,
                rootXPath,
                rawWalker,
                query,
                visibleOnly,
                includeOffViewport,
                viewportBounds,
                interactiveOnly,
                interactiveMode,
                maxResults,
                maxNodes,
                returnFields,
                includeElementIds,
                windowHwnd,
                cancellationToken);

            var finalResponse = warnings is null ? response : response with { Warnings = warnings };
            if (finalResponse.Truncated && finalResponse.ReturnedMatches == 0)
            {
                var nextWarnings = finalResponse.Warnings is null
                    ? new List<string>(capacity: 1)
                    : new List<string>(finalResponse.Warnings);
                nextWarnings.Add($"find_elements scanned {finalResponse.ScannedNodes} nodes and returned 0 matches before truncating; try increasing maxNodes (current {maxNodes}) or narrowing root/query.");
                finalResponse = finalResponse with { Warnings = nextWarnings };
            }
            trace?.SetSummary($"{finalResponse.BackendUsed} matches={finalResponse.ReturnedMatches} truncated={finalResponse.Truncated}");
            return finalResponse;
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
    }

    public async Task<GetPathToElementResponse> GetPathToElementAsync(
        InspectionBackend backend,
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_path_to_element");
        try
        {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_path_to_element requires exactly one of: locator OR elementId.");
        }

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        if (hasElementId)
        {
            var id = elementId!.Trim();
            var handle = RequireHandle(id);

            if (windowHandle is not null && windowHandle.Value != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            if (handle.Backend == InspectionBackend.Wpf)
            {
                var request = !string.IsNullOrWhiteSpace(handle.WpfAgentElementId)
                    ? new GetWpfPathRequest(
                        WindowHandle: handle.WindowHandle,
                        Locator: null,
                        ElementId: handle.WpfAgentElementId,
                        RootXPath: null,
                        VisibleOnly: true,
                        IncludeOffViewport: true,
                        MaxNodes: 8000)
                    : new GetWpfPathRequest(
                        WindowHandle: handle.WindowHandle,
                        Locator: new ElementLocator(XPath: handle.XPath),
                        ElementId: null,
                        RootXPath: null,
                        VisibleOnly: true,
                        IncludeOffViewport: true,
                        MaxNodes: 8000);

                var fallbackRequest = !string.IsNullOrWhiteSpace(handle.WpfAgentElementId)
                    ? new GetWpfPathRequest(
                        WindowHandle: handle.WindowHandle,
                        Locator: CreateWpfHandleRecoveryLocator(handle),
                        ElementId: null,
                        RootXPath: null,
                        VisibleOnly: true,
                        IncludeOffViewport: true,
                        MaxNodes: 8000)
                    : null;
                var target = new WpfAgentTarget(
                    handle.WindowHandle,
                    request.Locator,
                    handle.WpfAgentElementId,
                    id,
                    fallbackRequest?.Locator,
                    handle);
                var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
                var wpfResponse = await CallWpfAgentTargetAsync<GetPathToElementResponse>(
                    client,
                    "wpf/get_path",
                    request,
                    fallbackRequest,
                    target,
                    cancellationToken).ConfigureAwait(false);
                _elementHandles.TryUpdateWpfPath(id, wpfResponse.XPath);
                trace?.SetSummary($"{wpfResponse.BackendUsed} {wpfResponse.XPath}");
                return wpfResponse;
            }

            Window resolvedWindow;
            try
            {
                resolvedWindow = FindWindowByHandle(application, automation, handle.WindowHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{id}'. Call resolve_element again.");
            }

            var resolvedWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            _ = ResolveUiaElementById(resolvedWindow, resolvedWalker, id, out _);
            var uiaResponseFromId = new GetPathToElementResponse(InspectionBackend.Uia, handle.XPath);
            trace?.SetSummary($"{uiaResponseFromId.BackendUsed} {uiaResponseFromId.XPath}");
            return uiaResponseFromId;
        }

        if (backend == InspectionBackend.Wpf)
        {
            var resolvedWindowHandle = windowHandle ?? FindMainWindow(application, automation).Properties.NativeWindowHandle.Value.ToInt64();
            var request = new GetWpfPathRequest(
                WindowHandle: resolvedWindowHandle,
                Locator: locator,
                RootXPath: null,
                VisibleOnly: true,
                MaxNodes: 8000);

            var wpfResponse = await GetWpfPathAsync(request, injectIfMissing: true, cancellationToken).ConfigureAwait(false);
            trace?.SetSummary($"{wpfResponse.BackendUsed} {wpfResponse.XPath}");
            return wpfResponse;
        }

        var window = windowHandle is long requestedWindowHandle
            ? FindWindowByHandle(application, automation, requestedWindowHandle)
            : FindMainWindow(application, automation);

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
        var element = ResolveElement(window, locator!, controlWalker, rawWalker);
        var xpath = ComputeXPath(window, element, rawWalker);

        var responseUia = new GetPathToElementResponse(InspectionBackend.Uia, xpath);
        trace?.SetSummary($"{responseUia.BackendUsed} {responseUia.XPath}");
        return responseUia;
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
    }

    public async Task<GetElementPropertiesResponse> GetElementPropertiesAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_element_properties");
        try
        {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_element_properties requires exactly one of: locator OR elementId.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
        var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

        Window window;
        AutomationElement element;
        string xpath;
        UiaMappingDiagnostics? uiaMapping = null;

        if (hasElementId)
        {
            var id = elementId!.Trim();
            var handle = RequireHandle(id);
            if (handle.Backend != InspectionBackend.Uia &&
                handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{id}' has unsupported backend '{handle.Backend}'.");
            }

            if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            try
            {
                window = FindWindowByHandle(application, automation, handle.WindowHandle);
            }
            catch
            {
                throw new InvalidOperationException($"stale_element: window_closed for '{id}'. Call resolve_element again.");
            }

            if (handle.Backend == InspectionBackend.Uia)
            {
                element = ResolveUiaElementById(window, rawWalker, id, out xpath);
            }
            else
            {
                var resolution = ResolveUiaElementByWpfHandleForProperties(window, controlWalker, rawWalker, id, handle);
                element = resolution.Element;
                xpath = resolution.XPath;
                uiaMapping = resolution.UiaMapping;
            }
        }
        else
        {
            window = windowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

            var wpfTarget = await TryResolveWpfLocatorTargetForAutoAsync(
                window,
                locator!,
                timeoutMs: 0,
                pollIntervalMs: 100,
                stableMs: 0,
                visibleOnly: false,
                includeOffViewport: true,
                interactiveOnly: false,
                interactiveMode: InteractiveMode.Heuristic,
                cancellationToken).ConfigureAwait(false);

            if (wpfTarget is not null)
            {
                var resolution = ResolveUiaElementByWpfHandleForProperties(window, controlWalker, rawWalker, wpfTarget.ElementId, wpfTarget.Handle);
                element = resolution.Element;
                xpath = resolution.XPath;
                uiaMapping = resolution.UiaMapping;
            }
            else
            {
                element = ResolveElement(window, locator!, controlWalker, rawWalker);
                xpath = ComputeXPath(window, element, rawWalker);
            }
        }

        var summary = new ElementSummary(
            ElementType: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            ClassName: GetClassName(element),
            Bounds: ToRect(element.BoundingRectangle),
            IsEnabled: element.IsEnabled,
            IsOffscreen: element.IsOffscreen,
            XPath: xpath);

        var properties = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        PopulateProperties(element, properties);

        var patterns = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        PopulatePatterns(element, patterns);

        var response = new GetElementPropertiesResponse(summary, properties, patterns, uiaMapping);
        trace?.SetSummary($"{summary.ElementType} {summary.XPath}");
        return response;
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
    }

    private void EnsureNotAttached()
    {
        if (IsApplicationRunning(_application))
        {
            throw new InvalidOperationException("An application is already attached. Close the current session or create a new session.");
        }

        Cleanup();
    }

    private Application EnsureAttached()
    {
        var application = _application;
        if (IsApplicationRunning(application))
        {
            return application!;
        }

        Cleanup();
        throw new InvalidOperationException("No application is attached. Call launch_app or attach_to_app first.");
    }

    private static bool IsApplicationRunning(Application? application)
    {
        if (application is null)
        {
            return false;
        }

        try
        {
            return !application.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private UIA3Automation EnsureAutomation() =>
        _automation ?? throw new InvalidOperationException("Automation has not been initialized.");

    private static Window FindMainWindow(Application application, UIA3Automation automation, TimeSpan? timeout = null)
    {
        var window = application.GetMainWindow(automation, timeout ?? TimeSpan.FromSeconds(10));
        if (window is null)
        {
            throw new InvalidOperationException("Failed to find the main window within the timeout.");
        }

        return window;
    }

    private static IReadOnlyList<Window> GetAllTopLevelWindows(Application application, UIA3Automation automation)
    {
        var windows = application.GetAllTopLevelWindows(automation).ToList();
        var handles = new HashSet<long>(windows.Select(w => w.Properties.NativeWindowHandle.Value.ToInt64()));

        foreach (var hwnd in EnumerateVisibleTopLevelWindowHandles(application.ProcessId))
        {
            var handle = hwnd.ToInt64();
            if (!handles.Add(handle))
            {
                continue;
            }

            try
            {
                var element = automation.FromHandle(hwnd);
                var window = element.AsWindow();

                var bounds = window.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(window.Title))
                {
                    continue;
                }

                windows.Add(window);
            }
            catch
            {
            }
        }

        return windows;
    }

    private static IReadOnlyList<IntPtr> EnumerateVisibleTopLevelWindowHandles(int processId)
    {
        var handles = new List<IntPtr>();

        EnumWindows(
            (hwnd, lParam) =>
            {
                try
                {
                    GetWindowThreadProcessId(hwnd, out var windowProcessId);
                    if (windowProcessId != processId)
                    {
                        return true;
                    }

                    if (!IsWindowVisible(hwnd))
                    {
                        return true;
                    }

                    handles.Add(hwnd);
                    return true;
                }
                catch
                {
                    return true;
                }
            },
            IntPtr.Zero);

        return handles;
    }

    private static Window FindWindowByHandle(Application application, UIA3Automation automation, long nativeWindowHandle)
    {
        var windows = GetAllTopLevelWindows(application, automation);
        var window = windows.FirstOrDefault(w => w.Properties.NativeWindowHandle.Value.ToInt64() == nativeWindowHandle);
        if (window is null)
        {
            throw new InvalidOperationException($"No window found with handle {nativeWindowHandle}.");
        }

        return window;
    }

    private static Window FindWindowByTitle(Application application, UIA3Automation automation, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var windows = GetAllTopLevelWindows(application, automation).ToArray();

        var exact = windows
            .Where(w => w is not null && string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exact.Length == 1)
        {
            return exact[0];
        }

        if (exact.Length > 1)
        {
            throw new InvalidOperationException($"Multiple windows found with title '{title}'. Provide windowHandle instead.");
        }

        var contains = windows
            .Where(w => w is not null && w.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        if (contains.Length == 1)
        {
            return contains[0];
        }

        if (contains.Length > 1)
        {
            throw new InvalidOperationException($"Multiple windows contain title '{title}'. Provide windowHandle instead.");
        }

        throw new InvalidOperationException($"No window found with title '{title}'.");
    }

    private static WindowInfo ToWindowInfo(Window window)
    {
        var bounds = window.BoundingRectangle;
        return new WindowInfo(
            Title: window.Title,
            Handle: window.Properties.NativeWindowHandle.Value.ToInt64(),
            Bounds: new Rect(
                X: bounds.Left,
                Y: bounds.Top,
                Width: bounds.Width,
                Height: bounds.Height),
            IsVisible: !window.IsOffscreen,
            IsEnabled: window.IsEnabled);
    }

    private enum ActionKind
    {
        Inspect,
        Click,
        Invoke,
        TypeText,
        SetValue,
        SelectItem,
        ScrollToElement,
        Drag
    }

    private static AutomationElement ResolveElement(
        Window window,
        ElementLocator locator,
        ITreeWalker controlWalker,
        ITreeWalker rawWalker,
        ActionKind actionKind = ActionKind.Inspect,
        bool visibleOnly = false,
        bool includeOffViewport = false,
        bool interactiveOnly = false,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic)
    {
        if (locator is null)
        {
            throw new ArgumentNullException(nameof(locator));
        }

        if (IsEmptyLocator(locator))
        {
            throw new ArgumentException(
                "Locator must specify at least one of: xpath, automationId, automationIdContains, name, nameContains, className, classNameContains, typeEquals, controlTypeEquals, index.",
                nameof(locator));
        }

        if (!string.IsNullOrWhiteSpace(locator.XPath))
        {
            if (locator.Index is not null)
            {
                throw new ArgumentException("index cannot be used with xpath.", nameof(locator));
            }

            var resolved = TryResolveByXPath(window, locator, rawWalker)
                ?? throw new InvalidOperationException("Locator did not match any element.");

            var mismatch = DescribeXPathFilterMismatchUia(resolved, locator);
            if (mismatch is not null)
            {
                throw new InvalidOperationException(mismatch);
            }

            if (visibleOnly && !IsVisibleUia(window, resolved, includeOffViewport))
            {
                throw new InvalidOperationException("Locator did not match any element (visibleOnly=true).");
            }

            if (interactiveOnly && !IsInteractiveUia(resolved, interactiveMode))
            {
                throw new InvalidOperationException("Locator did not match any element (interactiveOnly=true).");
            }

            return resolved;
        }

        var indexOnly = TryResolveByIndexOnly(window, locator, controlWalker, visibleOnly, includeOffViewport, interactiveOnly, interactiveMode);
        if (indexOnly is not null)
        {
            return indexOnly;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, controlWalker)
            .Where(e => MatchesLocatorUia(e, locator))
            .Where(e => !visibleOnly || IsVisibleUia(window, e, includeOffViewport))
            .Where(e => !interactiveOnly || IsInteractiveUia(e, interactiveMode))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException("Locator did not match any element.");
        }

        return SelectMatch(matches, locator, actionKind)
            ?? throw new InvalidOperationException("Locator did not match any element.");
    }

    private static bool IsVisibleUia(Window window, AutomationElement element, bool includeOffViewport)
    {
        if (!HasValidBounds(element))
        {
            return false;
        }

        if (includeOffViewport)
        {
            return true;
        }

        try
        {
            if (element.Properties.IsOffscreen.Value)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!TryGetClientBoundsScreen(window, out var clientBounds))
        {
            return true;
        }

        try
        {
            var bounds = ToRect(element.BoundingRectangle);
            return RectIntersects(bounds, clientBounds);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEmptyLocator(ElementLocator locator)
    {
        return string.IsNullOrWhiteSpace(locator.AutomationId)
               && string.IsNullOrWhiteSpace(locator.AutomationIdContains)
               && string.IsNullOrWhiteSpace(locator.Name)
               && string.IsNullOrWhiteSpace(locator.NameContains)
               && string.IsNullOrWhiteSpace(locator.ClassName)
               && string.IsNullOrWhiteSpace(locator.ClassNameContains)
               && string.IsNullOrWhiteSpace(locator.TypeEquals)
               && string.IsNullOrWhiteSpace(locator.ControlTypeEquals)
               && string.IsNullOrWhiteSpace(locator.XPath)
               && locator.Index is null;
    }

    private static string? DescribeXPathFilterMismatchUia(AutomationElement element, ElementLocator locator)
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            var actual = GetAutomationId(element);
            if (!string.Equals(actual, locator.AutomationId, StringComparison.Ordinal))
            {
                errors.Add($"automationId expected '{locator.AutomationId}' actual '{actual ?? ""}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.AutomationIdContains))
        {
            var expected = locator.AutomationIdContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetAutomationId(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    errors.Add($"automationIdContains expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.Name))
        {
            var actual = GetName(element);
            if (!string.Equals(actual, locator.Name, StringComparison.Ordinal))
            {
                errors.Add($"name expected '{locator.Name}' actual '{actual ?? ""}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.NameContains))
        {
            var expected = locator.NameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetName(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    errors.Add($"nameContains expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName))
        {
            var actual = GetClassName(element);
            if (!string.Equals(actual, locator.ClassName, StringComparison.Ordinal))
            {
                errors.Add($"className expected '{locator.ClassName}' actual '{actual ?? ""}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassNameContains))
        {
            var expected = locator.ClassNameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetClassName(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    errors.Add($"classNameContains expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ControlTypeEquals))
        {
            var expected = locator.ControlTypeEquals.Trim();
            if (expected.Length > 0)
            {
                var actual = element.ControlType.ToString();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"controlTypeEquals expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.TypeEquals))
        {
            var expected = locator.TypeEquals.Trim();
            if (expected.Length > 0)
            {
                var actual = GetXPathLabel(element);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"typeEquals expected '{expected}' actual '{actual}'");
                }
            }
        }

        if (errors.Count == 0)
        {
            return null;
        }

        return $"xpath_resolved_but_filters_mismatch: {string.Join("; ", errors)}";
    }

    private static bool MatchesLocatorUia(AutomationElement element, ElementLocator locator)
    {
        if (!string.IsNullOrWhiteSpace(locator.AutomationId) &&
            !string.Equals(GetAutomationId(element), locator.AutomationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.AutomationIdContains))
        {
            var expected = locator.AutomationIdContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetAutomationId(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.Name) &&
            !string.Equals(GetName(element), locator.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.NameContains))
        {
            var expected = locator.NameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetName(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassName) &&
            !string.Equals(GetClassName(element), locator.ClassName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(locator.ClassNameContains))
        {
            var expected = locator.ClassNameContains.Trim();
            if (expected.Length > 0)
            {
                var actual = GetClassName(element) ?? "";
                if (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.ControlTypeEquals))
        {
            var expected = locator.ControlTypeEquals.Trim();
            if (expected.Length > 0)
            {
                var actual = element.ControlType.ToString();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(locator.TypeEquals))
        {
            var expected = locator.TypeEquals.Trim();
            if (expected.Length > 0)
            {
                var actual = GetXPathLabel(element);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static AutomationElement? TryResolveByAutomationId(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.AutomationId))
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetAutomationId(e), locator.AutomationId, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByAutomationIdContains(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.AutomationIdContains))
        {
            return null;
        }

        var value = locator.AutomationIdContains.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => (GetAutomationId(e) ?? "").Contains(value, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByName(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.Name))
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetName(e), locator.Name, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByNameContains(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.NameContains))
        {
            return null;
        }

        var value = locator.NameContains.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => (GetName(e) ?? "").Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByClassName(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.ClassName))
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetClassName(e), locator.ClassName, StringComparison.Ordinal))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByClassNameContains(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.ClassNameContains))
        {
            return null;
        }

        var value = locator.ClassNameContains.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => (GetClassName(e) ?? "").Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByTypeEquals(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.TypeEquals))
        {
            return null;
        }

        var value = locator.TypeEquals.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var matches = EnumerateSelfAndDescendantsDepthFirst(window, walker)
            .Where(e => string.Equals(GetXPathLabel(e), value, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return SelectMatch(matches, locator, ActionKind.Inspect);
    }

    private static AutomationElement? TryResolveByXPath(Window window, ElementLocator locator, ITreeWalker walker)
    {
        if (string.IsNullOrWhiteSpace(locator.XPath))
        {
            return null;
        }

        var xpath = locator.XPath.Trim();
        if (xpath.Length == 0)
        {
            return null;
        }

        var segments = xpath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseXPathSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("XPath must contain at least one segment.", nameof(locator));
        }

        AutomationElement current = window;
        var rootLabel = GetXPathLabel(current);
        if (string.Equals(segments[0].TypeName, rootLabel, StringComparison.OrdinalIgnoreCase))
        {
            segments = segments.Skip(1).ToArray();
        }

        foreach (var segment in segments)
        {
            var children = GetChildren(current, walker);
            var matches = children
                .Where(c => string.Equals(GetXPathLabel(c), segment.TypeName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"XPath segment not found: '{segment.TypeName}'.");
            }

            if (segment.OneBasedIndex is int oneBased)
            {
                if (oneBased <= 0 || oneBased > matches.Length)
                {
                    throw new InvalidOperationException($"XPath index [{oneBased}] is out of range for segment '{segment.TypeName}' (found {matches.Length}).");
                }

                current = matches[oneBased - 1];
            }
            else
            {
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"XPath segment '{segment.TypeName}' is ambiguous (found {matches.Length}). Add an index like '{segment.TypeName}[n]'.");
                }

                current = matches[0];
            }
        }

        return current;
    }

    private static AutomationElement? TryResolveByIndexOnly(
        Window window,
        ElementLocator locator,
        ITreeWalker walker,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode)
    {
        if (locator.Index is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(locator.AutomationId) ||
            !string.IsNullOrWhiteSpace(locator.AutomationIdContains) ||
            !string.IsNullOrWhiteSpace(locator.Name) ||
            !string.IsNullOrWhiteSpace(locator.NameContains) ||
            !string.IsNullOrWhiteSpace(locator.ClassName) ||
            !string.IsNullOrWhiteSpace(locator.ClassNameContains) ||
            !string.IsNullOrWhiteSpace(locator.TypeEquals) ||
            !string.IsNullOrWhiteSpace(locator.ControlTypeEquals) ||
            !string.IsNullOrWhiteSpace(locator.XPath))
        {
            return null;
        }

        var index = locator.Index.Value;
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(locator), "index must be >= 0.");
        }

        var query = EnumerateSelfAndDescendantsDepthFirst(window, walker).Skip(1);
        if (visibleOnly)
        {
            query = query.Where(e => IsVisibleUia(window, e, includeOffViewport));
        }

        if (interactiveOnly)
        {
            query = query.Where(e => IsInteractiveUia(e, interactiveMode));
        }

        var descendants = query.ToArray();
        if (index >= descendants.Length)
        {
            throw new InvalidOperationException($"index {index} is out of range (found {descendants.Length} descendants).");
        }

        return descendants[index];
    }

    private static AutomationElement? SelectMatch(IReadOnlyList<AutomationElement> matches, ElementLocator locator, ActionKind actionKind)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (locator.Index is null)
        {
            if (matches.Count == 1)
            {
                return matches[0];
            }

            var orderedForAction = OrderMatchesForAction(matches, locator, actionKind);
            if (locator.Strict)
            {
                var details = BuildAmbiguousCandidatesDetails(orderedForAction, maxCandidates: 5);
                throw new InvalidOperationException(
                    $"Locator is ambiguous (found {matches.Count}). Provide 'index' to disambiguate."
                    + details);
            }

            return orderedForAction.Count > 0 ? orderedForAction[0] : matches[0];
        }

        var index = locator.Index.Value;
        if (index < 0)
        {
            throw new InvalidOperationException("index must be >= 0.");
        }

        var ordered = OrderMatchesDeterministic(matches, locator);
        if (index >= ordered.Count)
        {
            throw new InvalidOperationException(
                $"Locator matched {ordered.Count} elements but index {index} is out of range.");
        }

        return ordered[index];
    }

    private static IReadOnlyList<AutomationElement> OrderMatchesDeterministic(IReadOnlyList<AutomationElement> matches, ElementLocator locator)
    {
        if (matches.Count <= 1)
        {
            return matches;
        }

        var list = matches.ToList();
        list.Sort((a, b) =>
        {
            var offA = locator.PreferVisible ? GetOffscreenRank(a) : 0;
            var offB = locator.PreferVisible ? GetOffscreenRank(b) : 0;
            var cmp = offA.CompareTo(offB);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = GetEnabledRank(a).CompareTo(GetEnabledRank(b));
            if (cmp != 0)
            {
                return cmp;
            }

            var ba = TryGetBounds(a);
            var bb = TryGetBounds(b);
            cmp = (ba?.Top ?? int.MaxValue).CompareTo(bb?.Top ?? int.MaxValue);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = (ba?.Left ?? int.MaxValue).CompareTo(bb?.Left ?? int.MaxValue);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(GetAutomationId(a), GetAutomationId(b), StringComparison.Ordinal);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(GetName(a), GetName(b), StringComparison.Ordinal);
            return cmp;
        });
        return list;
    }

    private static IReadOnlyList<AutomationElement> OrderMatchesForAction(IReadOnlyList<AutomationElement> matches, ElementLocator locator, ActionKind actionKind)
    {
        if (matches.Count <= 1)
        {
            return matches;
        }

        var list = matches.ToList();
        list.Sort((a, b) =>
        {
            var offA = locator.PreferVisible ? GetOffscreenRank(a) : 0;
            var offB = locator.PreferVisible ? GetOffscreenRank(b) : 0;
            var cmp = offA.CompareTo(offB);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = GetEnabledRank(a).CompareTo(GetEnabledRank(b));
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = GetActionAffinityRank(a, actionKind).CompareTo(GetActionAffinityRank(b, actionKind));
            if (cmp != 0)
            {
                return cmp;
            }

            var ba = TryGetBounds(a);
            var bb = TryGetBounds(b);
            cmp = (ba?.Top ?? int.MaxValue).CompareTo(bb?.Top ?? int.MaxValue);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = (ba?.Left ?? int.MaxValue).CompareTo(bb?.Left ?? int.MaxValue);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(GetAutomationId(a), GetAutomationId(b), StringComparison.Ordinal);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(GetName(a), GetName(b), StringComparison.Ordinal);
            return cmp;
        });
        return list;
    }

    private static int GetActionAffinityRank(AutomationElement element, ActionKind actionKind)
    {
        switch (actionKind)
        {
            case ActionKind.Invoke:
                return TryIsInvokeSupported(element) ? 0 : 1;
            case ActionKind.Click:
                {
                    if (TryIsInvokeSupported(element))
                    {
                        return 0;
                    }

                    var clickable = TryIsClickableControlType(element);
                    var hasClickPoint = TryHasClickablePoint(element);
                    if (clickable && hasClickPoint)
                    {
                        return 1;
                    }

                    if (hasClickPoint)
                    {
                        return 2;
                    }

                    return 3;
                }
            case ActionKind.TypeText:
                {
                    if (TryHasWritableValuePattern(element))
                    {
                        return 0;
                    }

                    try
                    {
                        if (element.ControlType == ControlType.Edit)
                        {
                            return 1;
                        }
                    }
                    catch
                    {
                    }

                    return TryHasValuePattern(element) ? 2 : 3;
                }
            case ActionKind.SetValue:
                {
                    if (TryHasWritableRangeValuePattern(element))
                    {
                        return 0;
                    }

                    if (TryHasWritableValuePattern(element))
                    {
                        return 1;
                    }

                    return TryHasRangeValuePattern(element) || TryHasValuePattern(element) ? 2 : 3;
                }
            case ActionKind.SelectItem:
                {
                    try
                    {
                        if (element.ControlType == ControlType.ComboBox)
                        {
                            return 0;
                        }
                    }
                    catch
                    {
                    }

                    return TryHasSelectionPattern(element) ? 0 : 1;
                }
            case ActionKind.ScrollToElement:
                return TryHasScrollItemPattern(element) ? 0 : 1;
            case ActionKind.Drag:
                return TryHasValidBounds(element) ? 0 : 1;
            case ActionKind.Inspect:
            default:
                return 0;
        }
    }

    private static bool TryHasValidBounds(AutomationElement element)
    {
        try
        {
            var bounds = element.BoundingRectangle;
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasClickablePoint(AutomationElement element)
    {
        try
        {
            return element.TryGetClickablePoint(out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryIsInvokeSupported(AutomationElement element)
    {
        try
        {
            return element.Patterns.Invoke.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasSelectionPattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.Selection.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasScrollItemPattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.ScrollItem.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasRangeValuePattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.RangeValue.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasWritableRangeValuePattern(AutomationElement element)
    {
        try
        {
            var pattern = element.Patterns.RangeValue.PatternOrDefault;
            return pattern is not null && pattern.IsReadOnly == false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasValuePattern(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasWritableValuePattern(AutomationElement element)
    {
        try
        {
            var pattern = element.Patterns.Value.PatternOrDefault;
            return pattern is not null && pattern.IsReadOnly == false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryIsClickableControlType(AutomationElement element)
    {
        try
        {
            return element.ControlType == ControlType.Button
                   || element.ControlType == ControlType.Hyperlink
                   || element.ControlType == ControlType.MenuItem
                   || element.ControlType == ControlType.SplitButton;
        }
        catch
        {
            return false;
        }
    }

    private static int GetOffscreenRank(AutomationElement element)
    {
        try
        {
            return element.Properties.IsOffscreen.Value ? 1 : 0;
        }
        catch
        {
            return 2;
        }
    }

    private static int GetEnabledRank(AutomationElement element)
    {
        try
        {
            return element.Properties.IsEnabled.Value ? 0 : 1;
        }
        catch
        {
            return 2;
        }
    }

    private static Rectangle? TryGetBounds(AutomationElement element)
    {
        try
        {
            return element.BoundingRectangle;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAmbiguousCandidatesDetails(IReadOnlyList<AutomationElement> matches, int maxCandidates)
    {
        if (matches.Count == 0 || maxCandidates <= 0)
        {
            return "";
        }

        var take = Math.Min(maxCandidates, matches.Count);
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Candidates:");
        for (var i = 0; i < take; i++)
        {
            var e = matches[i];
            var type = GetXPathLabel(e);
            var name = GetName(e);
            var automationId = GetAutomationId(e);
            var bounds = TryGetBounds(e);
            var boundsText = bounds is null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0
                ? "bounds=n/a"
                : $"bounds={bounds.Value.Left},{bounds.Value.Top} {bounds.Value.Width}x{bounds.Value.Height}";

            var enabled = TryGetBooleanString(() => e.Properties.IsEnabled.Value);
            var offscreen = TryGetBooleanString(() => e.Properties.IsOffscreen.Value);

            sb.Append("  - ");
            sb.Append(type);
            if (!string.IsNullOrWhiteSpace(name))
            {
                sb.Append($", name='{name}'");
            }

            if (!string.IsNullOrWhiteSpace(automationId))
            {
                sb.Append($", automationId='{automationId}'");
            }

            sb.Append($", {boundsText}");
            if (enabled is not null)
            {
                sb.Append($", enabled={enabled}");
            }

            if (offscreen is not null)
            {
                sb.Append($", offscreen={offscreen}");
            }

            sb.AppendLine();
        }

        if (matches.Count > take)
        {
            sb.AppendLine($"  ... and {matches.Count - take} more");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? TryGetBooleanString(Func<bool> action)
    {
        try
        {
            return action() ? "true" : "false";
        }
        catch
        {
            return null;
        }
    }

    private static void TryScrollIntoView(AutomationElement element)
    {
        try
        {
            if (element.IsOffscreen)
            {
                element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            }
        }
        catch
        {
        }
    }

    private static string? TryGetParentXPath(string xpath)
    {
        if (string.IsNullOrWhiteSpace(xpath))
        {
            return null;
        }

        var trimmed = xpath.Trim();
        if (trimmed.Length <= 1)
        {
            return null;
        }

        if (trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var parent = trimmed[..slash];
        return string.IsNullOrWhiteSpace(parent) ? null : parent;
    }

    private static Point GetClickPoint(AutomationElement element)
    {
        if (element.TryGetClickablePoint(out var point))
        {
            return point;
        }

        var bounds = element.BoundingRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Element has no clickable point and has invalid bounds.");
        }

        return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    private static IEnumerable<AutomationElement> EnumerateSelfAndDescendantsDepthFirst(AutomationElement root, ITreeWalker walker)
    {
        yield return root;

        foreach (var child in GetChildren(root, walker))
        {
            foreach (var descendant in EnumerateSelfAndDescendantsDepthFirst(child, walker))
            {
                yield return descendant;
            }
        }
    }

    private sealed record XPathSegment(string TypeName, int? OneBasedIndex);

    private static XPathSegment ParseXPathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("XPath segment cannot be empty.");
        }

        var bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex < 0)
        {
            return new XPathSegment(segment, null);
        }

        var closingIndex = segment.IndexOf(']', bracketIndex + 1);
        if (closingIndex < 0)
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': missing closing ']'.");
        }

        if (closingIndex != segment.Length - 1)
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': unexpected characters after ']'.");
        }

        var typeName = segment[..bracketIndex];
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': missing type name.");
        }

        var indexText = segment[(bracketIndex + 1)..closingIndex];
        if (!int.TryParse(indexText, out var oneBasedIndex))
        {
            throw new ArgumentException($"Invalid XPath segment '{segment}': index is not a number.");
        }

        return new XPathSegment(typeName, oneBasedIndex);
    }

    private static string ComputeXPath(Window window, AutomationElement element, ITreeWalker walker)
    {
        if (AreSameElement(window, element))
        {
            return "/Window";
        }

        var segments = new List<string>();
        AutomationElement? current = element;

        while (current is not null && !AreSameElement(current, window))
        {
            AutomationElement? parent;
            try
            {
                parent = walker.GetParent(current);
            }
            catch
            {
                parent = null;
            }

            if (parent is null)
            {
                break;
            }

            segments.Add(ComputeXPathSegment(parent, current, walker));
            current = parent;
        }

        segments.Reverse();
        return "/Window/" + string.Join('/', segments);
    }

    private static string ComputeXPathSegment(AutomationElement parent, AutomationElement child, ITreeWalker walker)
    {
        var label = GetXPathLabel(child);
        var siblings = GetChildren(parent, walker)
            .Where(c => string.Equals(GetXPathLabel(c), label, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (siblings.Length <= 1)
        {
            return label;
        }

        var oneBasedIndex = Array.FindIndex(siblings, s => AreSameElement(s, child)) + 1;
        if (oneBasedIndex <= 0)
        {
            return label;
        }

        return $"{label}[{oneBasedIndex}]";
    }

    private static bool AreSameElement(AutomationElement first, AutomationElement second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        var firstRuntimeId = TryGetRuntimeId(first);
        var secondRuntimeId = TryGetRuntimeId(second);
        if (firstRuntimeId is not null && secondRuntimeId is not null)
        {
            return firstRuntimeId.SequenceEqual(secondRuntimeId);
        }

        return false;
    }

    private static int[]? TryGetRuntimeId(AutomationElement element)
    {
        try
        {
            return element.Properties.RuntimeId.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string GetXPathLabel(AutomationElement element)
    {
        if (element.ControlType == ControlType.Window)
        {
            return "Window";
        }

        var className = GetClassName(element);
        return !string.IsNullOrWhiteSpace(className) ? className : element.ControlType.ToString();
    }

    private readonly record struct TreeFieldSet(
        bool IncludeClassName,
        bool IncludeBounds,
        bool IncludeIsEnabled,
        bool IncludeIsOffscreen)
    {
        private static readonly string[] KnownFields =
        [
            "className",
            "bounds",
            "isEnabled",
            "isOffscreen",
            "visibility",
            "isVisible",
            "dataContextType"
        ];

        public static TreeFieldSet Resolve(TreePreset preset, IReadOnlyList<string>? fields)
        {
            if (fields is not null && fields.Count > 0)
            {
                var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    if (string.IsNullOrWhiteSpace(field))
                    {
                        continue;
                    }

                    normalized.Add(field.Trim());
                }

                var unknown = normalized.Where(f => !KnownFields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (unknown.Length > 0)
                {
                    throw new ArgumentException(
                        $"Unknown field(s): {string.Join(", ", unknown)}. Known fields: {string.Join(", ", KnownFields)}.");
                }

                return new TreeFieldSet(
                    IncludeClassName: normalized.Contains("className"),
                    IncludeBounds: normalized.Contains("bounds"),
                    IncludeIsEnabled: normalized.Contains("isEnabled"),
                    IncludeIsOffscreen: normalized.Contains("isOffscreen"));
            }

            return preset switch
            {
                TreePreset.Minimal => new TreeFieldSet(false, false, false, false),
                TreePreset.Standard => new TreeFieldSet(true, true, true, true),
                TreePreset.Debug => new TreeFieldSet(true, true, true, true),
                _ => new TreeFieldSet(false, false, false, false)
            };
        }
    }

    private sealed class UiaTreeBuildContext(
        ITreeWalker walker,
        TreeFieldSet fieldSet,
        int maxNodes,
        bool visibleOnly,
        bool includeOffViewport,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        Rect? viewportBounds,
        CancellationToken cancellationToken)
    {
        public ITreeWalker Walker { get; } = walker;
        public TreeFieldSet FieldSet { get; } = fieldSet;
        public int MaxNodes { get; } = maxNodes;
        public bool VisibleOnly { get; } = visibleOnly;
        public bool IncludeOffViewport { get; } = includeOffViewport;
        public bool InteractiveOnly { get; } = interactiveOnly;
        public InteractiveMode InteractiveMode { get; } = interactiveMode;
        public Rect? ViewportBounds { get; } = viewportBounds;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public int ReturnedNodes { get; set; }
        public int ScannedNodes { get; set; }
        public bool Truncated { get; private set; }
        public string? TruncatedReason { get; private set; }

        public void MarkTruncated(string reason)
        {
            if (Truncated)
            {
                return;
            }

            Truncated = true;
            TruncatedReason = reason;
        }
    }

    private static TreeNode? BuildUiaTreeNode(
        AutomationElement element,
        string xpath,
        int depth,
        bool isRoot,
        UiaTreeBuildContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.ScannedNodes++;

        if (!isRoot && context.VisibleOnly && !IsVisibleInTree(element, context))
        {
            return null;
        }

        if (!isRoot && context.ReturnedNodes >= context.MaxNodes)
        {
            context.MarkTruncated("maxNodes");
            return null;
        }

        // Reserve a slot so maxNodes is enforced during recursion.
        context.ReturnedNodes++;

        var rawChildren = GetChildren(element, context.Walker).ToArray();
        if (context.VisibleOnly)
        {
            rawChildren = rawChildren.Where(c => IsVisibleInTree(c, context)).ToArray();
        }

        var childrenCount = rawChildren.Length;
        var children = Array.Empty<TreeNode>();
        if (depth > 1 && childrenCount > 0)
        {
            if (context.ReturnedNodes < context.MaxNodes)
            {
                children = BuildUiaChildren(rawChildren, xpath, depth - 1, context);
            }
            else
            {
                context.MarkTruncated("maxNodes");
            }
        }

        var isInteractive = IsInteractiveUia(element, context.InteractiveMode);

        if (!isRoot && context.InteractiveOnly && !isInteractive && childrenCount == 0)
        {
            context.ReturnedNodes--;
            return null;
        }

        string? className = null;
        Rect? bounds = null;
        bool? isEnabled = null;
        bool? isOffscreen = null;

        if (context.FieldSet.IncludeClassName)
        {
            className = GetClassName(element);
        }

        if (context.FieldSet.IncludeBounds)
        {
            bounds = ToRect(element.BoundingRectangle);
        }

        if (context.FieldSet.IncludeIsEnabled)
        {
            isEnabled = element.IsEnabled;
        }

        if (context.FieldSet.IncludeIsOffscreen)
        {
            isOffscreen = element.IsOffscreen;
        }

        return new TreeNode(
            Type: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ChildrenCount: childrenCount,
            Children: children,
            ClassName: className,
            Bounds: bounds,
            IsEnabled: isEnabled,
            IsOffscreen: isOffscreen);
    }

    private static bool IsVisibleInTree(AutomationElement element, UiaTreeBuildContext context)
    {
        if (!HasValidBounds(element))
        {
            return false;
        }

        if (context.IncludeOffViewport || context.ViewportBounds is null)
        {
            return true;
        }

        try
        {
            if (element.IsOffscreen)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            var bounds = ToRect(element.BoundingRectangle);
            return RectIntersects(bounds, context.ViewportBounds!);
        }
        catch
        {
            return false;
        }
    }

    private static TreeNode[] BuildUiaChildren(
        AutomationElement[] rawChildren,
        string parentXPath,
        int remainingDepth,
        UiaTreeBuildContext context)
    {
        if (rawChildren.Length == 0)
        {
            return [];
        }

        var labels = rawChildren.Select(GetXPathLabel).ToArray();
        var countsByLabel = labels
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<TreeNode>(rawChildren.Length);

        for (var i = 0; i < rawChildren.Length; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (context.ReturnedNodes >= context.MaxNodes)
            {
                context.MarkTruncated("maxNodes");
                break;
            }

            var child = rawChildren[i];
            var label = labels[i];

            runningIndexByLabel.TryGetValue(label, out var currentIndex);
            currentIndex++;
            runningIndexByLabel[label] = currentIndex;

            var includeIndex = countsByLabel[label] > 1;
            var segment = includeIndex ? $"{label}[{currentIndex}]" : label;
            var childXPath = $"{parentXPath}/{segment}";

            var node = BuildUiaTreeNode(child, childXPath, remainingDepth, isRoot: false, context);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes.ToArray();
    }

    private static bool IsInteractiveUia(AutomationElement element, InteractiveMode mode)
    {
        if (!element.IsEnabled)
        {
            return false;
        }

        if (mode == InteractiveMode.Patterns)
        {
            return IsInteractiveUiaByPatterns(element);
        }

        return IsInteractiveUiaByHeuristic(element);
    }

    private static bool IsInteractiveUiaByHeuristic(AutomationElement element)
    {
        var type = element.ControlType;
        return type == ControlType.Button
               || type == ControlType.Hyperlink
               || type == ControlType.CheckBox
               || type == ControlType.RadioButton
               || type == ControlType.ComboBox
               || type == ControlType.Edit
               || type == ControlType.Slider
               || type == ControlType.TabItem
               || type == ControlType.ListItem
               || type == ControlType.TreeItem
               || type == ControlType.MenuItem
               || type == ControlType.Custom;
    }

    private static bool IsInteractiveUiaByPatterns(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.Toggle.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.SelectionItem.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.RangeValue.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            if (element.Patterns.ScrollItem.IsSupported)
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private FindElementsResponse FindElementsUia(
        AutomationElement rootElement,
        string rootXPath,
        ITreeWalker walker,
        FindElementsQuery? query,
        bool visibleOnly,
        bool includeOffViewport,
        Rect? viewportBounds,
        bool interactiveOnly,
        InteractiveMode interactiveMode,
        int maxResults,
        int maxNodes,
        FindReturnFields returnFields,
        bool includeElementIds,
        long windowHandle,
        CancellationToken cancellationToken)
    {
        if (query is null ||
            (string.IsNullOrWhiteSpace(query.AutomationIdEquals) &&
             string.IsNullOrWhiteSpace(query.AutomationIdContains) &&
             string.IsNullOrWhiteSpace(query.NameEquals) &&
             string.IsNullOrWhiteSpace(query.NameContains) &&
             string.IsNullOrWhiteSpace(query.TypeEquals)))
        {
            throw new ArgumentException("find_elements requires a non-empty query.");
        }

        var matches = new List<ElementRef>();
        var scannedNodes = 0;
        var truncated = false;
        string? truncatedReason = null;

        var stack = new Stack<(AutomationElement Element, string XPath)>();
        stack.Push((rootElement, rootXPath));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scannedNodes >= maxNodes)
            {
                truncated = true;
                truncatedReason = "maxNodes";
                break;
            }

            var (current, currentXPath) = stack.Pop();
            scannedNodes++;

            if (!AreSameElement(current, rootElement) && visibleOnly && !IsVisibleInSearch(current, includeOffViewport, viewportBounds))
            {
                continue;
            }

            if (IsQueryMatchUia(current, query) && (!interactiveOnly || IsInteractiveUia(current, interactiveMode)))
            {
                string? elementId = null;
                if (includeElementIds)
                {
                    elementId = _elementHandles.RegisterUia(
                        windowHandle,
                        currentXPath,
                        TryGetRuntimeId(current),
                        current.ControlType.ToString(),
                        GetAutomationId(current),
                        GetName(current),
                        GetClassName(current));
                }

                matches.Add(BuildElementRefUia(current, currentXPath, returnFields, elementId));
                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    truncatedReason = "maxResults";
                    break;
                }
            }

            var rawChildren = GetChildren(current, walker).ToArray();
            if (rawChildren.Length == 0)
            {
                continue;
            }

            var labels = rawChildren.Select(GetXPathLabel).ToArray();
            var countsByLabel = labels
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var runningIndexByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = rawChildren.Length - 1; i >= 0; i--)
            {
                var child = rawChildren[i];
                var label = labels[i];

                runningIndexByLabel.TryGetValue(label, out var currentIndex);
                currentIndex++;
                runningIndexByLabel[label] = currentIndex;

                var includeIndex = countsByLabel[label] > 1;
                var segment = includeIndex ? $"{label}[{countsByLabel[label] - currentIndex + 1}]" : label;

                // Note: we iterate backwards; adjust index to keep XPath stable.
                if (includeIndex)
                {
                    var oneBasedForwardIndex = countsByLabel[label] - currentIndex + 1;
                    segment = $"{label}[{oneBasedForwardIndex}]";
                }

                var childXPath = $"{currentXPath}/{segment}";
                stack.Push((child, childXPath));
            }
        }

        return new FindElementsResponse(
            BackendUsed: InspectionBackend.Uia,
            Matches: matches,
            ReturnedMatches: matches.Count,
            ScannedNodes: scannedNodes,
            Truncated: truncated,
            TruncatedReason: truncatedReason,
            Warnings: null);
    }

    private static bool IsVisibleInSearch(AutomationElement element, bool includeOffViewport, Rect? viewportBounds)
    {
        if (!HasValidBounds(element))
        {
            return false;
        }

        if (!includeOffViewport)
        {
            try
            {
                if (element.IsOffscreen)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            if (viewportBounds is not null)
            {
                try
                {
                    var bounds = ToRect(element.BoundingRectangle);
                    return RectIntersects(bounds, viewportBounds);
                }
                catch
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsQueryMatchUia(AutomationElement element, FindElementsQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.TypeEquals))
        {
            var type = element.ControlType.ToString();
            var className = GetClassName(element);
            if (!string.Equals(type, query.TypeEquals, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(className, query.TypeEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.AutomationIdEquals))
        {
            var id = GetAutomationId(element);
            if (!string.Equals(id, query.AutomationIdEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.AutomationIdContains))
        {
            var id = GetAutomationId(element) ?? string.Empty;
            if (id.IndexOf(query.AutomationIdContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.NameEquals))
        {
            var name = GetName(element);
            if (!string.Equals(name, query.NameEquals, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.NameContains))
        {
            var name = GetName(element) ?? string.Empty;
            if (name.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static ElementRef BuildElementRefUia(AutomationElement element, string xpath, FindReturnFields returnFields, string? elementId)
    {
        if (returnFields == FindReturnFields.Standard)
        {
            return new ElementRef(
                Type: element.ControlType.ToString(),
                AutomationId: GetAutomationId(element),
                Name: GetName(element),
                XPath: xpath,
                ClassName: GetClassName(element),
                Bounds: ToRect(element.BoundingRectangle),
                ElementId: elementId);
        }

        return new ElementRef(
            Type: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ElementId: elementId);
    }

    private static IReadOnlyList<AutomationElement> GetChildren(AutomationElement element, ITreeWalker walker)
    {
        var children = new List<AutomationElement>();

        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch
        {
            return children;
        }

        while (child is not null)
        {
            children.Add(child);

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch
            {
                break;
            }
        }

        return children;
    }

    private static void PopulateProperties(AutomationElement element, IDictionary<string, JsonNode?> destination)
    {
        var props = element.Properties;
        var declaredType = typeof(AutomationElement).GetProperty(nameof(AutomationElement.Properties))?.PropertyType
            ?? props.GetType();

        var properties = declaredType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var property in properties)
        {
            object? wrapper;
            try
            {
                wrapper = property.GetValue(props);
            }
            catch (Exception ex)
            {
                destination[property.Name] = JsonValue.Create($"<error: {ex.Message}>");
                continue;
            }

            if (wrapper is null)
            {
                continue;
            }

            var value = TryGetWrapperValue(wrapper);
            destination[property.Name] = ToJsonNode(value);
        }
    }

    private static void PopulatePatterns(AutomationElement element, IDictionary<string, JsonNode?> destination)
    {
        var patternsObject = element.Patterns;
        var declaredType = typeof(AutomationElement).GetProperty(nameof(AutomationElement.Patterns))?.PropertyType
            ?? patternsObject.GetType();

        var patterns = declaredType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var patternProperty in patterns)
        {
            object? wrapper;
            try
            {
                wrapper = patternProperty.GetValue(patternsObject);
            }
            catch (Exception ex)
            {
                destination[patternProperty.Name] = new JsonObject
                {
                    ["isSupported"] = false,
                    ["error"] = ex.Message
                };
                continue;
            }

            if (wrapper is null)
            {
                continue;
            }

            var isSupported = TryGetBooleanProperty(wrapper, "IsSupported");
            if (isSupported is not true)
            {
                continue;
            }

            var json = new JsonObject
            {
                ["isSupported"] = true
            };

            var patternInstance = TryGetProperty(wrapper, "Pattern");
            if (patternInstance is not null)
            {
                var values = ExtractPatternValues(patternProperty.Name, patternInstance);
                if (values.Count > 0)
                {
                    json["values"] = values;
                }
            }

            destination[patternProperty.Name] = json;
        }
    }

    private static JsonObject ExtractPatternValues(string patternName, object patternInstance)
    {
        var values = new JsonObject();

        switch (patternName)
        {
            case "Value":
                AddPatternValue(values, patternInstance, "Value");
                AddPatternValue(values, patternInstance, "IsReadOnly");
                break;
            case "Toggle":
                AddPatternValue(values, patternInstance, "ToggleState");
                break;
            case "RangeValue":
                AddPatternValue(values, patternInstance, "Value");
                AddPatternValue(values, patternInstance, "Minimum");
                AddPatternValue(values, patternInstance, "Maximum");
                AddPatternValue(values, patternInstance, "IsReadOnly");
                break;
            case "Scroll":
                AddPatternValue(values, patternInstance, "HorizontallyScrollable");
                AddPatternValue(values, patternInstance, "VerticallyScrollable");
                AddPatternValue(values, patternInstance, "HorizontalScrollPercent");
                AddPatternValue(values, patternInstance, "VerticalScrollPercent");
                AddPatternValue(values, patternInstance, "HorizontalViewSize");
                AddPatternValue(values, patternInstance, "VerticalViewSize");
                break;
            case "ExpandCollapse":
                AddPatternValue(values, patternInstance, "ExpandCollapseState");
                break;
            case "SelectionItem":
                AddPatternValue(values, patternInstance, "IsSelected");
                break;
            case "Selection":
                AddPatternValue(values, patternInstance, "CanSelectMultiple");
                AddPatternValue(values, patternInstance, "IsSelectionRequired");
                break;
            case "Window":
                AddPatternValue(values, patternInstance, "IsModal");
                AddPatternValue(values, patternInstance, "IsTopmost");
                AddPatternValue(values, patternInstance, "WindowInteractionState");
                AddPatternValue(values, patternInstance, "WindowVisualState");
                break;
        }

        return values;
    }

    private static void AddPatternValue(JsonObject values, object patternInstance, string propertyName)
    {
        var property = patternInstance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
        {
            return;
        }

        object? value;
        try
        {
            value = property.GetValue(patternInstance);
        }
        catch
        {
            return;
        }

        var unwrapped = value is null ? null : TryGetWrapperValue(value) ?? value;
        values[propertyName] = ToJsonNode(unwrapped);
    }

    private static object? TryGetWrapperValue(object wrapper)
    {
        var type = wrapper.GetType();
        var valueOrDefault = type.GetProperty("ValueOrDefault", BindingFlags.Instance | BindingFlags.Public);
        if (valueOrDefault is not null && valueOrDefault.CanRead)
        {
            try
            {
                return valueOrDefault.GetValue(wrapper);
            }
            catch
            {
                return null;
            }
        }

        var value = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        if (value is not null && value.CanRead)
        {
            try
            {
                return value.GetValue(wrapper);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static object? TryGetProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
        {
            return null;
        }

        try
        {
            return property.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetBooleanProperty(object instance, string propertyName)
    {
        var value = TryGetProperty(instance, propertyName);
        return value as bool? ?? (value is bool b ? b : null);
    }

    private static string? GetAutomationId(AutomationElement element)
    {
        try
        {
            var value = element.Properties.AutomationId.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetName(AutomationElement element)
    {
        try
        {
            var value = element.Properties.Name.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetClassName(AutomationElement element)
    {
        try
        {
            var value = element.Properties.ClassName.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static Rect ToRect(Rectangle rectangle) =>
        new(X: rectangle.Left, Y: rectangle.Top, Width: rectangle.Width, Height: rectangle.Height);

    private static bool RectIntersects(Rect a, Rect b)
    {
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
        {
            return false;
        }

        var ax2 = (long)a.X + a.Width;
        var ay2 = (long)a.Y + a.Height;
        var bx2 = (long)b.X + b.Width;
        var by2 = (long)b.Y + b.Height;

        return a.X < bx2 && ax2 > b.X && a.Y < by2 && ay2 > b.Y;
    }

    private static bool RectContains(Rect outer, Rect inner)
    {
        if (outer.Width <= 0 || outer.Height <= 0 || inner.Width <= 0 || inner.Height <= 0)
        {
            return false;
        }

        var outerRight = (long)outer.X + outer.Width;
        var outerBottom = (long)outer.Y + outer.Height;
        var innerRight = (long)inner.X + inner.Width;
        var innerBottom = (long)inner.Y + inner.Height;

        return inner.X >= outer.X &&
               inner.Y >= outer.Y &&
               innerRight <= outerRight &&
               innerBottom <= outerBottom;
    }

    private static bool IsRectVisibleEnough(Rect bounds, Rect containerBounds, bool fullyVisible) =>
        fullyVisible ? RectContains(containerBounds, bounds) : RectIntersects(bounds, containerBounds);

    private static string FormatRect(Rect rect) =>
        rect.Width <= 0 && rect.Height <= 0
            ? "empty"
            : $"x={rect.X},y={rect.Y},w={rect.Width},h={rect.Height}";

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return JsonValue.Create(s);
        }

        if (value is bool b)
        {
            return JsonValue.Create(b);
        }

        if (value is int i)
        {
            return JsonValue.Create(i);
        }

        if (value is long l)
        {
            return JsonValue.Create(l);
        }

        if (value is double d)
        {
            if (double.IsNaN(d))
            {
                return JsonValue.Create("{NaN}");
            }

            if (double.IsPositiveInfinity(d))
            {
                return JsonValue.Create("{Infinity}");
            }

            if (double.IsNegativeInfinity(d))
            {
                return JsonValue.Create("{-Infinity}");
            }

            return JsonValue.Create(d);
        }

        if (value is float f)
        {
            if (float.IsNaN(f))
            {
                return JsonValue.Create("{NaN}");
            }

            if (float.IsPositiveInfinity(f))
            {
                return JsonValue.Create("{Infinity}");
            }

            if (float.IsNegativeInfinity(f))
            {
                return JsonValue.Create("{-Infinity}");
            }

            return JsonValue.Create(f);
        }

        if (value is decimal dec)
        {
            return JsonValue.Create(dec);
        }

        if (value is Enum e)
        {
            return JsonValue.Create(e.ToString());
        }

        if (value is IntPtr ptr)
        {
            return JsonValue.Create(ptr.ToInt64());
        }

        if (value is Guid guid)
        {
            return JsonValue.Create(guid.ToString());
        }

        if (value is Rectangle rect)
        {
            return JsonSerializer.SerializeToNode(ToRect(rect));
        }

        if (value is AutomationElement element)
        {
            return new JsonObject
            {
                ["elementType"] = element.ControlType.ToString(),
                ["automationId"] = GetAutomationId(element),
                ["name"] = GetName(element),
                ["className"] = GetClassName(element)
            };
        }

        if (value is IEnumerable<AutomationElement> elements)
        {
            var array = new JsonArray();
            foreach (var item in elements)
            {
                array.Add(ToJsonNode(item));
            }

            return array;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var array = new JsonArray();
            foreach (var item in enumerable)
            {
                array.Add(ToJsonNode(item));
            }

            return array;
        }

        try
        {
            return JsonSerializer.SerializeToNode(value);
        }
        catch
        {
            return JsonValue.Create(value.ToString());
        }
    }

    private void Cleanup()
    {
        CleanupAgent();

        if (_automation is not null)
        {
            _automation.Dispose();
            _automation = null;
        }

        if (_application is not null)
        {
            _application.Dispose();
            _application = null;
        }
    }
}
