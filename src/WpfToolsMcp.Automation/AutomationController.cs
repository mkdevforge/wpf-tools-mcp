using System.ComponentModel;
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
    private ProcessInstanceIdentity? _processIdentity;
    private readonly SemaphoreSlim _toolMutex = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly string _resourceOwnerId = Guid.NewGuid().ToString("N");
    private LastHighlightRequest? _lastHighlight;
    private int _disposeStarted;

    private static readonly int UiDelayMs = GetEnvInt("WPF_TOOLS_MCP_UI_DELAY_MS", defaultValue: 0, minValue: 0, maxValue: 1000);
    private static readonly int UiDelayScrollMs = GetEnvInt("WPF_TOOLS_MCP_UI_SCROLL_DELAY_MS", defaultValue: 15, minValue: 0, maxValue: 1000);
    private static readonly int UiDelayWindowSettleMs = GetEnvInt("WPF_TOOLS_MCP_UI_WINDOW_SETTLE_MS", defaultValue: 25, minValue: 0, maxValue: 5000);
    private static readonly bool ScreenshotDebugEnabled = GetEnvFlag("WPF_TOOLS_MCP_DEBUG_SCREENSHOT");
    private const int MaximumElementProperties = 200;
    internal const int MaximumUiaMappingCandidates = 10;
    private static readonly HashSet<string> SummaryElementPropertyNames = new(StringComparer.Ordinal)
    {
        "AcceleratorKey",
        "AccessKey",
        "AriaProperties",
        "AriaRole",
        "ClickablePoint",
        "FrameworkId",
        "FullDescription",
        "HasKeyboardFocus",
        "HelpText",
        "IsContentElement",
        "IsControlElement",
        "IsKeyboardFocusable",
        "IsPassword",
        "IsRequiredForForm",
        "ItemStatus",
        "ItemType",
        "LabeledBy",
        "LocalizedControlType",
        "Orientation",
        "ProcessId"
    };

    private sealed record LastHighlightRequest(Rect Bounds, string Color, int Thickness, DateTime ExpiresAtUtc);

    public bool IsAttached =>
        IsApplicationRunning(_application) &&
        _processIdentity is ProcessInstanceIdentity identity &&
        ProcessTargetResolver.IsCurrent(identity);
    internal ProcessInstanceIdentity AttachedProcessIdentity =>
        _processIdentity ?? throw new InvalidOperationException("No stable process identity is attached.");
    internal bool IsDisposing => Volatile.Read(ref _disposeStarted) != 0;
    internal CancellationToken LifetimeToken => _lifetimeCts.Token;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();
        _toolMutex.Wait();
        try
        {
            HighlightOverlay.Hide(_resourceOwnerId);
            _lastHighlight = null;
            _traceSession = null;
            _elementHandles.Clear();
            Cleanup();
        }
        finally
        {
            _toolMutex.Release();
            _lifetimeCts.Dispose();
        }
    }

    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfUnavailableForToolCall();

        await _toolMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailableForToolCall();
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
        ThrowIfUnavailableForToolCall();

        await _toolMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailableForToolCall();
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
            throw FailureDiagnostics.Exception(
                code: "process_not_found",
                stage: "process_discovery",
                detail: "The requested executable was not found.",
                retryable: false,
                recoveryActions: [FailureDiagnostics.Recovery.RepairInstallation]);
        }

        var waitMs = Math.Clamp(request.WaitForMainWindowMs, 1000, 120000);
        var waitTimeout = TimeSpan.FromMilliseconds(waitMs);
        Exception? lastLaunchError = null;

        foreach (var launchStrategy in CreateLaunchStartInfos(request))
        {
            try
            {
                _application = Application.Launch(launchStrategy.StartInfo);
                ResolvedProcessTarget? launchedTarget = null;
                Exception? launchInitError = null;
                try
                {
                    launchedTarget = ProcessTargetResolver.ResolveByPid(_application.ProcessId);
                }
                catch (ActionableFailureException ex) when (
                    request.ReuseExistingInstance &&
                    string.Equals(ex.Failure.Code, "process_not_found", StringComparison.Ordinal))
                {
                    launchInitError = ex;
                }

                if (launchedTarget is not null &&
                    TryInitializeApplication(_application, waitTimeout, out launchInitError))
                {
                    EnsureCurrentProcessInstance(launchedTarget.Identity, "launched process initialization");

                    _processIdentity = launchedTarget.Identity;
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
            catch (ProcessSelectionAmbiguityException)
            {
                Cleanup();
                throw;
            }
            catch (Exception ex)
            {
                lastLaunchError = ex;
                Cleanup();
            }
        }

        if (lastLaunchError is ActionableFailureException actionable)
        {
            throw actionable;
        }

        if (IsExecutableNotFound(lastLaunchError))
        {
            throw FailureDiagnostics.Exception(
                code: FailureDiagnostics.Codes.ProcessNotFound,
                stage: FailureDiagnostics.Stages.ProcessDiscovery,
                detail: "The requested executable was not found.",
                retryable: false,
                recoveryActions: [FailureDiagnostics.Recovery.RepairInstallation],
                inner: lastLaunchError);
        }

        throw FailureDiagnostics.Exception(
            code: "attachment_failed",
            stage: "attachment",
            detail: "The application could not be launched and attached.",
            retryable: true,
            recoveryActions: ["retry", "reattach"],
            inner: lastLaunchError);
    }

    private static bool IsExecutableNotFound(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or DirectoryNotFoundException ||
                current is Win32Exception { NativeErrorCode: 2 or 3 })
            {
                return true;
            }
        }

        return false;
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
            ResolvedProcessTarget target;
            try
            {
                target = ProcessTargetResolver.ResolveByName(processName);
            }
            catch (ProcessSelectionAmbiguityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex;
                Thread.Sleep(200);
                continue;
            }

            Application? attached = null;
            try
            {
                attached = Application.Attach(target.Identity.Pid);
                if (!TryInitializeApplication(attached, perAttemptTimeout, out var initError))
                {
                    error = initError;
                    attached.Dispose();
                    Thread.Sleep(200);
                    continue;
                }

                try
                {
                    EnsureCurrentProcessInstance(target.Identity, "fallback attachment");
                }
                catch (InvalidOperationException ex)
                {
                    error = ex;
                    attached.Dispose();
                    Cleanup();
                    Thread.Sleep(200);
                    continue;
                }

                _application = attached;
                _processIdentity = target.Identity;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                attached?.Dispose();
            }

            Thread.Sleep(200);
        }

        return false;
    }

    public Task<AttachToAppResponse> AttachAsync(AttachToAppRequest request, CancellationToken cancellationToken = default)
    {
        EnsureNotAttached();
        cancellationToken.ThrowIfCancellationRequested();
        ProcessIntegrityLevelComparison? integrityComparison = null;

        try
        {
            var target = ProcessTargetResolver.Resolve(request);
            if (ProcessIntegrityLevelInspector.TryCompareWithCurrentProcess(
                    target.Identity.Pid,
                    out var measuredIntegrity))
            {
                integrityComparison = measuredIntegrity;
            }

            (_application, _automation) = CreateAttachment(target.Identity.Pid);

            EnsureCurrentProcessInstance(target.Identity, "attachment initialization");

            _processIdentity = target.Identity;
            var response = new AttachToAppResponse(SessionId: "", _application.ProcessId, _application.Name)
            {
                ProcessInstanceId = target.Identity.Value
            };
            return Task.FromResult(response);
        }
        catch (OperationCanceledException)
        {
            Cleanup();
            throw;
        }
        catch (ProcessSelectionAmbiguityException)
        {
            Cleanup();
            throw;
        }
        catch (ActionableFailureException)
        {
            Cleanup();
            throw;
        }
        catch (Exception ex)
        {
            Cleanup();
            throw FailureDiagnostics.CreateException(
                ex,
                FailureDiagnostics.Stages.Attachment,
                integrityComparison);
        }
    }

    private (Application Application, UIA3Automation Automation) CreateAttachment(int pid)
    {
        var application = Application.Attach(pid);
        UIA3Automation? automation = null;
        try
        {
            application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(10));
            application.WaitWhileBusy(TimeSpan.FromSeconds(10));
            automation = new UIA3Automation();
            _ = FindMainWindow(application, automation);
            return (application, automation);
        }
        catch
        {
            automation?.Dispose();
            application.Dispose();
            throw;
        }
    }

    private static void EnsureCurrentProcessInstance(ProcessInstanceIdentity identity, string operation)
    {
        switch (ProcessTargetResolver.Observe(identity))
        {
            case ProcessInstanceState.Current:
                return;
            case ProcessInstanceState.ExitedOrReused:
                throw FailureDiagnostics.Exception(
                    code: "stale_process_candidate",
                    stage: "process_discovery",
                    detail: "The selected process exited or its PID was reused before attachment completed.",
                    retryable: false,
                    recoveryActions: ["select_process_instance"]);
            default:
                throw FailureDiagnostics.Exception(
                    code: "process_identity_unavailable",
                    stage: "process_discovery",
                    detail: "The selected process identity could not be verified before attachment completed.",
                    retryable: true,
                    recoveryActions: ["retry", "select_process_instance"]);
        }
    }

    public Task<CloseAppResponse> CloseAsync(CloseAppRequest request, CancellationToken cancellationToken = default) =>
        EndApplicationAsync(
            traceName: "close_session",
            timeoutMs: request.TimeoutMs,
            closeRequested: true,
            forceTerminationRequested: request.Force,
            cancellationToken);

    public Task<CloseAppResponse> CloseApplicationAsync(int timeoutMs, CancellationToken cancellationToken = default) =>
        EndApplicationAsync(
            traceName: "close_app",
            timeoutMs,
            closeRequested: true,
            forceTerminationRequested: false,
            cancellationToken);

    public Task<CloseAppResponse> TerminateApplicationAsync(int timeoutMs, CancellationToken cancellationToken = default) =>
        EndApplicationAsync(
            traceName: "terminate_app",
            timeoutMs,
            closeRequested: false,
            forceTerminationRequested: true,
            cancellationToken);

    private async Task<CloseAppResponse> EndApplicationAsync(
        string traceName,
        int timeoutMs,
        bool closeRequested,
        bool forceTerminationRequested,
        CancellationToken cancellationToken)
    {
        var trace = BeginTraceSpan(traceName);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timeout = Math.Clamp(timeoutMs <= 0 ? 5000 : timeoutMs, 100, 120_000);
            var application = _application;
            if (application is null || !IsApplicationRunning(application))
            {
                var alreadyExited = CreateCloseAppResponse(
                    processExited: true,
                    processAlreadyExited: true,
                    closeRequested,
                    closeRequestDispatched: false,
                    forceTerminationRequested,
                    forceTerminationAttempted: false);
                trace?.SetSummary("process_exited=true already_exited=true");
                return alreadyExited;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(application.ProcessId);
            }
            catch (ArgumentException)
            {
                var alreadyExited = CreateCloseAppResponse(
                    processExited: true,
                    processAlreadyExited: true,
                    closeRequested,
                    closeRequestDispatched: false,
                    forceTerminationRequested,
                    forceTerminationAttempted: false);
                trace?.SetSummary("process_exited=true already_exited=true");
                return alreadyExited;
            }

            using (process)
            {
                if (TryObserveProcessExit(process))
                {
                    var alreadyExited = CreateCloseAppResponse(
                        processExited: true,
                        processAlreadyExited: true,
                        closeRequested,
                        closeRequestDispatched: false,
                        forceTerminationRequested,
                        forceTerminationAttempted: false);
                    trace?.SetSummary("process_exited=true already_exited=true");
                    return alreadyExited;
                }

                var closeRequestDispatched = false;
                var forceTerminationAttempted = false;
                var processExited = false;

                if (closeRequested)
                {
                    closeRequestDispatched = TryDispatchCloseRequest(process);
                    processExited = await WaitForProcessExitAsync(process, timeout, cancellationToken).ConfigureAwait(false);
                }

                if (!processExited && forceTerminationRequested)
                {
                    if (TryObserveProcessExit(process))
                    {
                        processExited = true;
                    }
                    else
                    {
                        var terminationDispatched = TryTerminateProcess(process, out forceTerminationAttempted);
                        processExited = terminationDispatched
                            ? await WaitForProcessExitAsync(process, timeout, cancellationToken).ConfigureAwait(false)
                            : TryObserveProcessExit(process);
                    }
                }

                if (!processExited)
                {
                    processExited = TryObserveProcessExit(process);
                }

                var result = CreateCloseAppResponse(
                    processExited,
                    processAlreadyExited: false,
                    closeRequested,
                    closeRequestDispatched,
                    forceTerminationRequested,
                    forceTerminationAttempted);
                trace?.SetSummary(
                    $"process_exited={result.ProcessExited} close_requested={closeRequested} close_dispatched={closeRequestDispatched} force_requested={forceTerminationRequested} force_attempted={forceTerminationAttempted}");
                return result;
            }
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            Cleanup();
            trace?.Dispose();
        }
    }

    private static CloseAppResponse CreateCloseAppResponse(
        bool processExited,
        bool processAlreadyExited,
        bool closeRequested,
        bool closeRequestDispatched,
        bool forceTerminationRequested,
        bool forceTerminationAttempted) =>
        new(
            Closed: processExited,
            ProcessExited: processExited,
            ProcessAlreadyExited: processAlreadyExited)
        {
            CloseRequested = closeRequested,
            CloseRequestDispatched = closeRequestDispatched,
            ForceTerminationRequested = forceTerminationRequested,
            ForceTerminationAttempted = forceTerminationAttempted
        };

    private static bool TryDispatchCloseRequest(Process process)
    {
        try
        {
            return !process.HasExited && process.CloseMainWindow();
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

    private static bool TryTerminateProcess(Process process, out bool attempted)
    {
        attempted = false;
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            attempted = true;
            process.Kill();
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

    private static async Task<bool> WaitForProcessExitAsync(
        Process process,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (TryObserveProcessExit(process))
        {
            return true;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TryObserveProcessExit(process);
        }
        catch (InvalidOperationException)
        {
            return TryObserveProcessExit(process);
        }
        catch (Win32Exception)
        {
            return TryObserveProcessExit(process);
        }
    }

    private static bool TryObserveProcessExit(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
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

            var autoBackendRoute = requestedBackend == InspectionBackend.Auto
                ? GetAutoBackendRoute(window)
                : AutoBackendRoute.ProbeWpfThenUia;

            if (requestedBackend == InspectionBackend.Auto &&
                autoInject &&
                !hasElementId &&
                request.Locator is not null &&
                autoBackendRoute != AutoBackendRoute.Uia)
            {
                var autoClient = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
                if (autoClient is not null)
                {
                    elementBackend = InspectionBackend.Wpf;
                }
            }

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
            ViewportConditions? capturedViewport = null;
            var recovered = false;

            async Task<(Bitmap Bitmap, Rect CapturedBounds, Rect? RequestedBounds, bool WasClipped, ScreenshotCaptureMode CaptureModeUsed)> CaptureWithViewportAsync(
                Rect? requestedBounds)
            {
                var stableCapture = await ViewportCaptureStabilityCoordinator.CaptureAsync(
                    request.IncludeViewport || request.Correlation is not null,
                    () => CaptureViewportConditions(new IntPtr(windowHandleUsed)),
                    () => CaptureScreenshotWithMetadata(window, requestedBounds, mode, area, clip, includeOverlay: false),
                    rejected => rejected.Bitmap.Dispose(),
                    async token =>
                    {
                        _ = await WaitForStableViewportAsync(new IntPtr(windowHandleUsed), token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);

                capturedViewport = stableCapture.Viewport;
                return stableCapture.Capture;
            }

            try
            {
                if (hasElementId)
                {
                    var elementId = request.ElementId!.Trim();
                    if (elementBackend == InspectionBackend.Uia)
                    {
                        element = ResolveUiaElementById(
                            window,
                            rawWalker,
                            elementId,
                            out _,
                            request.RequireStableElementIdentity || autoScroll
                                ? UiaHandleResolutionMode.RequireRegisteredIdentity
                                : UiaHandleResolutionMode.ObserveCurrentXPathOccupant);

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
                            throwIfScrollFailed: autoScroll,
                            allowHandleRecovery: !request.RequireStableElementIdentity).ConfigureAwait(false);
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
                                    await ScrollToElementCoreAsync(
                                        new ScrollToElementRequest(
                                            WindowHandle: windowHandleUsed,
                                            ElementId: request.ElementId!.Trim(),
                                            AutoWait: false),
                                        cancellationToken: cancellationToken).ConfigureAwait(false);
                                }
                                else if (request.Locator is not null)
                                {
                                    await ScrollToElementCoreAsync(
                                        new ScrollToElementRequest(
                                            WindowHandle: windowHandleUsed,
                                            Locator: request.Locator,
                                            AutoWait: false),
                                        cancellationToken: cancellationToken).ConfigureAwait(false);
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
                                    throwIfScrollFailed: true,
                                    allowHandleRecovery: !request.RequireStableElementIdentity).ConfigureAwait(false);
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
                    capture = await CaptureWithViewportAsync(requestedBounds).ConfigureAwait(false);
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
                                await ScrollToElementCoreAsync(
                                    new ScrollToElementRequest(
                                        WindowHandle: windowHandleUsed,
                                        ElementId: request.ElementId!.Trim(),
                                        AutoWait: false),
                                    cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                            else if (request.Locator is not null)
                            {
                                await ScrollToElementCoreAsync(
                                    new ScrollToElementRequest(
                                        WindowHandle: windowHandleUsed,
                                        Locator: request.Locator,
                                        AutoWait: false),
                                    cancellationToken: cancellationToken).ConfigureAwait(false);
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

                    capture = await CaptureWithViewportAsync(requestedBounds).ConfigureAwait(false);
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
                    autoBackendRoute != AutoBackendRoute.Uia &&
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

                        capture = await CaptureWithViewportAsync(wpfElementBounds).ConfigureAwait(false);
                        recovered = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception fallbackEx)
                    {
                        throw new InvalidOperationException(
                            "take_screenshot failed using both UIA and WPF fallback.",
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

            ScreenshotCorrelationResult? correlation = null;
            if (request.Correlation is { } correlationOptions)
            {
                var viewport = capturedViewport
                    ?? throw new InvalidOperationException("screenshot_correlation_missing_viewport_context");
                var capturedWindow = ToWindowInfo(window) with
                {
                    Handle = windowHandleUsed,
                    Bounds = viewport.OuterBoundsPhysicalPixels
                };
                var captureContext = new ScreenshotCaptureContext(
                    CaptureModeRequested: request.CaptureMode,
                    CaptureModeUsed: captureModeUsed,
                    Area: request.Area,
                    Clip: request.Clip,
                    Window: capturedWindow,
                    CapturedBounds: capturedBounds,
                    RequestedBounds: requestedBoundsUsed,
                    WasClipped: wasClipped,
                    Viewport: viewport,
                    Obscuration: CaptureScreenshotObscuration(
                        new IntPtr(windowHandleUsed),
                        capturedBounds,
                        captureModeUsed));
                correlation = await CorrelateScreenshotAsync(
                    window,
                    windowHandleUsed,
                    bitmapToSave,
                    capturedBounds,
                    correlationOptions,
                    captureContext,
                    cancellationToken).ConfigureAwait(false);
            }

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
                Base64: base64)
            {
                Viewport = capturedViewport,
                Correlation = correlation
            };

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

    internal static bool IsEligibleAutoScreenshotFallback(Exception ex)
    {
        if (IsPerWindowAutoWpfMiss(ex))
        {
            return true;
        }

        var message = GetInternalFailureMessage(ex);
        ex = ex.GetBaseException();

        if (ex is ArgumentException)
        {
            return false;
        }

        if (ex is not InvalidOperationException)
        {
            return false;
        }

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

        var imageBounds = ScreenshotCorrelationGeometry.MapScreenRegionToImage(
            annotationBounds,
            bitmap.Width,
            bitmap.Height,
            capturedBounds);
        if (imageBounds is null)
        {
            return;
        }

        var x = imageBounds.X;
        var y = imageBounds.Y;
        var w = imageBounds.Width;
        var h = imageBounds.Height;

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
        ArgumentNullException.ThrowIfNull(request);

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

            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var effects = new InteractionEffectTracker();
            await EnsureWindowForegroundAsync(
                window,
                operation: "set_active_window",
                policy,
                effects,
                settleDelayMs: UiDelayWindowSettleMs,
                cancellationToken).ConfigureAwait(false);

            var response = new FocusWindowResponse(
                Focused: true,
                Handle: window.Properties.NativeWindowHandle.Value.ToInt64(),
                Title: GetWindowTitle(window),
                Effects: effects.ToContract());

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

    internal Task<GetActiveWindowResponse> GetWindowMetadataAsync(
        long? windowHandle = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var application = EnsureAttached();
        var automation = EnsureAutomation();
        var window = windowHandle is long requestedHandle
            ? FindWindowByHandle(application, automation, requestedHandle)
            : FindMainWindow(application, automation);

        return Task.FromResult(new GetActiveWindowResponse(
            Handle: window.Properties.NativeWindowHandle.Value.ToInt64(),
            Title: GetWindowTitle(window)));
    }

    public async Task<ClickElementResponse> ClickElementAsync(
        ClickElementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("click_element");
        try
        {
        var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
        var effects = new InteractionEffectTracker();
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
        AutomationElement? element = null;

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

            if (handle.Backend == InspectionBackend.Wpf)
            {
                await EnsureWpfHandleEnabledOrThrowAsync(elementId, "click_element", cancellationToken).ConfigureAwait(false);

                if (ShouldUseDirectWpfMouseClick(request))
                {
                    var response = await ClickWpfHandleWithMouseAsync(
                        window,
                        handle,
                        request,
                        policy,
                        effects,
                        cancellationToken).ConfigureAwait(false);
                    trace?.SetSummary("method=mouse_wpf");
                    return response;
                }

                var wpfClick = await TryInvokeWpfAsync(elementId, handle, cancellationToken).ConfigureAwait(false);
                if (wpfClick is not null)
                {
                    effects.MarkSemantic();
                    trace?.SetSummary($"method={wpfClick.MethodUsed}");
                    return new ClickElementResponse(true, wpfClick.MethodUsed ?? "wpf_invoke", effects.ToContract());
                }

                try
                {
                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, elementId, handle, out _);
                }
                catch (InvalidOperationException)
                {
                    var response = await ClickWpfHandleWithMouseAsync(
                        window,
                        handle,
                        request,
                        policy,
                        effects,
                        cancellationToken).ConfigureAwait(false);
                    trace?.SetSummary("method=mouse_wpf");
                    return response;
                }
            }

            if (handle.Backend == InspectionBackend.Uia)
            {
                element = ResolveUiaElementById(
                    window,
                    rawWalker,
                    elementId,
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{elementId}' has unsupported backend '{handle.Backend}'.");
            }
        }
        else
        {
            window = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);

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

                if (ShouldUseDirectWpfMouseClick(request))
                {
                    var response = await ClickWpfHandleWithMouseAsync(
                        window,
                        wpfTarget.Handle,
                        request,
                        policy,
                        effects,
                        cancellationToken).ConfigureAwait(false);
                    trace?.SetSummary("method=mouse_wpf");
                    return response;
                }

                var wpfClick = await TryInvokeWpfAsync(
                    wpfTarget.ElementId,
                    wpfTarget.Handle,
                    cancellationToken).ConfigureAwait(false);
                if (wpfClick is not null)
                {
                    effects.MarkSemantic();
                    trace?.SetSummary($"method={wpfClick.MethodUsed}");
                    return new ClickElementResponse(true, wpfClick.MethodUsed ?? "wpf_invoke", effects.ToContract());
                }

                try
                {
                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, wpfTarget.ElementId, wpfTarget.Handle, out _);
                }
                catch (InvalidOperationException)
                {
                    var response = await ClickWpfHandleWithMouseAsync(
                        window,
                        wpfTarget.Handle,
                        request,
                        policy,
                        effects,
                        cancellationToken).ConfigureAwait(false);
                    trace?.SetSummary("method=mouse_wpf");
                    return response;
                }
            }

            if (wpfTarget is null)
            {
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
        }

        if (element is null)
        {
            throw new InvalidOperationException("Failed to resolve target element.");
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
            if (element.Patterns.Invoke.PatternOrDefault is { } invoke)
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
                effects.MarkSemantic();
                trace?.SetSummary("method=invoke");
                return new ClickElementResponse(Clicked: true, MethodUsed: "invoke", Effects: effects.ToContract());
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
                effects.MarkSemantic();
                trace?.SetSummary("method=toggle");
                return new ClickElementResponse(Clicked: true, MethodUsed: "toggle", Effects: effects.ToContract());
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
                effects.MarkSemantic();
                trace?.SetSummary("method=selectionItem");
                return new ClickElementResponse(Clicked: true, MethodUsed: "selectionItem", Effects: effects.ToContract());
            }
        }

        await PrepareWindowForPhysicalInputAsync(
            window,
            operation: "click_element",
            policy,
            effects,
            semanticAlternative: "The target exposes no supported semantic click pattern.",
            cancellationToken).ConfigureAwait(false);

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
        effects.MarkMouseInput();

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }
        trace?.SetSummary("method=mouse");
        return new ClickElementResponse(Clicked: true, MethodUsed: "mouse", Effects: effects.ToContract());
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

    private async Task<ClickElementResponse> ClickWpfHandleWithMouseAsync(
        Window window,
        ElementHandle handle,
        ClickElementRequest request,
        EffectiveInteractionPolicy policy,
        InteractionEffectTracker effects,
        CancellationToken cancellationToken)
    {
        var bounds = await ResolveWpfBoundsForHandleAsync(
            window,
            handle,
            autoScroll: request.AutoWait,
            cancellationToken,
            throwIfScrollFailed: request.AutoWait).ConfigureAwait(false);

        await PrepareWindowForPhysicalInputAsync(
            window,
            operation: "click_element",
            policy,
            effects,
            semanticAlternative: "The WPF target could not be mapped to a semantic UI Automation action.",
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
        effects.MarkMouseInput();

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }

        return new ClickElementResponse(Clicked: true, MethodUsed: "mouse", Effects: effects.ToContract());
    }

    private static bool ShouldUseDirectWpfMouseClick(ClickElementRequest request) =>
        request.ClickType != ClickType.Single || request.ClickMode == ClickMode.MouseAlways;

    private static void EnsurePhysicalInputAllowed(
        string operation,
        EffectiveInteractionPolicy policy,
        string semanticAlternative)
    {
        if (!policy.AllowPhysicalInput)
        {
            throw InteractionPolicyResolver.Blocked(
                operation,
                requiredEffect: "physical mouse or keyboard input",
                policySetting: "allowPhysicalInput",
                alternative: $"{semanticAlternative} Retry with interactionPolicy.allowPhysicalInput=true to permit fallback.");
        }
    }

    private static InteractionEffects MarkSemanticAndGetEffects(InteractionEffectTracker effects)
    {
        effects.MarkSemantic();
        return effects.ToContract();
    }

    private static async Task PrepareWindowForPhysicalInputAsync(
        Window window,
        string operation,
        EffectiveInteractionPolicy policy,
        InteractionEffectTracker effects,
        string semanticAlternative,
        CancellationToken cancellationToken,
        bool focusWindow = true)
    {
        EnsurePhysicalInputAllowed(operation, policy, semanticAlternative);
        await EnsureWindowForegroundAsync(
            window,
            operation,
            policy,
            effects,
            settleDelayMs: UiDelayWindowSettleMs,
            cancellationToken,
            focusWindow).ConfigureAwait(false);
    }

    private static async Task EnsureWindowForegroundAsync(
        Window window,
        string operation,
        EffectiveInteractionPolicy policy,
        InteractionEffectTracker effects,
        int settleDelayMs,
        CancellationToken cancellationToken,
        bool focusWindow = true)
    {
        var windowHandle = window.Properties.NativeWindowHandle.Value;
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Window handle is not available.");
        }

        var foregroundBefore = GetForegroundWindow();
        var requiresForegroundTransition = foregroundBefore != windowHandle;
        var windowPattern = window.Patterns.Window.PatternOrDefault;
        var isMinimized = windowPattern is not null &&
                          windowPattern.WindowVisualState == WindowVisualState.Minimized;

        if ((requiresForegroundTransition || isMinimized) && !policy.AllowForegroundActivation)
        {
            throw InteractionPolicyResolver.Blocked(
                operation,
                requiredEffect: isMinimized ? "window restore and foreground activation" : "foreground activation",
                policySetting: "allowForegroundActivation",
                alternative: "Use a semantic action that can run in the background, or retry with interactionPolicy.allowForegroundActivation=true.");
        }

        if (isMinimized && windowPattern is not null)
        {
            try
            {
                windowPattern.SetWindowVisualState(WindowVisualState.Normal);
                effects.MarkWindowRestored();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"foreground_activation_failed: operation={operation} could not restore the minimized window.",
                    ex);
            }

            await Task.Delay(Math.Max(UiDelayWindowSettleMs, 100), cancellationToken);
        }

        if (requiresForegroundTransition)
        {
            try
            {
                window.SetForeground();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"foreground_activation_failed: operation={operation} could not foreground the target window.",
                    ex);
            }
        }

        if (focusWindow)
        {
            try
            {
                window.Focus();
            }
            catch
            {
            }
        }

        if (requiresForegroundTransition && GetForegroundWindow() != windowHandle)
        {
            for (var attempt = 0; attempt < 3 && GetForegroundWindow() != windowHandle; attempt++)
            {
                _ = TrySetForegroundWindowWithAttachedInput(windowHandle, focusWindow);
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        await Task.Delay(settleDelayMs, cancellationToken);

        var foregroundAfter = GetForegroundWindow();
        if (foregroundAfter != windowHandle)
        {
            throw new InvalidOperationException(
                $"foreground_activation_failed: operation={operation} targetHandle={windowHandle.ToInt64()} " +
                $"foregroundHandle={foregroundAfter.ToInt64()}.");
        }

        if (requiresForegroundTransition)
        {
            effects.MarkForegroundActivated();
        }
    }

    private static bool TrySetForegroundWindowWithAttachedInput(IntPtr windowHandle, bool focusWindow)
    {
        var foregroundHandle = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);
        var foregroundThreadId = foregroundHandle == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundHandle, out _);

        var attachedToForeground = AttachInputQueue(currentThreadId, foregroundThreadId);
        var attachedToTarget = AttachInputQueue(currentThreadId, targetThreadId);
        try
        {
            _ = BringWindowToTop(windowHandle);
            var foregroundSet = SetForegroundWindow(windowHandle);
            if (focusWindow)
            {
                _ = SetFocus(windowHandle);
            }

            return foregroundSet || GetForegroundWindow() == windowHandle;
        }
        finally
        {
            DetachInputQueue(currentThreadId, targetThreadId, attachedToTarget);
            DetachInputQueue(currentThreadId, foregroundThreadId, attachedToForeground);
        }
    }

    private static bool AttachInputQueue(uint currentThreadId, uint otherThreadId) =>
        otherThreadId != 0 &&
        otherThreadId != currentThreadId &&
        AttachThreadInput(currentThreadId, otherThreadId, attach: true);

    private static void DetachInputQueue(uint currentThreadId, uint otherThreadId, bool attached)
    {
        if (attached)
        {
            _ = AttachThreadInput(currentThreadId, otherThreadId, attach: false);
        }
    }

    public async Task<InvokeResponse> InvokeAsync(
        InvokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("invoke");
        try
        {
            var effects = new InteractionEffectTracker();
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

                if (handle.Backend == InspectionBackend.Wpf)
                {
                    wpfSourceHandle = handle;
                    var wpfInvoke = await TryInvokeWpfAsync(elementId, handle, cancellationToken).ConfigureAwait(false);
                    if (wpfInvoke is not null)
                    {
                        effects.MarkSemantic();
                        trace?.SetSummary($"invoked=true method={wpfInvoke.MethodUsed}");
                        return wpfInvoke with { Effects = effects.ToContract() };
                    }

                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, elementId, handle, out _);
                }
                else if (handle.Backend == InspectionBackend.Uia)
                {
                    element = ResolveUiaElementById(
                        window,
                        rawWalker,
                        elementId,
                        out _,
                        UiaHandleResolutionMode.RequireRegisteredIdentity);
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
                    var wpfInvoke = await TryInvokeWpfAsync(
                        wpfTarget.ElementId,
                        wpfTarget.Handle,
                        cancellationToken).ConfigureAwait(false);
                    if (wpfInvoke is not null)
                    {
                        effects.MarkSemantic();
                        trace?.SetSummary($"invoked=true method={wpfInvoke.MethodUsed}");
                        return wpfInvoke with { Effects = effects.ToContract() };
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

            effects.MarkSemantic();
            var response = new InvokeResponse(
                Invoked: true,
                MethodUsed: "invoke",
                Effects: effects.ToContract());
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
            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var effects = new InteractionEffectTracker();
            var hasLocator = request.Locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            if (hasLocator && hasElementId)
            {
                throw new ArgumentException("type_text requires at most one of: locator OR elementId.");
            }

            if (request.Text is null)
            {
                throw new ArgumentException("text cannot be null.");
            }

            var mode = KeyboardInputEngine.ResolveTextEntryMode(
                request.Mode,
                hasTarget: hasLocator || hasElementId);

            var application = EnsureAttached();
            var automation = EnsureAutomation();
            var timeoutMs = Math.Clamp(request.TimeoutMs, 0, 60_000);
            var pollIntervalMs = Math.Clamp(request.PollIntervalMs, 25, 2000);
            var stableMs = Math.Clamp(request.StableMs, 0, 5000);

            Window window;
            AutomationElement element;
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();

            if (!hasLocator && !hasElementId)
            {
                window = request.WindowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                var focusedBeforeOperation = TryGetFocusedElement(automation);
                await PrepareWindowForPhysicalInputAsync(
                    window,
                    operation: "type_text",
                    policy,
                    effects,
                    semanticAlternative: "Specify a locator or elementId so a writable ValuePattern can be used.",
                    cancellationToken,
                    focusWindow: false).ConfigureAwait(false);

                try
                {
                    element = automation.FocusedElement();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("focused_element_unavailable: unable to read the currently focused element.", ex);
                }

                if (AreSameElement(window, element))
                {
                    throw new InvalidOperationException("focused_element_unavailable: the session window is focused, but no child input target is focused.");
                }

                if (!IsElementWithinWindow(window, element, rawWalker))
                {
                    throw new InvalidOperationException("focused_element_outside_session: the currently focused element does not belong to the active session window.");
                }

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

                KeyboardInputEngine.TypeText(request.Text, mode);
                effects.MarkKeyboardInput();
                MarkKeyboardFocusChangeIfDifferent(focusedBeforeOperation, automation, effects);
                if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=keyboard_focused");
                return new TypeTextResponse(
                    Typed: true,
                    MethodUsed: "keyboard_focused",
                    Effects: effects.ToContract(),
                    ModeUsed: mode,
                    ForegroundFocusRequired: true,
                    PhysicalInputRequired: true);
            }

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

                if (handle.Backend == InspectionBackend.Wpf)
                {
                    await EnsureWpfHandleEnabledOrThrowAsync(elementId, "type_text", cancellationToken).ConfigureAwait(false);
                    var wpfSet = await TrySetWpfValueAsync(
                        elementId,
                        handle,
                        new SetValueRequest(ElementId: elementId, Text: request.Text),
                        cancellationToken,
                        mode).ConfigureAwait(false);
                    if (wpfSet is not null)
                    {
                        effects.MarkSemantic();
                        trace?.SetSummary($"method={wpfSet.MethodUsed}");
                        return new TypeTextResponse(
                            Typed: true,
                            MethodUsed: wpfSet.MethodUsed,
                            Effects: effects.ToContract(),
                            ModeUsed: mode);
                    }

                    try
                    {
                        element = ResolveUiaElementByWpfHandle(
                            window,
                            controlWalker,
                            rawWalker,
                            elementId,
                            handle,
                            out _);
                    }
                    catch (InvalidOperationException)
                    {
                        return await TypeTextWithWpfPhysicalFallbackAsync(
                            window,
                            elementId,
                            handle,
                            request,
                            mode,
                            policy,
                            effects,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (handle.Backend == InspectionBackend.Uia)
                {
                    element = ResolveUiaElementById(
                        window,
                        rawWalker,
                        elementId,
                        out _,
                        UiaHandleResolutionMode.RequireRegisteredIdentity);
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
                    var wpfSet = await TrySetWpfValueAsync(
                        wpfTarget.ElementId,
                        wpfTarget.Handle,
                        new SetValueRequest(Locator: request.Locator, Text: request.Text),
                        cancellationToken,
                        mode).ConfigureAwait(false);
                    if (wpfSet is not null)
                    {
                        effects.MarkSemantic();
                        trace?.SetSummary($"method={wpfSet.MethodUsed}");
                        return new TypeTextResponse(
                            Typed: true,
                            MethodUsed: wpfSet.MethodUsed,
                            Effects: effects.ToContract(),
                            ModeUsed: mode);
                    }

                    try
                    {
                        element = ResolveUiaElementByWpfHandle(
                            window,
                            controlWalker,
                            rawWalker,
                            wpfTarget.ElementId,
                            wpfTarget.Handle,
                            out _);
                    }
                    catch (InvalidOperationException)
                    {
                        return await TypeTextWithWpfPhysicalFallbackAsync(
                            window,
                            wpfTarget.ElementId,
                            wpfTarget.Handle,
                            request,
                            mode,
                            policy,
                            effects,
                            cancellationToken).ConfigureAwait(false);
                    }
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
                            ActionKind.TypeText,
                            cancellationToken)
                        : ResolveElement(window, request.Locator!, controlWalker, rawWalker, ActionKind.TypeText);
                }
            }

            EnsureEnabledOrThrow(element, "type_text");
            var valuePattern = element.Patterns.Value.PatternOrDefault;
            var isPassword = element.Properties.IsPassword.ValueOrDefault;
            if (KeyboardInputEngine.CanUseSemanticValuePattern(
                    mode,
                    isPassword) &&
                valuePattern is not null &&
                valuePattern.IsReadOnly == false)
            {
                string semanticText;
                try
                {
                    semanticText = mode == TextEntryMode.Append
                        ? (valuePattern.Value ?? string.Empty) + request.Text
                        : request.Text;
                    valuePattern.SetValue(semanticText);
                }
                catch (COMException ex)
                {
                    throw (InvalidOperationException)WrapUiaActionException(ex, "type_text", element);
                }

                if (request.AutoWait && KeyboardInputEngine.CanReadValuePatternText(isPassword))
                {
                    await WaitForValuePatternTextAsync(
                        valuePattern,
                        expected: semanticText,
                        timeoutMs,
                        pollIntervalMs,
                        cancellationToken);
                }
                else if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                effects.MarkSemantic();
                var methodUsed = mode == TextEntryMode.Append
                    ? "valuePattern_append"
                    : "valuePattern";
                trace?.SetSummary($"method={methodUsed}");
                return new TypeTextResponse(
                    Typed: true,
                    MethodUsed: methodUsed,
                    Effects: effects.ToContract(),
                    ModeUsed: mode);
            }

            var physicalAlternative = mode switch
            {
                TextEntryMode.AtSelection =>
                    "AtSelection requires keyboard input for this UI Automation target.",
                TextEntryMode.Append when isPassword =>
                    "Append requires keyboard input because UI Automation does not expose the current password value.",
                _ => "The target has no writable ValuePattern."
            };
            EnsurePhysicalInputAllowed("type_text", policy, physicalAlternative);
            var focusedBeforePhysicalInput = TryGetFocusedElement(automation);
            await PrepareWindowForPhysicalInputAsync(
                window,
                operation: "type_text",
                policy,
                effects,
                semanticAlternative: physicalAlternative,
                cancellationToken,
                focusWindow: false).ConfigureAwait(false);
            TryScrollIntoView(element);
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

            FocusUiaElementForKeyboardInput(element, automation, rawWalker, effects);
            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }

            KeyboardInputEngine.TypeText(request.Text, mode);
            effects.MarkKeyboardInput();
            MarkKeyboardFocusChangeIfDifferent(focusedBeforePhysicalInput, automation, effects);

            if (request.AutoWait)
            {
                var afterValuePattern = element.Patterns.Value.PatternOrDefault;
                if (mode == TextEntryMode.Replace &&
                    KeyboardInputEngine.CanReadValuePatternText(isPassword) &&
                    afterValuePattern is not null &&
                    afterValuePattern.IsReadOnly == false)
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

            var physicalMethodUsed = request.Mode is null
                ? "keyboard"
                : "keyboard_uia_focus";
            trace?.SetSummary($"method={physicalMethodUsed}");
            return new TypeTextResponse(
                Typed: true,
                MethodUsed: physicalMethodUsed,
                Effects: effects.ToContract(),
                ModeUsed: mode,
                ForegroundFocusRequired: true,
                PhysicalInputRequired: true);
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

    private async Task<TypeTextResponse> TypeTextWithWpfPhysicalFallbackAsync(
        Window window,
        string wpfElementId,
        ElementHandle handle,
        TypeTextRequest request,
        TextEntryMode mode,
        EffectiveInteractionPolicy policy,
        InteractionEffectTracker effects,
        CancellationToken cancellationToken)
    {
        EnsurePhysicalInputAllowed(
            operation: "type_text",
            policy,
            semanticAlternative: "The WPF target does not support direct value assignment.");
        var automation = EnsureAutomation();
        var focusedBeforeInput = TryGetFocusedElement(automation);

        await PrepareWindowForPhysicalInputAsync(
            window,
            operation: "type_text",
            policy,
            effects,
            semanticAlternative: "The WPF target does not support direct value assignment.",
            cancellationToken,
            focusWindow: false).ConfigureAwait(false);

        var focused = await FocusWpfHandleForKeyboardInputAsync(
            wpfElementId,
            handle,
            cancellationToken).ConfigureAwait(false);
        if (focused.KeyboardFocusChanged)
        {
            effects.MarkKeyboardFocusChanged();
        }

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }

        KeyboardInputEngine.TypeText(request.Text, mode);
        effects.MarkKeyboardInput();
        MarkKeyboardFocusChangeIfDifferent(focusedBeforeInput, automation, effects);
        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }

        var physicalMethodUsed = request.Mode is null
            ? "keyboard"
            : "keyboard_wpf_focus";
        return new TypeTextResponse(
            Typed: true,
            MethodUsed: physicalMethodUsed,
            Effects: effects.ToContract(),
            ModeUsed: mode,
            ForegroundFocusRequired: true,
            PhysicalInputRequired: true);
    }

    public async Task<SetValueResponse> SetValueAsync(
        SetValueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("set_value");
        try
        {
            var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
            var effects = new InteractionEffectTracker();
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

                if (handle.Backend == InspectionBackend.Wpf)
                {
                    var wpfSet = await TrySetWpfValueAsync(elementId, handle, request, cancellationToken).ConfigureAwait(false);
                    if (wpfSet is not null)
                    {
                        effects.MarkSemantic();
                        trace?.SetSummary($"method={wpfSet.MethodUsed}");
                        return wpfSet with { Effects = effects.ToContract() };
                    }

                    element = ResolveUiaElementByWpfHandle(window, controlWalker, rawWalker, elementId, handle, out _);
                }
                else if (handle.Backend == InspectionBackend.Uia)
                {
                    element = ResolveUiaElementById(
                        window,
                        rawWalker,
                        elementId,
                        out _,
                        UiaHandleResolutionMode.RequireRegisteredIdentity);
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
                        effects.MarkSemantic();
                        trace?.SetSummary($"method={wpfSet.MethodUsed}");
                        return wpfSet with { Effects = effects.ToContract() };
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

            var rangeValue = element.Patterns.RangeValue.PatternOrDefault;
            if (hasNumericValue && rangeValue is not null && rangeValue.IsReadOnly == false)
            {
                try
                {
                    rangeValue.SetValue(numericValue);
                    effects.MarkSemantic();
                }
                catch (COMException ex)
                {
                    if (!triedDrag &&
                        await TrySetValueByDraggingWithPolicyAsync(
                            window,
                            element,
                            rawWalker,
                            numericValue,
                            request.AutoWait,
                            timeoutMs,
                            pollIntervalMs,
                            steps: 16,
                            policy,
                            effects,
                            cancellationToken).ConfigureAwait(false))
                    {
                        triedDrag = true;
                        trace?.SetSummary("method=drag");
                        return new SetValueResponse(true, "drag", effects.ToContract());
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
                            await TrySetValueByDraggingWithPolicyAsync(
                                window,
                                element,
                                rawWalker,
                                numericValue,
                                request.AutoWait,
                                timeoutMs,
                                pollIntervalMs,
                                steps: 16,
                                policy,
                                effects,
                                cancellationToken).ConfigureAwait(false))
                        {
                            triedDrag = true;
                            trace?.SetSummary("method=drag");
                            return new SetValueResponse(true, "drag", effects.ToContract());
                        }

                        throw;
                    }
                }
                else if (UiDelayMs > 0)
                {
                    await Task.Delay(UiDelayMs, cancellationToken);
                }

                trace?.SetSummary("method=rangeValue");
                return new SetValueResponse(true, "rangeValue", effects.ToContract());
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

                effects.MarkSemantic();
                trace?.SetSummary("method=valuePattern");
                return new SetValueResponse(true, "valuePattern", effects.ToContract());
            }

            if (hasNumericValue && preferDrag)
            {
                triedDrag = true;
                if (await TrySetValueByDraggingWithPolicyAsync(
                        window,
                        element,
                        rawWalker,
                        numericValue,
                        request.AutoWait,
                        timeoutMs,
                        pollIntervalMs,
                        steps: 16,
                        policy,
                        effects,
                        cancellationToken).ConfigureAwait(false))
                {
                    trace?.SetSummary("method=drag");
                    return new SetValueResponse(true, "drag", effects.ToContract());
                }
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

    private async Task<bool> TrySetValueByDraggingWithPolicyAsync(
        Window window,
        AutomationElement element,
        ITreeWalker rawWalker,
        double value,
        bool autoWait,
        int timeoutMs,
        int pollIntervalMs,
        int steps,
        EffectiveInteractionPolicy policy,
        InteractionEffectTracker effects,
        CancellationToken cancellationToken)
    {
        await PrepareWindowForPhysicalInputAsync(
            window,
            operation: "set_value",
            policy,
            effects,
            semanticAlternative: "The target rejected semantic RangeValue or ValuePattern assignment.",
            cancellationToken).ConfigureAwait(false);

        var set = await TrySetValueByDraggingAsync(
            element,
            rawWalker,
            value,
            autoWait,
            timeoutMs,
            pollIntervalMs,
            steps,
            cancellationToken).ConfigureAwait(false);
        if (set)
        {
            effects.MarkMouseInput();
        }

        return set;
    }

    public async Task<SelectItemResponse> SelectItemAsync(
        SelectItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("select_item");
        try
        {
        var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
        var effects = new InteractionEffectTracker();
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
        ElementHandle? itemHandleFromId = null;
        AutomationElement? itemFromElementId = null;

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

            if (handle.Backend == InspectionBackend.Wpf)
            {
                container = ResolveUiaElementByWpfHandle(
                    window,
                    controlWalker,
                    rawWalker,
                    elementId,
                    handle,
                    out _);
            }
            else if (handle.Backend == InspectionBackend.Uia)
            {
                container = ResolveUiaElementById(
                    window,
                    rawWalker,
                    elementId,
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
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

        if (hasItemElementId)
        {
            var itemElementId = request.ItemElementId!.Trim();
            itemHandleFromId = RequireHandle(itemElementId);

            if (itemHandleFromId.WindowHandle != window.Properties.NativeWindowHandle.Value.ToInt64())
            {
                throw new ArgumentException("itemElementId window does not match container window.");
            }

            if (itemHandleFromId.Backend == InspectionBackend.Uia)
            {
                itemFromElementId = ResolveUiaElementById(
                    window,
                    rawWalker,
                    itemElementId,
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else if (itemHandleFromId.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"itemElementId '{itemElementId}' has unsupported backend '{itemHandleFromId.Backend}'.");
            }
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
            var item = itemHandleFromId!.Backend == InspectionBackend.Wpf
                ? ResolveUiaElementByWpfHandle(
                    window,
                    controlWalker,
                    rawWalker,
                    itemElementId,
                    itemHandleFromId,
                    out _)
                : itemFromElementId!;
            TryScrollIntoView(item);
            var methodUsed = await SelectItemElementAsync(
                window,
                item,
                policy,
                effects,
                cancellationToken).ConfigureAwait(false);

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }
            trace?.SetSummary($"selected=true method={methodUsed}");
            return new SelectItemResponse(true, methodUsed, effects.ToContract());
        }

        if (hasItemLocator)
        {
            var itemLocator = request.ItemLocator!;
            var item = !string.IsNullOrWhiteSpace(itemLocator.XPath)
                ? ResolveElement(window, itemLocator, controlWalker, rawWalker)
                : await ResolveElementWithinRootOrScrollAsync(container, itemLocator, controlWalker, cancellationToken);

            TryScrollIntoView(item);
            var methodUsed = await SelectItemElementAsync(
                window,
                item,
                policy,
                effects,
                cancellationToken).ConfigureAwait(false);

            if (UiDelayMs > 0)
            {
                await Task.Delay(UiDelayMs, cancellationToken);
            }
            trace?.SetSummary($"selected=true method={methodUsed}");
            return new SelectItemResponse(true, methodUsed, effects.ToContract());
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
            effects.MarkSemantic();
            trace?.SetSummary("selected=true method=comboBoxSelect");
            return new SelectItemResponse(true, "comboBoxSelect", effects.ToContract());
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
        var selectedMethod = await SelectItemElementAsync(
            window,
            selectedItem,
            policy,
            effects,
            cancellationToken).ConfigureAwait(false);

        if (UiDelayMs > 0)
        {
            await Task.Delay(UiDelayMs, cancellationToken);
        }
        trace?.SetSummary($"selected=true method={selectedMethod}");
        return new SelectItemResponse(true, selectedMethod, effects.ToContract());
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
        CancellationToken cancellationToken = default) =>
        await ScrollToElementCoreAsync(
            request,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task<ScrollToElementResponse> ScrollToElementCoreAsync(
        ScrollToElementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("scroll_to_element");
        try
        {
        var effects = new InteractionEffectTracker();
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
        ElementHandle? containerHandleFromId = null;
        if (hasElementId)
        {
            idForWindow = request.ElementId!.Trim();
            var handle = RequireHandle(idForWindow);
            windowHandleFromId = handle.WindowHandle;
            elementHandleFromId = handle;
        }

        if (!string.IsNullOrWhiteSpace(request.ContainerElementId))
        {
            var containerElementId = request.ContainerElementId!.Trim();
            var handle = RequireHandle(containerElementId);
            idForWindow ??= containerElementId;
            windowHandleFromId ??= handle.WindowHandle;
            containerHandleFromId = handle;

            if (handle.WindowHandle != windowHandleFromId)
            {
                throw new ArgumentException("elementId and containerElementId must refer to the same window.");
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

        AutomationElement? elementFromId = null;
        string? targetXPathFromId = null;
        if (elementHandleFromId is not null)
        {
            if (elementHandleFromId.Backend == InspectionBackend.Uia)
            {
                elementFromId = ResolveUiaElementById(
                    window,
                    rawWalker,
                    request.ElementId!.Trim(),
                    out targetXPathFromId,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else if (elementHandleFromId.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException(
                    $"elementId '{request.ElementId!.Trim()}' has unsupported backend '{elementHandleFromId.Backend}'.");
            }
        }

        AutomationElement? uiaContainerFromElementId = null;
        if (containerHandleFromId is not null)
        {
            if (containerHandleFromId.Backend == InspectionBackend.Uia)
            {
                uiaContainerFromElementId = ResolveUiaElementById(
                    window,
                    rawWalker,
                    request.ContainerElementId!.Trim(),
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else if (containerHandleFromId.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException(
                    $"containerElementId '{request.ContainerElementId!.Trim()}' has unsupported backend '{containerHandleFromId.Backend}'.");
            }
        }

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
                    var alreadyVisible = new ScrollToElementResponse(
                        Scrolled: false,
                        MethodUsed: "alreadyVisible",
                        Effects: effects.ToContract());
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
                    MethodUsed: bring.BroughtIntoView ? "wpf_bringIntoView" : "wpf_bringIntoView_failed",
                    Effects: MarkSemanticAndGetEffects(effects));

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
                var alreadyVisible = new ScrollToElementResponse(
                    Scrolled: false,
                    MethodUsed: "alreadyVisible",
                    Effects: effects.ToContract());
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
                MethodUsed: bring.BroughtIntoView ? "wpf_bringIntoView" : "wpf_bringIntoView_failed",
                Effects: MarkSemanticAndGetEffects(effects));

            trace?.SetSummary($"scrolled={bringResponse.Scrolled} method={bringResponse.MethodUsed}");
            return bringResponse;
        }

        AutomationElement? container = null;
        if (!string.IsNullOrWhiteSpace(request.ContainerElementId))
        {
            var containerElementId = request.ContainerElementId!.Trim();
            var containerHandle = containerHandleFromId!;

            if (containerHandle.Backend == InspectionBackend.Wpf)
            {
                var bounds = await ResolveWpfBoundsForHandleAsync(
                    window,
                    containerHandle,
                    autoScroll: request.AutoWait,
                    cancellationToken,
                    throwIfScrollFailed: request.AutoWait).ConfigureAwait(false);

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
                container = uiaContainerFromElementId!;
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
            element = elementFromId
                ?? throw new InvalidOperationException(
                    $"elementId '{request.ElementId!.Trim()}' could not be resolved through UI Automation.");
            targetXPath = targetXPathFromId;
        }
        else if (container is not null && string.IsNullOrWhiteSpace(request.Locator!.XPath))
        {
            (element, scrolledDuringSearch) = await ResolveElementWithinContainerOrScrollAsync(
                container,
                request.Locator!,
                controlWalker,
                cancellationToken: cancellationToken);
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
            cancellationToken: cancellationToken);

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

        if (scrolledDuringSearch || !string.Equals(bringIntoViewMethod, "alreadyVisible", StringComparison.Ordinal))
        {
            effects.MarkSemantic();
        }

        var response = new ScrollToElementResponse(
            Scrolled: scrolledDuringSearch || scrolledBringingIntoView,
            MethodUsed: methodUsed,
            Effects: effects.ToContract());

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
        var policy = InteractionPolicyResolver.Resolve(request.InteractionPolicy);
        var effects = new InteractionEffectTracker();
        var hasLocator = request.Locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("drag requires exactly one of: locator OR elementId.");
        }

        var hasTargetLocator = request.TargetLocator is not null;
        var hasTargetElementId = !string.IsNullOrWhiteSpace(request.TargetElementId);
        var hasAnyCoordinate = request.ToX is not null || request.ToY is not null;
        if (!hasElementId && !hasTargetElementId)
        {
            EnsurePhysicalInputAllowed(
                operation: "drag",
                policy,
                semanticAlternative: "Drag has no semantic automation equivalent.");
        }

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
            if (sourceHandleFromId!.WindowHandle != targetHandleFromId!.WindowHandle)
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

        AutomationElement? sourceElementFromId = null;
        if (sourceHandleFromId is not null)
        {
            if (sourceHandleFromId.Backend == InspectionBackend.Uia)
            {
                sourceElementFromId = ResolveUiaElementById(
                    window,
                    rawWalker,
                    request.ElementId!.Trim(),
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else if (sourceHandleFromId.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException(
                    $"elementId '{request.ElementId!.Trim()}' has unsupported backend '{sourceHandleFromId.Backend}'.");
            }
        }

        AutomationElement? targetElementFromId = null;
        if (targetHandleFromId is not null)
        {
            if (targetHandleFromId.Backend == InspectionBackend.Uia)
            {
                targetElementFromId = ResolveUiaElementById(
                    window,
                    rawWalker,
                    request.TargetElementId!.Trim(),
                    out _,
                    UiaHandleResolutionMode.RequireRegisteredIdentity);
            }
            else if (targetHandleFromId.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException(
                    $"targetElementId '{request.TargetElementId!.Trim()}' has unsupported backend '{targetHandleFromId.Backend}'.");
            }
        }

        if (hasElementId || hasTargetElementId)
        {
            EnsurePhysicalInputAllowed(
                operation: "drag",
                policy,
                semanticAlternative: "Drag has no semantic automation equivalent.");
        }

        await PrepareWindowForPhysicalInputAsync(
            window,
            operation: "drag",
            policy,
            effects,
            semanticAlternative: "Drag has no semantic automation equivalent.",
            cancellationToken).ConfigureAwait(false);

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
                    cancellationToken,
                    throwIfScrollFailed: request.AutoWait).ConfigureAwait(false);
                start = GetRectCenterPoint(bounds);
            }
            else if (handle.Backend == InspectionBackend.Uia)
            {
                var source = sourceElementFromId!;
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
                    cancellationToken,
                    throwIfScrollFailed: request.AutoWait).ConfigureAwait(false);
                end = GetRectCenterPoint(bounds);
            }
            else if (handle.Backend == InspectionBackend.Uia)
            {
                var target = targetElementFromId!;
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
        effects.MarkMouseInput();
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
        var response = new DragResponse(
            Dragged: true,
            MethodUsed: "mouse",
            Effects: effects.ToContract());
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

    public Task<WaitForResponse> WaitForAsync(
        WaitForRequest request,
        CancellationToken cancellationToken = default) =>
        WaitForAsync(request, cancellationToken, structuredElementDeadline: null);

    private async Task<WaitForResponse> WaitForAsync(
        WaitForRequest request,
        CancellationToken cancellationToken,
        StructuredElementWaitDeadline? structuredElementDeadline)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("wait_for");
        try
        {
            if (request.Condition is not null)
            {
                var structuredResponse = await WaitForConditionAsync(request, cancellationToken).ConfigureAwait(false);
                trace?.SetSummary(
                    $"{structuredResponse.State} succeeded={structuredResponse.Succeeded} " +
                    $"attempts={structuredResponse.Attempts} backend={structuredResponse.BackendUsed}");
                return structuredResponse;
            }

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

        Window? locatorWindow = null;
        if (hasLocator)
        {
            locatorWindow = request.WindowHandle is long requestedHandle
                ? FindWindowByHandle(application, automation, requestedHandle)
                : FindMainWindow(application, automation);
        }

        var backendForLocator = request.Backend;
        if (hasLocator && backendForLocator == InspectionBackend.Auto)
        {
            if (GetAutoBackendRoute(locatorWindow!) == AutoBackendRoute.Uia)
            {
                backendForLocator = InspectionBackend.Uia;
            }
            else
            {
                var autoClient = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
                backendForLocator = autoClient is not null ? InspectionBackend.Wpf : InspectionBackend.Uia;
            }
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
                    structuredElementDeadline,
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
            var hwnd = locatorWindow!.Properties.NativeWindowHandle.Value.ToInt64();
            try
            {
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
                    structuredElementDeadline,
                    cancellationToken).ConfigureAwait(false);

                trace?.SetSummary($"{request.State} succeeded={response.Succeeded} attempts={response.Attempts}");
                return response;
            }
            catch (Exception ex) when (
                request.Backend == InspectionBackend.Auto &&
                IsPerWindowAutoWpfMiss(ex))
            {
                // Unknown framework windows may reject WPF routing per HWND; continue with UIA.
            }
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
            window = locatorWindow!;

            xpathHint = request.Locator?.XPath;
        }

        var start = structuredElementDeadline?.StartTimestamp ?? Stopwatch.GetTimestamp();
        var attempts = 0;
        WaitForObservation? lastObservation = null;
        WaitObservedValue? lastObservedValue = CreateUnavailableObservedValue("condition_not_observed");
        var lastFailureReason = "not_attached";

        Rectangle? lastBounds = null;
        long? stableStartTimestamp = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

            if ((timeoutMs > 0 || attempts > 0) &&
                Stopwatch.GetElapsedTime(start).TotalMilliseconds >= timeoutMs)
            {
                var timeoutResponse = CreateLegacyWaitTimeoutResponse(
                    request.State,
                    WaitBackend.Uia,
                    timeoutMs,
                    start,
                    attempts,
                    lastObservation,
                    lastObservedValue,
                    lastFailureReason,
                    request.ThrowOnTimeout);
                trace?.SetSummary(
                    $"{request.State} succeeded=false attempts={attempts} reason={lastFailureReason}");
                return timeoutResponse;
            }

            attempts++;

            AutomationElement? element;
            try
            {
                if (hasElementId)
                {
                    element = ResolveUiaElementById(
                        window,
                        rawWalker,
                        request.ElementId!.Trim(),
                        out _,
                        UiaHandleResolutionMode.ObserveCurrentXPathOccupant);
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
                lastObservedValue = CreateUnavailableObservedValue("not_attached");
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
                lastObservedValue = ObserveLegacyUiaWaitValue(element, state);
            }

            lastFailureReason = failureReason ?? lastFailureReason;
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (satisfied && (timeoutMs == 0 || elapsed.TotalMilliseconds < timeoutMs))
            {
                var elapsedMs = (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
                trace?.SetSummary($"{request.State} succeeded=true attempts={attempts}");
                return new WaitForResponse(
                    Succeeded: true,
                    State: request.State,
                    ElapsedMs: elapsedMs,
                    Attempts: attempts,
                    LastObservation: lastObservation)
                {
                    BackendUsed = WaitBackend.Uia,
                    LastObservedValue = lastObservedValue
                };
            }

            if (satisfied)
            {
                lastFailureReason = "condition_met_after_timeout";
            }

            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                var timeoutResponse = CreateLegacyWaitTimeoutResponse(
                    request.State,
                    WaitBackend.Uia,
                    timeoutMs,
                    start,
                    attempts,
                    lastObservation,
                    lastObservedValue,
                    lastFailureReason,
                    request.ThrowOnTimeout);
                trace?.SetSummary(
                    $"{request.State} succeeded=false attempts={attempts} reason={lastFailureReason}");
                return timeoutResponse;
            }

            var remainingMs = Math.Max(1, timeoutMs - (int)elapsed.TotalMilliseconds);
            await Task.Delay(Math.Min(pollIntervalMs, remainingMs), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            IsStructuredElementDeadlineCancellation(structuredElementDeadline))
        {
            var timeoutResponse = CreateLegacyWaitTimeoutResponse(
                request.State,
                WaitBackend.Uia,
                timeoutMs,
                start,
                attempts,
                lastObservation,
                lastObservedValue,
                lastFailureReason,
                request.ThrowOnTimeout);
            trace?.SetSummary(
                $"{request.State} succeeded=false attempts={attempts} reason={lastFailureReason}");
            return timeoutResponse;
        }
        catch (Exception ex) when (
            structuredElementDeadline is not null &&
            ex is not OperationCanceledException &&
            !IsApplicationRunning(_application))
        {
            var targetExitedResponse = CreateTargetProcessExitedResponse(
                request.State,
                WaitBackend.Uia,
                GetElapsedMilliseconds(start),
                attempts,
                lastObservation,
                lastObservedValue);
            trace?.SetSummary(
                $"{request.State} succeeded=false attempts={attempts} reason=target_process_exited");
            return targetExitedResponse;
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
        StructuredElementWaitDeadline? structuredElementDeadline,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(xpath) && locator is null)
        {
            throw new ArgumentException("wait_for requires either an elementId (WPF handle) or a locator for backend=wpf.");
        }

        var start = structuredElementDeadline?.StartTimestamp ?? Stopwatch.GetTimestamp();
        var attempts = 0;
        WaitForObservation? lastObservation = null;
        WaitObservedValue? lastObservedValue = CreateUnavailableObservedValue("condition_not_observed");
        var lastFailureReason = "not_attached";

        Rect? lastBounds = null;
        long? stableStartTimestamp = null;

        AgentClient? client = null;
        try
        {
            client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
            string? currentXPath = string.IsNullOrWhiteSpace(xpath) ? null : NormalizeWpfXPath(xpath);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if ((timeoutMs > 0 || attempts > 0) &&
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds >= timeoutMs)
                {
                    return CreateLegacyWaitTimeoutResponse(
                        stateText,
                        WaitBackend.Wpf,
                        timeoutMs,
                        start,
                        attempts,
                        lastObservation,
                        lastObservedValue,
                        lastFailureReason,
                        throwOnTimeout);
                }

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
                        lastObservedValue = ObserveWpfComputedValue(computed.Properties);
                    }
                    catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
                    {
                        currentXPath = null;
                        lastObservation = null;
                        lastObservedValue = CreateUnavailableObservedValue("not_attached");
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
                            lastObservedValue = BooleanObservedValue(true);

                            satisfied = true;
                        }
                    }
                    catch (InvalidOperationException ex) when (IsWaitableWpfNotFound(ex))
                    {
                        lastObservedValue = CreateUnavailableObservedValue("not_attached");
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
                        lastObservedValue = CreateUnavailableObservedValue("not_attached");
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
                            lastObservedValue = ObserveWpfNameValue(node, computed.Properties, expectedText);
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
                            lastObservedValue = ObserveLegacyWpfWaitValue(node, state);
                        }
                    }
                }
            }

            lastFailureReason = failureReason ?? lastFailureReason;
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (satisfied && (timeoutMs == 0 || elapsed.TotalMilliseconds < timeoutMs))
            {
                var elapsedMs = (int)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero);
                return new WaitForResponse(
                    Succeeded: true,
                    State: stateText,
                    ElapsedMs: elapsedMs,
                    Attempts: attempts,
                    LastObservation: lastObservation)
                {
                    BackendUsed = WaitBackend.Wpf,
                    LastObservedValue = lastObservedValue
                };
            }

            if (satisfied)
            {
                lastFailureReason = "condition_met_after_timeout";
            }

            if (elapsed.TotalMilliseconds >= timeoutMs)
            {
                return CreateLegacyWaitTimeoutResponse(
                    stateText,
                    WaitBackend.Wpf,
                    timeoutMs,
                    start,
                    attempts,
                    lastObservation,
                    lastObservedValue,
                    lastFailureReason,
                    throwOnTimeout);
            }

            var remainingMs = Math.Max(1, timeoutMs - (int)elapsed.TotalMilliseconds);
            await Task.Delay(Math.Min(pollIntervalMs, remainingMs), cancellationToken);
        }
        }
        catch (OperationCanceledException) when (
            IsStructuredElementDeadlineCancellation(structuredElementDeadline))
        {
            return CreateLegacyWaitTimeoutResponse(
                stateText,
                WaitBackend.Wpf,
                timeoutMs,
                start,
                attempts,
                lastObservation,
                lastObservedValue,
                lastFailureReason,
                throwOnTimeout);
        }
        catch (Exception ex) when (
            structuredElementDeadline is not null &&
            ex is not OperationCanceledException &&
            !IsApplicationRunning(_application))
        {
            return CreateTargetProcessExitedResponse(
                stateText,
                WaitBackend.Wpf,
                GetElapsedMilliseconds(start),
                attempts,
                lastObservation,
                lastObservedValue);
        }
        catch (Exception ex) when (
            structuredElementDeadline is not null &&
            ex is not OperationCanceledException &&
            (ex is TimeoutException || client?.IsConnected == false))
        {
            var reasonCode = IsApplicationRunning(_application)
                ? "agent_connection_lost"
                : "target_process_exited";
            return CreateStructuredFailureResponse(
                stateText,
                WaitBackend.Wpf,
                start,
                attempts,
                lastObservation,
                lastObservedValue,
                reasonCode,
                reasonCode);
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

    internal static bool IsWaitableWpfXPathNotFound(InvalidOperationException ex)
    {
        var message = GetInternalFailureMessage(ex);
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
        await ScrollPatternBringIntoViewAsync(
            scrollElement,
            scrollPattern,
            scrollTarget,
            cancellationToken);
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

    private static async Task<string> SelectItemElementAsync(
        Window window,
        AutomationElement item,
        EffectiveInteractionPolicy policy,
        InteractionEffectTracker effects,
        CancellationToken cancellationToken)
    {
        try
        {
            var selectionItem = item.Patterns.SelectionItem.PatternOrDefault;
            if (selectionItem is not null)
            {
                selectionItem.Select();
                effects.MarkSemantic();
                return "selectionItem";
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
                effects.MarkSemantic();
                return "invoke";
            }
        }
        catch
        {
        }

        await PrepareWindowForPhysicalInputAsync(
            window,
            operation: "select_item",
            policy,
            effects,
            semanticAlternative: "The item exposes neither SelectionItemPattern nor InvokePattern.",
            cancellationToken).ConfigureAwait(false);

        var point = GetClickPoint(item);
        Mouse.LeftClick(point);
        effects.MarkMouseInput();
        return "mouse";
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

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
        bool autoInject = false,
        string? rootElementId = null,
        bool requireStableRootIdentity = false)
    {
        var trace = BeginTraceSpan("get_visual_tree");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();
            var hasRootElementId = !string.IsNullOrWhiteSpace(rootElementId);

            if (root is not null && hasRootElementId)
            {
                throw new ArgumentException("Provide either root or rootElementId, not both.");
            }

            if (hasRootElementId && backend != InspectionBackend.Uia)
            {
                throw new ArgumentException("rootElementId is supported only with the UIA backend.");
            }

            if (depth <= 0)
            {
                depth = 1;
            }

            maxNodes = Math.Clamp(maxNodes, 1, 5000);
            IReadOnlyList<string>? warnings = null;
            BackendFallbackInfo? fallback = null;
            Window? autoWindow = null;

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
                autoWindow = windowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);
                var resolvedWindowHandle = autoWindow.Properties.NativeWindowHandle.Value.ToInt64();
                var wpfRootXPath = root?.XPath;
                var canTryWpf = GetAutoBackendRoute(autoWindow) != AutoBackendRoute.Uia;

                if (!canTryWpf)
                {
                    warnings = [GetNativeAutoRoutingWarning(autoWindow)];
                    fallback = CreateWpfToUiaFallback(attempted: false);
                }

                if (canTryWpf && root is not null && string.IsNullOrWhiteSpace(wpfRootXPath))
                {
                    var attemptSequence = GetAutoAgentAttemptSequence();
                    var client = autoInject
                        ? await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false)
                        : await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);

                    if (client is null)
                    {
                        canTryWpf = false;
                        warnings = [autoInject ? GetAutoAgentFallbackWarning() : "backend=auto: WPF agent not connected; used UIA."];
                        var failure = GetWpfBackendFailure();
                        fallback = CreateWpfToUiaFallback(
                            attempted: autoInject && GetAutoAgentAttemptSequence() != attemptSequence,
                            failure: failure);
                    }
                    else
                    {
                        try
                        {
                            wpfRootXPath = await ResolveWpfRootXPathAsync(root, resolvedWindowHandle, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            canTryWpf = false;
                            var failure = ClassifyAutoWpfFallbackFailure(ex);
                            warnings = IsAutoWpfScopeMiss(ex)
                                ? ["backend=auto: WPF root locator could not be resolved; used UIA."]
                                : [$"backend=auto: WPF backend unavailable ({failure.Code} at {failure.Stage}); used UIA."];
                            fallback = CreateWpfToUiaFallback(
                                attempted: true,
                                failure: failure);
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

                    (GetVisualTreeResponse? Response, bool Attempted, FailureInfo? Failure) wpfAttempt =
                        (null, false, null);
                    try
                    {
                        wpfAttempt = await TryGetVisualTreeWpfAsync(
                            request,
                            cancellationToken,
                            autoInject).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsPerWindowAutoWpfMiss(ex))
                    {
                        warnings = [GetPerWindowAutoRoutingWarning()];
                        fallback = CreateWpfToUiaFallback(
                            attempted: true,
                            failure: ClassifyAutoWpfFallbackFailure(ex));
                    }

                    if (wpfAttempt.Response is { } wpf)
                    {
                        trace?.SetSummary($"{wpf.BackendUsed} returned={wpf.ReturnedNodes} truncated={wpf.Truncated}");
                        return wpf;
                    }

                    warnings ??=
                    [
                        autoInject || wpfAttempt.Failure is not null
                            ? GetAutoAgentFallbackWarning(wpfAttempt.Failure)
                            : "backend=auto: WPF agent not connected; used UIA."
                    ];
                    var failure = wpfAttempt.Failure ?? GetWpfBackendFailure();
                    fallback ??= CreateWpfToUiaFallback(
                        attempted: wpfAttempt.Attempted,
                        failure: failure);
                }
            }

            var window = autoWindow ?? (windowHandle is long uiaRequestedHandle
                ? FindWindowByHandle(application, automation, uiaRequestedHandle)
                : FindMainWindow(application, automation));

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            AutomationElement rootElement;
            string rootXPath;
            if (hasRootElementId)
            {
                var id = rootElementId!.Trim();
                var handle = RequireHandle(id);
                var resolvedWindowHandle = window.Properties.NativeWindowHandle.Value.ToInt64();
                if (handle.Backend != InspectionBackend.Uia)
                {
                    throw new InvalidOperationException($"elementId '{id}' is not a UIA handle.");
                }

                if (handle.WindowHandle != resolvedWindowHandle)
                {
                    throw new ArgumentException("windowHandle does not match the rootElementId window.");
                }

                rootElement = ResolveUiaElementById(
                    window,
                    rawWalker,
                    id,
                    out rootXPath,
                    requireStableRootIdentity
                        ? UiaHandleResolutionMode.RequireRegisteredIdentity
                        : UiaHandleResolutionMode.ObserveCurrentXPathOccupant);
            }
            else
            {
                rootElement = root is null ? window : ResolveElement(window, root, controlWalker, rawWalker);
                rootXPath = ComputeXPath(window, rootElement, rawWalker);
            }

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
                Warnings: warnings)
            {
                Fallback = fallback
            };

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
            BackendFallbackInfo? fallback = null;
            Window? autoWindow = null;

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
                    ReturnFields: returnFields,
                    IncludeElementIds: includeElementIds);

                var wpf = await FindElementsWpfAsync(request, injectIfMissing: true, cancellationToken).ConfigureAwait(false);
                var responseWpf = includeElementIds
                    ? AttachWpfElementIds(wpf, resolvedWindowHandle)
                    : StripElementIds(wpf);

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
                autoWindow = windowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);
                var resolvedWindowHandle = autoWindow.Properties.NativeWindowHandle.Value.ToInt64();
                var wpfRootXPath = root?.XPath;
                var canTryWpf = GetAutoBackendRoute(autoWindow) != AutoBackendRoute.Uia;

                if (!canTryWpf)
                {
                    warnings = [GetNativeAutoRoutingWarning(autoWindow)];
                    fallback = CreateWpfToUiaFallback(attempted: false);
                }

                if (canTryWpf && root is not null && string.IsNullOrWhiteSpace(wpfRootXPath))
                {
                    var attemptSequence = GetAutoAgentAttemptSequence();
                    var client = autoInject
                        ? await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false)
                        : await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);

                    if (client is null)
                    {
                        canTryWpf = false;
                        warnings = [autoInject ? GetAutoAgentFallbackWarning() : "backend=auto: WPF agent not connected; used UIA."];
                        var failure = GetWpfBackendFailure();
                        fallback = CreateWpfToUiaFallback(
                            attempted: autoInject && GetAutoAgentAttemptSequence() != attemptSequence,
                            failure: failure);
                    }
                    else
                    {
                        try
                        {
                            wpfRootXPath = await ResolveWpfRootXPathAsync(root, resolvedWindowHandle, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            canTryWpf = false;
                            var failure = ClassifyAutoWpfFallbackFailure(ex);
                            warnings = IsAutoWpfScopeMiss(ex)
                                ? ["backend=auto: WPF root locator could not be resolved; used UIA."]
                                : [$"backend=auto: WPF backend unavailable ({failure.Code} at {failure.Stage}); used UIA."];
                            fallback = CreateWpfToUiaFallback(
                                attempted: true,
                                failure: failure);
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
                        ReturnFields: returnFields,
                        IncludeElementIds: includeElementIds);

                    (FindElementsResponse? Response, bool Attempted, FailureInfo? Failure) wpfAttempt =
                        (null, false, null);
                    try
                    {
                        wpfAttempt = await TryFindElementsWpfAsync(
                            request,
                            cancellationToken,
                            autoInject).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsPerWindowAutoWpfMiss(ex))
                    {
                        warnings = [GetPerWindowAutoRoutingWarning()];
                        fallback = CreateWpfToUiaFallback(
                            attempted: true,
                            failure: ClassifyAutoWpfFallbackFailure(ex));
                    }

                    if (wpfAttempt.Response is { } wpf)
                    {
                        var responseWpf = includeElementIds
                            ? AttachWpfElementIds(wpf, resolvedWindowHandle)
                            : StripElementIds(wpf);

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

                    warnings ??=
                    [
                        autoInject || wpfAttempt.Failure is not null
                            ? GetAutoAgentFallbackWarning(wpfAttempt.Failure)
                            : "backend=auto: WPF agent not connected; used UIA."
                    ];
                    var failure = wpfAttempt.Failure ?? GetWpfBackendFailure();
                    fallback ??= CreateWpfToUiaFallback(
                        attempted: wpfAttempt.Attempted,
                        failure: failure);
                }
            }

            var window = autoWindow ?? (windowHandle is long uiaRequestedHandle
                ? FindWindowByHandle(application, automation, uiaRequestedHandle)
                : FindMainWindow(application, automation));

            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();
            var rootElement = root is null ? window : ResolveElement(window, root, controlWalker, rawWalker);
            var rootXPath = ComputeXPath(window, rootElement, rawWalker);

            var windowHwnd = window.Properties.NativeWindowHandle.Value.ToInt64();
            var viewportBounds = TryGetClientBoundsScreen(window, out var clientBounds) ? clientBounds : null;
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

            var finalResponse = response with
            {
                Warnings = warnings ?? response.Warnings,
                Fallback = fallback
            };
            if (!includeElementIds)
            {
                finalResponse = StripElementIds(finalResponse);
            }
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
            _ = ResolveUiaElementById(
                resolvedWindow,
                resolvedWalker,
                id,
                out _,
                UiaHandleResolutionMode.ObserveCurrentXPathOccupant);
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
        ElementPropertiesPreset preset = ElementPropertiesPreset.Summary,
        int maxProperties = 25,
        CancellationToken cancellationToken = default,
        bool requireStableElementIdentity = false,
        int maxValueLength = PropertyValueBudget.MaxStringLength,
        int maxCollectionItems = PropertyValueBudget.MaxCollectionItems,
        int maxValueDepth = PropertyValueBudget.MaxValueDepth,
        int maxSerializedValueCharacters = PropertyValueBudget.MaxSerializedValueCharacters)
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

        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported property preset.");
        }

        if (maxProperties is < 1 or > MaximumElementProperties)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxProperties),
                maxProperties,
                $"maxProperties must be between 1 and {MaximumElementProperties}.");
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
                element = ResolveUiaElementById(
                    window,
                    rawWalker,
                    id,
                    out xpath,
                    requireStableElementIdentity
                        ? UiaHandleResolutionMode.RequireRegisteredIdentity
                        : UiaHandleResolutionMode.ObserveCurrentXPathOccupant);
            }
            else
            {
                if (requireStableElementIdentity)
                {
                    _ = await ResolveWpfElementRefAsync(
                        handle,
                        handle.WindowHandle,
                        visibleOnly: false,
                        includeOffViewport: true,
                        interactiveOnly: false,
                        interactiveMode: InteractiveMode.Heuristic,
                        cancellationToken,
                        allowHandleRecovery: false).ConfigureAwait(false);

                    element = ResolveUiaElementByWpfHandle(
                        window,
                        controlWalker,
                        rawWalker,
                        id,
                        handle,
                        out xpath);
                }
                else
                {
                    var permissive = ResolveUiaElementByWpfHandleForProperties(window, controlWalker, rawWalker, id, handle);
                    element = permissive.Element;
                    xpath = permissive.XPath;
                    uiaMapping = permissive.UiaMapping;
                }
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

        var valueBudget = new PropertyValueBudget(
            maxStringLength: maxValueLength,
            maxCollectionItems: maxCollectionItems,
            maxValueDepth: maxValueDepth,
            maxSerializedValueCharacters: maxSerializedValueCharacters,
            maxXPathLength: maxValueLength);
        var boundedXPath = BoundedPropertyValueSerializer.SerializeXPath(
            xpath,
            valueBudget,
            out var xpathOmitted);
        var summary = new ElementSummary(
            ElementType: element.ControlType.ToString(),
            AutomationId: BoundedPropertyValueSerializer.SerializeString(GetAutomationId(element), valueBudget),
            Name: BoundedPropertyValueSerializer.SerializeString(GetName(element), valueBudget),
            ClassName: BoundedPropertyValueSerializer.SerializeString(GetClassName(element), valueBudget),
            Bounds: ToRect(element.BoundingRectangle),
            IsEnabled: element.IsEnabled,
            IsOffscreen: element.IsOffscreen,
            XPath: boundedXPath,
            XPathOmitted: xpathOmitted ? true : null);

        var properties = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var propertyCounts = PopulateProperties(element, properties, preset, maxProperties, valueBudget);

        var patterns = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        PopulatePatterns(element, patterns, valueBudget);

        var boundedUiaMapping = BoundUiaMappingDiagnostics(
            uiaMapping,
            valueBudget,
            out var mappingCandidatesLimitReached);

        var truncatedReasons = BoundedPropertyValueSerializer.GetTruncatedReasons(
            propertyCounts.Truncated,
            valueBudget,
            mappingCandidatesLimitReached);
        var truncatedReason = truncatedReasons.FirstOrDefault();

        var response = new GetElementPropertiesResponse(
            Element: summary,
            Properties: properties,
            Patterns: patterns,
            UiaMapping: boundedUiaMapping,
            Preset: preset,
            ReturnedProperties: propertyCounts.Returned,
            SelectedProperties: propertyCounts.Selected,
            ScannedProperties: propertyCounts.Scanned,
            Truncated: truncatedReason is not null,
            TruncatedReason: truncatedReason,
            TruncatedReasons: truncatedReasons.Count == 0 ? null : truncatedReasons);
        trace?.SetSummary($"{summary.ElementType} {summary.XPath ?? "<xpath omitted>"}");
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

    public async Task<GetUiaTreeResponse> GetUiaTreeAsync(
        long? windowHandle = null,
        ElementLocator? root = null,
        int depth = 4,
        int maxNodes = 200,
        bool visibleOnly = true,
        bool includeOffViewport = true,
        CancellationToken cancellationToken = default)
    {
        var tree = await GetVisualTreeAsync(
            InspectionBackend.Uia,
            windowHandle,
            root,
            depth,
            maxNodes,
            visibleOnly,
            includeOffViewport,
            interactiveOnly: false,
            interactiveMode: InteractiveMode.Heuristic,
            preset: TreePreset.Standard,
            fields: null,
            cancellationToken).ConfigureAwait(false);

        return new GetUiaTreeResponse(
            Root: ConvertToUiaTreeNode(tree.Root),
            ReturnedNodes: tree.ReturnedNodes,
            ScannedNodes: tree.ScannedNodes,
            Truncated: tree.Truncated,
            TruncatedReason: tree.TruncatedReason);
    }

    public async Task<GetUiaLocatorsResponse> GetUiaLocatorsAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        InspectionBackend? backend = null,
        int maxNodes = DefaultUiaMappingMaxNodes,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_uia_locators");
        try
        {
            var hasLocator = locator is not null;
            var hasElementId = !string.IsNullOrWhiteSpace(elementId);
            if (hasLocator == hasElementId)
            {
                throw new ArgumentException("get_uia_locators requires exactly one of: locator OR elementId.");
            }

            if (backend == InspectionBackend.Auto)
            {
                throw new ArgumentException(
                    "get_uia_locators does not accept backend=Auto. Omit backend or use backend=Uia for a UIA locator; use backend=Wpf for a WPF locator.",
                    nameof(backend));
            }

            if (maxNodes is < 1 or > MaximumUiaMappingNodes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxNodes),
                    $"maxNodes must be between 1 and {MaximumUiaMappingNodes}.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var application = EnsureAttached();
            var automation = EnsureAutomation();
            var controlWalker = automation.TreeWalkerFactory.GetControlViewWalker();
            var rawWalker = automation.TreeWalkerFactory.GetRawViewWalker();

            Window window;
            AutomationElement? element = null;
            string? uiaXPath = null;
            string? mappedUiaElementId = null;
            IReadOnlyList<AutomationElement>? scannedElements = null;
            WpfLocatorIdentity? wpf = null;
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

                if (backend is { } requestedBackend && requestedBackend != handle.Backend)
                {
                    throw new ArgumentException(
                        $"backend={requestedBackend} does not match the elementId backend {handle.Backend}.",
                        nameof(backend));
                }

                ValidateUiaMappingWindowScope(windowHandle, handle.WindowHandle);

                try
                {
                    window = FindWindowByHandle(application, automation, handle.WindowHandle);
                }
                catch
                {
                    throw new InvalidOperationException($"stale_element: window_closed for '{id}'. Call resolve_element again.");
                }

                if (handle.Backend == InspectionBackend.Wpf)
                {
                    var current = await ResolveWpfElementRefAsync(
                        handle,
                        handle.WindowHandle,
                        visibleOnly: false,
                        includeOffViewport: true,
                        interactiveOnly: false,
                        interactiveMode: InteractiveMode.Heuristic,
                        cancellationToken,
                        allowHandleRecovery: false).ConfigureAwait(false);
                    var source = RefreshWpfMappingSource(handle, current);
                    var mapping = MapWpfHandleToUia(
                        window,
                        controlWalker,
                        rawWalker,
                        source,
                        maxNodes,
                        cancellationToken);
                    element = mapping.SelectedElement;
                    uiaXPath = mapping.SelectedXPath;
                    mappedUiaElementId = mapping.SelectedElementId;
                    scannedElements = mapping.ScannedElements;
                    uiaMapping = mapping.Diagnostics;
                    wpf = CreateWpfLocatorIdentity(source, id) with { Bounds = source.Bounds };
                }
                else
                {
                    element = ResolveUiaElementById(
                        window,
                        rawWalker,
                        id,
                        out uiaXPath,
                        UiaHandleResolutionMode.ObserveCurrentXPathOccupant);
                    mappedUiaElementId = id;
                }
            }
            else
            {
                window = windowHandle is long requestedHandle
                    ? FindWindowByHandle(application, automation, requestedHandle)
                    : FindMainWindow(application, automation);

                var locatorBackend = backend ?? InspectionBackend.Uia;
                if (locatorBackend == InspectionBackend.Wpf)
                {
                    if (!HasStrictStableWpfLocator(locator!))
                    {
                        throw new ArgumentException(
                            "A WPF locator for get_uia_locators must be strict and include an exact automationId or xpath.",
                            nameof(locator));
                    }

                    var hwnd = window.Properties.NativeWindowHandle.Value.ToInt64();
                    var resolved = await ResolveWpfElementRefDetailedAsync(
                        locator!,
                        hwnd,
                        visibleOnly: false,
                        includeOffViewport: true,
                        interactiveOnly: false,
                        interactiveMode: InteractiveMode.Heuristic,
                        cancellationToken).ConfigureAwait(false);
                    var wpfElementId = _elementHandles.RegisterWpf(
                        hwnd,
                        resolved.XPath,
                        resolved.ElementIdWpf,
                        resolved.Type,
                        resolved.AutomationId,
                        resolved.Name,
                        resolved.ClassName,
                        resolved.Bounds);
                    var source = RequireHandle(wpfElementId);
                    var mapping = MapWpfHandleToUia(
                        window,
                        controlWalker,
                        rawWalker,
                        source,
                        maxNodes,
                        cancellationToken);
                    element = mapping.SelectedElement;
                    uiaXPath = mapping.SelectedXPath;
                    mappedUiaElementId = mapping.SelectedElementId;
                    scannedElements = mapping.ScannedElements;
                    uiaMapping = mapping.Diagnostics;
                    wpf = CreateWpfLocatorIdentity(source, wpfElementId) with { Bounds = source.Bounds };
                }
                else
                {
                    element = ResolveElement(window, locator!, controlWalker, rawWalker);
                    uiaXPath = ComputeXPath(window, element, rawWalker);

                    if (HasStableWpfLocator(locator!))
                    {
                        try
                        {
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
                                wpf = CreateWpfLocatorIdentity(wpfTarget.Handle, wpfTarget.ElementId);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (element is null || uiaXPath is null)
            {
                var mappingResponse = new GetUiaLocatorsResponse(wpf, null, null, null, uiaMapping);
                trace?.SetSummary(
                    $"mapping={uiaMapping?.Status?.ToString() ?? "none"} " +
                    $"candidates={uiaMapping?.ReturnedCandidates ?? 0}/{uiaMapping?.TotalCandidates ?? 0}");
                return mappingResponse;
            }

            var allElements = scannedElements ?? EnumerateSelfAndDescendantsDepthFirst(window, controlWalker).ToArray();
            var flaUiXPath = ComputeFlaUiXPath(window, element, controlWalker);
            var uia = CreateUiaLocatorIdentity(element, uiaXPath, flaUiXPath) with
            {
                ElementId = mappedUiaElementId
            };
            var suggestions = CreateUiaLocatorSuggestions(element, uiaXPath, flaUiXPath, allElements);
            var flaui = CreateFlaUiSnippets(suggestions);
            var response = new GetUiaLocatorsResponse(wpf, uia, suggestions, flaui, uiaMapping);
            trace?.SetSummary($"{uia.ControlType} {uia.UiaXPath} recommended={suggestions.Recommended}");
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

    private static UiaTreeNode ConvertToUiaTreeNode(TreeNode node) =>
        new(
            ControlType: node.Type,
            AutomationId: node.AutomationId,
            Name: node.Name,
            ClassName: node.ClassName,
            UiaXPath: node.XPath,
            ChildrenCount: node.ChildrenCount,
            Children: node.Children.Select(ConvertToUiaTreeNode).ToArray());

    private static WpfLocatorIdentity CreateWpfLocatorIdentity(ElementHandle handle, string? elementId) =>
        new(
            Type: handle.Type,
            AutomationId: handle.AutomationId,
            Name: handle.Name,
            ClassName: handle.ClassName,
            WpfXPath: handle.XPath,
            ElementId: elementId);

    private static bool HasStableWpfLocator(ElementLocator locator) =>
        !string.IsNullOrWhiteSpace(locator.AutomationId) ||
        !string.IsNullOrWhiteSpace(locator.XPath);

    private static bool HasStrictStableWpfLocator(ElementLocator locator) =>
        locator.Strict && HasStableWpfLocator(locator);

    private static UiaLocatorIdentity CreateUiaLocatorIdentity(AutomationElement element, string uiaXPath, string flaUiXPath) =>
        new(
            ControlType: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            ClassName: GetClassName(element),
            UiaXPath: uiaXPath,
            Bounds: ToRect(element.BoundingRectangle),
            IsEnabled: element.IsEnabled,
            IsOffscreen: element.IsOffscreen,
            HelpText: NullIfWhiteSpace(TryGetUiaProperty<string>(element, "HelpText")),
            IsControlElement: TryGetUiaProperty<bool>(element, "IsControlElement"),
            IsContentElement: TryGetUiaProperty<bool>(element, "IsContentElement"),
            FlaUiXPath: flaUiXPath);

    private static UiaLocatorSuggestions CreateUiaLocatorSuggestions(
        AutomationElement element,
        string uiaXPath,
        string flaUiXPath,
        IReadOnlyCollection<AutomationElement> allElements)
    {
        var controlType = element.ControlType.ToString();
        var automationId = GetAutomationId(element);
        var name = GetName(element);
        var className = GetClassName(element);

        var byAutomationId = string.IsNullOrWhiteSpace(automationId)
            ? null
            : $"cf.ByAutomationId(\"{EscapeCSharpString(automationId)}\")";
        var byName = string.IsNullOrWhiteSpace(name)
            ? null
            : $"cf.ByName(\"{EscapeCSharpString(name)}\")";
        var byClassName = string.IsNullOrWhiteSpace(className)
            ? null
            : $"cf.ByClassName(\"{EscapeCSharpString(className)}\")";
        var byControlType = $"cf.ByControlType(ControlType.{controlType})";

        var automationIdUnique = !string.IsNullOrWhiteSpace(automationId) &&
                                 allElements.Count(e => string.Equals(GetAutomationId(e), automationId, StringComparison.Ordinal)) == 1;
        var automationIdUniqueForType = !string.IsNullOrWhiteSpace(automationId) &&
                                        allElements.Count(e =>
                                            string.Equals(GetAutomationId(e), automationId, StringComparison.Ordinal) &&
                                            string.Equals(e.ControlType.ToString(), controlType, StringComparison.Ordinal)) == 1;
        var nameUniqueForType = !string.IsNullOrWhiteSpace(name) &&
                                allElements.Count(e =>
                                    string.Equals(GetName(e), name, StringComparison.Ordinal) &&
                                    string.Equals(e.ControlType.ToString(), controlType, StringComparison.Ordinal)) == 1;
        var classNameUniqueForType = !string.IsNullOrWhiteSpace(className) &&
                                     allElements.Count(e =>
                                         string.Equals(GetClassName(e), className, StringComparison.Ordinal) &&
                                         string.Equals(e.ControlType.ToString(), controlType, StringComparison.Ordinal)) == 1;

        string recommended;
        string reason;
        if (byAutomationId is not null && automationIdUnique)
        {
            recommended = "byAutomationId";
            reason = "AutomationId is present and unique in the UIA tree.";
        }
        else if (byAutomationId is not null && automationIdUniqueForType)
        {
            recommended = "byAutomationIdAndControlType";
            reason = "AutomationId is present and unique when combined with ControlType.";
        }
        else if (byName is not null && nameUniqueForType)
        {
            recommended = "byNameAndControlType";
            reason = "AutomationId is missing or not unique; Name is unique for this ControlType.";
        }
        else if (byClassName is not null && classNameUniqueForType)
        {
            recommended = "byClassNameAndControlType";
            reason = "AutomationId and Name are unavailable or not unique; ClassName is unique for this ControlType.";
        }
        else
        {
            recommended = "byXPath";
            reason = "No stable unique AutomationId, Name, or ClassName locator was available; XPath is the only concrete locator.";
        }

        return new UiaLocatorSuggestions(
            ByAutomationId: byAutomationId,
            ByName: byName,
            ByClassName: byClassName,
            ByControlType: byControlType,
            ByXPath: uiaXPath,
            Recommended: recommended,
            RecommendedReason: reason,
            ByFlaUiXPath: flaUiXPath);
    }

    private static FlaUiLocatorSnippets CreateFlaUiSnippets(UiaLocatorSuggestions suggestions)
    {
        var condition = suggestions.Recommended switch
        {
            "byAutomationId" => suggestions.ByAutomationId!,
            "byAutomationIdAndControlType" => $"{suggestions.ByAutomationId!}.And({suggestions.ByControlType})",
            "byNameAndControlType" => $"{suggestions.ByName!}.And({suggestions.ByControlType})",
            "byClassNameAndControlType" => $"{suggestions.ByClassName!}.And({suggestions.ByControlType})",
            _ => null
        };

        var xpathLiteral = EscapeCSharpString(suggestions.ByFlaUiXPath ?? suggestions.ByXPath);
        return new FlaUiLocatorSnippets(
            FindFirst: condition is null
                ? $"window.FindFirstByXPath(\"{xpathLiteral}\")"
                : $"window.FindFirstDescendant(cf => {condition})",
            FindFirstByXPath: $"window.FindFirstByXPath(\"{xpathLiteral}\")");
    }

    private static T? TryGetUiaProperty<T>(AutomationElement element, string propertyName)
    {
        try
        {
            var property = element.Properties.GetType().GetProperty(propertyName);
            var wrapper = property?.GetValue(element.Properties);
            var value = wrapper is null ? null : TryGetWrapperValue(wrapper);
            if (value is T typed)
            {
                return typed;
            }
        }
        catch
        {
        }

        return default;
    }

    private static string EscapeCSharpString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            _ = character switch
            {
                '\\' => builder.Append(@"\\"),
                '"' => builder.Append("\\\""),
                '\0' => builder.Append(@"\0"),
                '\a' => builder.Append(@"\a"),
                '\b' => builder.Append(@"\b"),
                '\f' => builder.Append(@"\f"),
                '\n' => builder.Append(@"\n"),
                '\r' => builder.Append(@"\r"),
                '\t' => builder.Append(@"\t"),
                '\v' => builder.Append(@"\v"),
                _ when char.IsControl(character) => builder.Append(@"\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)),
                _ => builder.Append(character)
            };
        }

        return builder.ToString();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
            if (_processIdentity is null)
            {
                return application!;
            }

            var runningState = ProcessTargetResolver.Observe(_processIdentity.Value);
            if (runningState == ProcessInstanceState.Current)
            {
                return application!;
            }

            if (runningState == ProcessInstanceState.Unavailable)
            {
                throw FailureDiagnostics.Exception(
                    code: FailureDiagnostics.Codes.ProcessStateUnavailable,
                    stage: FailureDiagnostics.Stages.TargetShutdown,
                    detail: "The target process state could not be observed, so the session was not discarded.",
                    retryable: true,
                    recoveryActions: [FailureDiagnostics.Recovery.Retry]);
            }
        }

        var identity = _processIdentity;
        var processState = identity is null
            ? ProcessInstanceState.ExitedOrReused
            : ProcessTargetResolver.Observe(identity.Value);
        if (processState == ProcessInstanceState.Unavailable)
        {
            throw FailureDiagnostics.Exception(
                code: "process_state_unavailable",
                stage: "target_shutdown",
                detail: "The target process state could not be observed, so the session was not discarded.",
                retryable: true,
                recoveryActions: ["retry"]);
        }

        Cleanup();
        throw FailureDiagnostics.Exception(
            code: "target_exited",
            stage: "target_shutdown",
            detail: "The target process exited or was replaced, so this session can no longer be used.",
            retryable: false,
            recoveryActions: ["reattach", "restart_target"]);
    }

    private ProcessInstanceIdentity EnsureAttachedProcessIdentityCurrent(int expectedPid)
    {
        var identity = _processIdentity ?? ProcessTargetResolver.ResolveByPid(expectedPid).Identity;
        if (identity.Pid != expectedPid)
        {
            throw new ActionableFailureException(FailureDiagnostics.TargetExited(processReplaced: true));
        }

        return ProcessTargetResolver.Observe(identity) switch
        {
            ProcessInstanceState.Current => identity,
            ProcessInstanceState.ExitedOrReused => throw new ActionableFailureException(
                FailureDiagnostics.TargetExited()),
            ProcessInstanceState.Unavailable => throw FailureDiagnostics.Exception(
                code: FailureDiagnostics.Codes.ProcessStateUnavailable,
                stage: FailureDiagnostics.Stages.TargetShutdown,
                detail: "The target process identity could not be verified.",
                retryable: true,
                recoveryActions: [FailureDiagnostics.Recovery.Retry]),
            _ => throw new ArgumentOutOfRangeException()
        };
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

    private Window FindMainWindow(Application application, UIA3Automation automation, TimeSpan? timeout = null)
    {
        var window = application.GetMainWindow(automation, timeout ?? TimeSpan.FromSeconds(10));
        if (window is null)
        {
            throw new InvalidOperationException("Failed to find the main window within the timeout.");
        }

        ObserveWindowHandle(window.Properties.NativeWindowHandle.Value.ToInt64());
        return window;
    }

    private IReadOnlyList<Window> GetAllTopLevelWindows(Application application, UIA3Automation automation)
    {
        var windows = new List<Window>();
        var handles = new HashSet<long>();

        TryAddWindow(application.MainWindowHandle);
        foreach (var hwnd in EnumerateVisibleTopLevelWindowHandles(application.ProcessId))
        {
            TryAddWindow(hwnd);
        }

        return windows;

        void TryAddWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var handle = hwnd.ToInt64();
            if (!handles.Add(handle))
            {
                return;
            }

            try
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                if (processId != application.ProcessId)
                {
                    return;
                }

                var element = automation.FromHandle(hwnd);
                var window = element.AsWindow();

                var bounds = window.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(GetWindowTitle(window)))
                {
                    return;
                }

                ObserveWindowHandle(handle);
                windows.Add(window);
            }
            catch
            {
            }
        }
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

    private Window FindWindowByHandle(Application application, UIA3Automation automation, long nativeWindowHandle)
    {
        var hwnd = new IntPtr(nativeWindowHandle);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            if (IsRetiredWindowHandle(nativeWindowHandle))
            {
                throw CreateStaleWindowException(nativeWindowHandle);
            }

            throw CreateWindowClosedException(nativeWindowHandle);
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            if (IsRetiredWindowHandle(nativeWindowHandle))
            {
                throw CreateStaleWindowException(nativeWindowHandle);
            }

            throw CreateWindowClosedException(nativeWindowHandle);
        }

        if (processId != application.ProcessId)
        {
            if (IsRetiredWindowHandle(nativeWindowHandle))
            {
                throw CreateStaleWindowException(nativeWindowHandle);
            }

            throw new InvalidOperationException(
                $"window_outside_session: window handle {nativeWindowHandle} belongs to process {processId}, " +
                $"not attached process {application.ProcessId}. Start or select the owning session.");
        }

        ObserveWindowHandle(nativeWindowHandle);
        try
        {
            var element = automation.FromHandle(hwnd);
            return element.AsWindow();
        }
        catch (Exception ex)
        {
            if (!IsWindow(hwnd))
            {
                if (IsRetiredWindowHandle(nativeWindowHandle))
                {
                    throw CreateStaleWindowException(nativeWindowHandle, ex);
                }

                throw CreateWindowClosedException(nativeWindowHandle, ex);
            }

            GetWindowThreadProcessId(hwnd, out var currentProcessId);
            if (currentProcessId == 0)
            {
                if (IsRetiredWindowHandle(nativeWindowHandle))
                {
                    throw CreateStaleWindowException(nativeWindowHandle, ex);
                }

                throw CreateWindowClosedException(nativeWindowHandle, ex);
            }

            if (currentProcessId != application.ProcessId)
            {
                if (IsRetiredWindowHandle(nativeWindowHandle))
                {
                    throw CreateStaleWindowException(nativeWindowHandle, ex);
                }

                throw new InvalidOperationException(
                    $"window_outside_session: window handle {nativeWindowHandle} belongs to process {currentProcessId}, " +
                    $"not attached process {application.ProcessId}. Start or select the owning session.",
                    ex);
            }

            throw new InvalidOperationException(
                $"window_uia_unavailable: window handle {nativeWindowHandle} belongs to the attached process but " +
                "does not expose a usable UI Automation window. Select another window with list_windows; " +
                "owner-drawn dialogs without UIA are outside the supported scope.",
                ex);
        }
    }

    private static InvalidOperationException CreateWindowClosedException(long nativeWindowHandle, Exception? innerException = null) =>
        new(
            $"window_closed: window handle {nativeWindowHandle} is no longer valid. " +
            "Call list_windows and select a live window.",
            innerException);

    private static InvalidOperationException CreateStaleWindowException(
        long nativeWindowHandle,
        Exception? innerException = null) =>
        new(
            $"stale_window: process_replaced for window handle {nativeWindowHandle}. " +
            "Call list_windows in the successor session and select a live window.",
            innerException);

    private Window FindWindowByTitle(Application application, UIA3Automation automation, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var windows = GetAllTopLevelWindows(application, automation).ToArray();

        var exactNative = windows
            .Where(w => w is not null && string.Equals(GetWindowTitle(w), title, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exactNative.Length == 1)
        {
            return exactNative[0];
        }

        if (exactNative.Length > 1)
        {
            throw new InvalidOperationException($"Multiple windows found with title '{title}'. Provide windowHandle instead.");
        }

        var exactAutomationName = windows
            .Where(w => w is not null &&
                string.Equals(GetWindowAutomationName(w), title, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exactAutomationName.Length == 1)
        {
            return exactAutomationName[0];
        }

        if (exactAutomationName.Length > 1)
        {
            throw new InvalidOperationException($"Multiple windows found with title '{title}'. Provide windowHandle instead.");
        }

        var contains = windows
            .Where(w => w is not null &&
                (GetWindowTitle(w).Contains(title, StringComparison.OrdinalIgnoreCase) ||
                 GetWindowAutomationName(w).Contains(title, StringComparison.OrdinalIgnoreCase)))
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

    private WindowInfo ToWindowInfo(Window window)
    {
        var bounds = window.BoundingRectangle;
        var handle = window.Properties.NativeWindowHandle.Value;
        ObserveWindowHandle(handle.ToInt64());
        var ownerHandle = TryGetOwnerHandle(handle);
        if (ownerHandle is long owner)
        {
            TrackOrRejectExternalWindowHandle(owner);
        }

        return new WindowInfo(
            Title: GetWindowTitle(window),
            Handle: handle.ToInt64(),
            Bounds: new Rect(
                X: bounds.Left,
                Y: bounds.Top,
                Width: bounds.Width,
                Height: bounds.Height),
            IsVisible: GetWindowVisibility(window, handle),
            IsEnabled: GetWindowEnabled(window, handle))
        {
            OwnerHandle = ownerHandle,
            IsModal = TryGetModalState(window, ownerHandle),
            FrameworkId = TryGetFrameworkId(window)
        };
    }

    private static bool GetWindowVisibility(Window window, IntPtr handle) =>
        ResolveWindowVisibility(
            TryGetNativeWindowVisibility(handle),
            () => window.IsOffscreen);

    private static bool GetWindowEnabled(Window window, IntPtr handle) =>
        ResolveWindowEnabled(
            TryGetNativeWindowEnabled(handle),
            () => window.IsEnabled);

    private static bool? TryGetNativeWindowVisibility(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IsWindowVisible(handle);
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetNativeWindowEnabled(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IsWindowEnabled(handle);
        }
        catch
        {
            return null;
        }
    }

    internal static bool ResolveWindowVisibility(
        bool? nativeIsVisible,
        Func<bool> getProviderIsOffscreen)
    {
        ArgumentNullException.ThrowIfNull(getProviderIsOffscreen);

        var providerIsOffscreen = SafeGetBool(getProviderIsOffscreen);
        if (providerIsOffscreen is bool isOffscreen)
        {
            return !isOffscreen;
        }

        return nativeIsVisible ?? true;
    }

    internal static bool ResolveWindowEnabled(
        bool? nativeIsEnabled,
        Func<bool> getProviderIsEnabled)
    {
        ArgumentNullException.ThrowIfNull(getProviderIsEnabled);

        return SafeGetBool(getProviderIsEnabled) ?? nativeIsEnabled ?? false;
    }

    private static long? TryGetOwnerHandle(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var owner = GetWindow(handle, GW_OWNER);
            return owner == IntPtr.Zero ? null : owner.ToInt64();
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetModalState(Window window, long? ownerHandle)
    {
        try
        {
            return window.IsModal;
        }
        catch
        {
            if (ownerHandle is not long owner || owner == 0 || !OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                return !IsWindowEnabled(new IntPtr(owner));
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? TryGetFrameworkId(Window window)
    {
        try
        {
            return window.FrameworkType == FrameworkType.None
                ? null
                : window.FrameworkType.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string GetNativeAutoRoutingWarning(Window window) =>
        $"backend=auto: target framework {TryGetFrameworkId(window) ?? "native"} is not WPF; used UIA.";

    private static string GetPerWindowAutoRoutingWarning() =>
        "backend=auto: target window is not backed by a WPF HwndSource; used UIA.";

    private static string GetWindowTitle(Window window)
    {
        var handle = window.Properties.NativeWindowHandle.Value;
        if (handle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            try
            {
                var length = GetWindowTextLength(handle);
                if (length > 0)
                {
                    var title = new StringBuilder(length + 1);
                    if (GetWindowText(handle, title, title.Capacity) > 0)
                    {
                        return title.ToString();
                    }
                }
            }
            catch
            {
            }
        }

        return GetWindowAutomationName(window);
    }

    private static string GetWindowAutomationName(Window window)
    {
        try
        {
            return window.Title ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
                ?? throw new InvalidOperationException("element_not_found: Locator did not match any element.");

            var mismatch = DescribeXPathFilterMismatchUia(resolved, locator);
            if (mismatch is not null)
            {
                throw new InvalidOperationException(mismatch);
            }

            if (visibleOnly && !IsVisibleUia(window, resolved, includeOffViewport))
            {
                throw new InvalidOperationException("element_not_found: Locator did not match any element (visibleOnly=true).");
            }

            if (interactiveOnly && !IsInteractiveUia(resolved, interactiveMode))
            {
                throw new InvalidOperationException("element_not_found: Locator did not match any element (interactiveOnly=true).");
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
            throw new InvalidOperationException("element_not_found: Locator did not match any element.");
        }

        return SelectMatch(matches, locator, actionKind)
            ?? throw new InvalidOperationException("element_not_found: Locator did not match any element.");
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

        if (locator.Index is int index)
        {
            if (index < 0)
            {
                throw new InvalidOperationException("index must be >= 0.");
            }

            var orderedByIdentity = OrderMatchesDeterministic(matches, locator);
            if (index >= orderedByIdentity.Count)
            {
                throw new InvalidOperationException(
                    $"Locator matched {orderedByIdentity.Count} elements but index {index} is out of range.");
            }

            return orderedByIdentity[index];
        }

        var ordered = OrderMatchesForAction(matches, locator, actionKind);
        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        if (locator.Strict)
        {
            throw new UiaLocatorAmbiguousException(ordered);
        }

        return ordered[0];
    }

    private sealed class UiaLocatorAmbiguousException : InvalidOperationException
    {
        public UiaLocatorAmbiguousException(IReadOnlyList<AutomationElement> candidates)
            : base(
                $"Locator is ambiguous (found {candidates.Count}). Provide 'index' to disambiguate."
                + BuildAmbiguousCandidatesDetails(candidates, maxCandidates: 5))
        {
            Candidates = candidates.ToArray();
        }

        public IReadOnlyList<AutomationElement> Candidates { get; }
    }

    private static string BuildAmbiguousCandidatesDetails(
        IReadOnlyList<AutomationElement> matches,
        int maxCandidates)
    {
        if (matches.Count == 0 || maxCandidates <= 0)
        {
            return "";
        }

        var take = Math.Min(maxCandidates, matches.Count);
        var details = new StringBuilder();
        details.AppendLine();
        details.AppendLine("Candidates:");

        for (var index = 0; index < take; index++)
        {
            var element = matches[index];
            var bounds = TryGetBounds(element);
            var boundsText = bounds is null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0
                ? "bounds=n/a"
                : $"bounds={bounds.Value.Left},{bounds.Value.Top} {bounds.Value.Width}x{bounds.Value.Height}";

            details.Append("  - ");
            details.Append(GetXPathLabel(element));
            AppendCandidateIdentity(details, "name", GetName(element));
            AppendCandidateIdentity(details, "automationId", GetAutomationId(element));
            details.Append($", {boundsText}");

            var enabled = TryGetBooleanString(() => element.Properties.IsEnabled.Value);
            var offscreen = TryGetBooleanString(() => element.Properties.IsOffscreen.Value);
            if (enabled is not null)
            {
                details.Append($", enabled={enabled}");
            }

            if (offscreen is not null)
            {
                details.Append($", offscreen={offscreen}");
            }

            details.AppendLine();
        }

        if (matches.Count > take)
        {
            details.AppendLine($"  ... and {matches.Count - take} more");
        }

        return details.ToString().TrimEnd();
    }

    private static void AppendCandidateIdentity(StringBuilder details, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            details.Append($", {name}='{value}'");
        }
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

    private static IReadOnlyList<AutomationElement> OrderMatchesDeterministic(
        IReadOnlyList<AutomationElement> matches,
        ElementLocator locator) =>
        OrderMatches(matches, locator, actionKind: null);

    private static IReadOnlyList<AutomationElement> OrderMatchesForAction(
        IReadOnlyList<AutomationElement> matches,
        ElementLocator locator,
        ActionKind actionKind) =>
        OrderMatches(matches, locator, actionKind);

    private static IReadOnlyList<AutomationElement> OrderMatches(
        IReadOnlyList<AutomationElement> matches,
        ElementLocator locator,
        ActionKind? actionKind)
    {
        if (matches.Count <= 1)
        {
            return matches;
        }

        return matches
            .Select((element, ordinal) =>
            {
                var bounds = TryGetBounds(element);
                return new
                {
                    Element = element,
                    Ordinal = ordinal,
                    OffscreenRank = locator.PreferVisible ? GetOffscreenRank(element) : 0,
                    EnabledRank = GetEnabledRank(element),
                    ActionAffinityRank = actionKind is ActionKind value
                        ? GetActionAffinityRank(element, value)
                        : 0,
                    Top = bounds?.Top ?? int.MaxValue,
                    Left = bounds?.Left ?? int.MaxValue,
                    AutomationId = GetAutomationId(element),
                    Name = GetName(element)
                };
            })
            .OrderBy(candidate => candidate.OffscreenRank)
            .ThenBy(candidate => candidate.EnabledRank)
            .ThenBy(candidate => candidate.ActionAffinityRank)
            .ThenBy(candidate => candidate.Top)
            .ThenBy(candidate => candidate.Left)
            .ThenBy(candidate => candidate.AutomationId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Ordinal)
            .Select(candidate => candidate.Element)
            .ToArray();
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
        return TryGetIsOffscreen(element) switch
        {
            false => 0,
            true => 1,
            null => 2
        };
    }

    private static bool? TryGetIsOffscreen(AutomationElement element)
    {
        try
        {
            return element.Properties.IsOffscreen.Value;
        }
        catch
        {
            return null;
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

    private static string ComputeFlaUiXPath(Window window, AutomationElement element, ITreeWalker walker)
    {
        if (AreSameElement(window, element))
        {
            return "/";
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

            segments.Add(ComputeFlaUiXPathSegment(parent, current, walker));
            current = parent;
        }

        segments.Reverse();
        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static string ComputeFlaUiXPathSegment(AutomationElement parent, AutomationElement child, ITreeWalker walker)
    {
        var label = GetFlaUiXPathLabel(child);
        var siblings = GetChildren(parent, walker)
            .Where(c => string.Equals(GetFlaUiXPathLabel(c), label, StringComparison.OrdinalIgnoreCase))
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

    private static string GetFlaUiXPathLabel(AutomationElement element) =>
        element.ControlType.ToString();

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

    private static bool IsElementWithinWindow(Window window, AutomationElement element, ITreeWalker walker)
    {
        if (AreSameElement(window, element))
        {
            return true;
        }

        AutomationElement? current = element;
        while (current is not null)
        {
            AutomationElement? parent;
            try
            {
                parent = walker.GetParent(current);
            }
            catch
            {
                return false;
            }

            if (parent is null)
            {
                return false;
            }

            if (AreSameElement(parent, window))
            {
                return true;
            }

            current = parent;
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
        var discoveredMatches = 0;
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
                discoveredMatches++;
                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    truncatedReason = "maxResults";
                    break;
                }

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

                matches.Add(BuildElementRefUia(current, currentXPath, returnFields, elementId, viewportBounds));
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
            Warnings: null)
        {
            DiscoveredMatches = discoveredMatches
        };
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

    private static ElementRef BuildElementRefUia(
        AutomationElement element,
        string xpath,
        FindReturnFields returnFields,
        string? elementId,
        Rect? viewportBounds = null)
    {
        if (returnFields == FindReturnFields.Standard)
        {
            var rawBounds = TryGetBounds(element);
            var bounds = rawBounds is Rectangle value ? ToRect(value) : null;
            var isOffscreen = ResolveUiaIsOffscreen(TryGetIsOffscreen(element), bounds, viewportBounds);
            bool? isVisible = rawBounds is null || isOffscreen is null
                ? null
                : rawBounds.Value.Width > 0 && rawBounds.Value.Height > 0 && !isOffscreen.Value;

            return new ElementRef(
                Type: element.ControlType.ToString(),
                AutomationId: GetAutomationId(element),
                Name: GetName(element),
                XPath: xpath,
                ClassName: GetClassName(element),
                Bounds: bounds,
                ElementId: elementId)
            {
                IsVisible = isVisible,
                IsOffscreen = isOffscreen
            };
        }

        return new ElementRef(
            Type: element.ControlType.ToString(),
            AutomationId: GetAutomationId(element),
            Name: GetName(element),
            XPath: xpath,
            ElementId: elementId);
    }

    internal static bool? ResolveUiaIsOffscreen(
        bool? providerIsOffscreen,
        Rect? bounds,
        Rect? viewportBounds)
    {
        if (providerIsOffscreen == true)
        {
            return true;
        }

        if (bounds is not Rect elementBounds)
        {
            return providerIsOffscreen;
        }

        // Some providers keep IsOffscreen=false for scrolled descendants, while their
        // bounds still prove that the element cannot intersect the window viewport.
        if (elementBounds.Width <= 0 || elementBounds.Height <= 0)
        {
            return true;
        }

        if (viewportBounds is Rect viewport && !RectIntersects(elementBounds, viewport))
        {
            return true;
        }

        return providerIsOffscreen;
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

    private static PropertyPopulationResult PopulateProperties(
        AutomationElement element,
        IDictionary<string, JsonNode?> destination,
        ElementPropertiesPreset preset,
        int maxProperties,
        PropertyValueBudget valueBudget)
    {
        var props = element.Properties;
        var declaredType = typeof(AutomationElement).GetProperty(nameof(AutomationElement.Properties))?.PropertyType
            ?? props.GetType();

        var properties = declaredType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        var selectedProperties = preset switch
        {
            ElementPropertiesPreset.Summary => properties
                .Where(property => SummaryElementPropertyNames.Contains(property.Name))
                .ToArray(),
            ElementPropertiesPreset.Full => properties,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported property preset.")
        };

        var truncated = selectedProperties.Length > maxProperties;

        foreach (var property in selectedProperties.Take(maxProperties))
        {
            if (valueBudget.IsCharacterLimitReached)
            {
                break;
            }

            object? wrapper;
            try
            {
                wrapper = property.GetValue(props);
            }
            catch (Exception ex)
            {
                if (!BoundedPropertyValueSerializer.TrySerialize(
                        $"<error: {ex.Message}>",
                        valueBudget,
                        out var error))
                {
                    break;
                }

                destination[property.Name] = error;
                continue;
            }

            if (wrapper is null)
            {
                continue;
            }

            var value = TryGetWrapperValue(wrapper);
            if (!BoundedPropertyValueSerializer.TrySerialize(value, valueBudget, out var serialized))
            {
                break;
            }

            destination[property.Name] = serialized;
        }

        return new PropertyPopulationResult(
            Returned: destination.Count,
            Selected: selectedProperties.Length,
            Scanned: properties.Length,
            Truncated: truncated);
    }

    private readonly record struct PropertyPopulationResult(
        int Returned,
        int Selected,
        int Scanned,
        bool Truncated);

    internal static UiaMappingDiagnostics? BoundUiaMappingDiagnostics(
        UiaMappingDiagnostics? mapping,
        PropertyValueBudget valueBudget,
        out bool candidatesLimitReached)
    {
        candidatesLimitReached = false;
        if (mapping is null)
        {
            return null;
        }

        var initialTruncation = valueBudget.Truncation;
        var totalCandidates = mapping.TotalCandidates > 0
            ? mapping.TotalCandidates
            : mapping.Candidates.Count;
        var selectedXPathOmitted = mapping.SelectedXPathOmitted == true;
        string? selectedXPath = null;
        if (mapping.SelectedXPath is not null)
        {
            selectedXPath = BoundedPropertyValueSerializer.SerializeXPath(
                mapping.SelectedXPath,
                valueBudget,
                out selectedXPathOmitted);
        }

        var candidates = new List<UiaMappingCandidate>(
            capacity: Math.Min(mapping.Candidates.Count, MaximumUiaMappingCandidates));
        var anyCandidateXPathOmitted = false;
        foreach (var candidate in mapping.Candidates.Take(MaximumUiaMappingCandidates))
        {
            if (valueBudget.IsCharacterLimitReached)
            {
                break;
            }

            var candidateXPathOmitted = candidate.XPathOmitted == true;
            string? candidateXPath = null;
            if (candidate.XPath is not null)
            {
                candidateXPath = BoundedPropertyValueSerializer.SerializeXPath(
                    candidate.XPath,
                    valueBudget,
                    out candidateXPathOmitted);
            }

            anyCandidateXPathOmitted |= candidateXPathOmitted;
            candidates.Add(new UiaMappingCandidate(
                ElementType: candidate.ElementType,
                AutomationId: BoundedPropertyValueSerializer.SerializeString(candidate.AutomationId, valueBudget),
                Name: BoundedPropertyValueSerializer.SerializeString(candidate.Name, valueBudget),
                ClassName: BoundedPropertyValueSerializer.SerializeString(candidate.ClassName, valueBudget),
                Bounds: candidate.Bounds,
                XPath: candidateXPath,
                Score: candidate.Score,
                XPathOmitted: candidateXPathOmitted ? true : null)
            {
                ElementId = BoundedPropertyValueSerializer.SerializeString(candidate.ElementId, valueBudget),
                Reusable = candidate.Reusable,
                Evidence = candidate.Evidence
            });
        }

        candidatesLimitReached = mapping.Truncated || totalCandidates > MaximumUiaMappingCandidates;
        var selectedElementId = BoundedPropertyValueSerializer.SerializeString(
            mapping.SelectedElementId,
            valueBudget);
        var mappingValueTruncated = valueBudget.Truncation != initialTruncation;

        return new UiaMappingDiagnostics(
            Ambiguous: mapping.Ambiguous,
            SelectedXPath: selectedXPath,
            Candidates: candidates,
            ReturnedCandidates: candidates.Count,
            TotalCandidates: totalCandidates,
            Truncated: mapping.Truncated ||
                       candidates.Count < totalCandidates ||
                       selectedXPathOmitted ||
                       anyCandidateXPathOmitted ||
                       mappingValueTruncated,
            SelectedXPathOmitted: selectedXPathOmitted ? true : null)
        {
            Status = mapping.Status,
            Method = mapping.Method,
            SelectedElementId = selectedElementId,
            Score = mapping.Score,
            ScoreLead = mapping.ScoreLead,
            Evidence = mapping.Evidence,
            ScannedNodes = mapping.ScannedNodes,
            ScanComplete = mapping.ScanComplete,
            TruncatedReason = mapping.TruncatedReason
        };
    }

    private static void PopulatePatterns(
        AutomationElement element,
        IDictionary<string, JsonNode?> destination,
        PropertyValueBudget valueBudget)
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
            if (valueBudget.IsCharacterLimitReached)
            {
                break;
            }

            object? wrapper;
            try
            {
                wrapper = patternProperty.GetValue(patternsObject);
            }
            catch (Exception ex)
            {
                if (!BoundedPropertyValueSerializer.TrySerialize(
                        ex.Message,
                        valueBudget,
                        out var error))
                {
                    break;
                }

                destination[patternProperty.Name] = new JsonObject
                {
                    ["isSupported"] = false,
                    ["error"] = error
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
                var values = ExtractPatternValues(patternProperty.Name, patternInstance, valueBudget);
                if (values.Count > 0)
                {
                    json["values"] = values;
                }
            }

            destination[patternProperty.Name] = json;
        }
    }

    private static JsonObject ExtractPatternValues(
        string patternName,
        object patternInstance,
        PropertyValueBudget valueBudget)
    {
        var values = new JsonObject();

        switch (patternName)
        {
            case "Value":
                AddPatternValue(values, patternInstance, "Value", valueBudget);
                AddPatternValue(values, patternInstance, "IsReadOnly", valueBudget);
                break;
            case "Toggle":
                AddPatternValue(values, patternInstance, "ToggleState", valueBudget);
                break;
            case "RangeValue":
                AddPatternValue(values, patternInstance, "Value", valueBudget);
                AddPatternValue(values, patternInstance, "Minimum", valueBudget);
                AddPatternValue(values, patternInstance, "Maximum", valueBudget);
                AddPatternValue(values, patternInstance, "IsReadOnly", valueBudget);
                break;
            case "Scroll":
                AddPatternValue(values, patternInstance, "HorizontallyScrollable", valueBudget);
                AddPatternValue(values, patternInstance, "VerticallyScrollable", valueBudget);
                AddPatternValue(values, patternInstance, "HorizontalScrollPercent", valueBudget);
                AddPatternValue(values, patternInstance, "VerticalScrollPercent", valueBudget);
                AddPatternValue(values, patternInstance, "HorizontalViewSize", valueBudget);
                AddPatternValue(values, patternInstance, "VerticalViewSize", valueBudget);
                break;
            case "ExpandCollapse":
                AddPatternValue(values, patternInstance, "ExpandCollapseState", valueBudget);
                break;
            case "SelectionItem":
                AddPatternValue(values, patternInstance, "IsSelected", valueBudget);
                break;
            case "Selection":
                AddPatternValue(values, patternInstance, "CanSelectMultiple", valueBudget);
                AddPatternValue(values, patternInstance, "IsSelectionRequired", valueBudget);
                break;
            case "Window":
                AddPatternValue(values, patternInstance, "IsModal", valueBudget);
                AddPatternValue(values, patternInstance, "IsTopmost", valueBudget);
                AddPatternValue(values, patternInstance, "WindowInteractionState", valueBudget);
                AddPatternValue(values, patternInstance, "WindowVisualState", valueBudget);
                break;
        }

        return values;
    }

    private static void AddPatternValue(
        JsonObject values,
        object patternInstance,
        string propertyName,
        PropertyValueBudget valueBudget)
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
        if (BoundedPropertyValueSerializer.TrySerialize(unwrapped, valueBudget, out var serialized))
        {
            values[propertyName] = serialized;
        }
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

        _processIdentity = null;
    }
}
