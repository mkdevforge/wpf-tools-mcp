using System.Diagnostics;
using System.Globalization;
using System.Text;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private static readonly TimeSpan AutoAgentFailureRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly string[] PublicPerformanceErrorCodes =
    [
        "performance_already_running",
        "performance_not_running",
        "performance_run_not_owned",
        "performance_run_id_mismatch",
        "performance_stop_failed"
    ];
    private readonly object _agentSync = new();
    private readonly object _agentDisposalSync = new();
    private readonly HashSet<Task> _pendingAgentDisposals = [];
    private AgentClient? _agentClient;
    private string? _agentPipeName;
    private int? _agentPid;
    private AgentCapabilitiesResponse? _agentCapabilities;
    private FailureInfo? _agentAutoConnectFailure;
    private DateTimeOffset? _agentAutoConnectFailureAtUtc;
    private long _agentAutoConnectAttemptSequence;

    public bool IsAgentConnected
    {
        get
        {
            lock (_agentSync)
            {
                return _agentClient is not null && _agentClient.IsConnected;
            }
        }
    }

    public string WpfBackendCapabilityState
    {
        get
        {
            lock (_agentSync)
            {
                if (_agentClient is not null && _agentClient.IsConnected)
                {
                    return "ready";
                }

                return _agentAutoConnectFailure is null
                    ? "not_initialized"
                    : "unavailable";
            }
        }
    }

    internal BackendCapabilityState GetWpfBackendCapabilityState()
    {
        lock (_agentSync)
        {
            if (_agentClient is not null && _agentClient.IsConnected)
            {
                return new BackendCapabilityState("wpf", "ready");
            }

            if (_agentAutoConnectFailure is null)
            {
                return new BackendCapabilityState("wpf", "not_initialized");
            }

            var failure = _agentAutoConnectFailure;
            if (failure.Retryable is true && _agentAutoConnectFailureAtUtc is { } recordedAt)
            {
                var remaining = GetAutoAgentFailureRetryDelay(failure) - (DateTimeOffset.UtcNow - recordedAt);
                failure = failure with
                {
                    RetryAfterMs = Math.Max(0, (int)Math.Ceiling(remaining.TotalMilliseconds))
                };
            }

            return new BackendCapabilityState("wpf", "unavailable")
            {
                Failure = failure
            };
        }
    }

    internal FailureInfo? GetWpfBackendFailure() =>
        GetWpfBackendCapabilityState().Failure;

    private sealed record WpfAgentTarget(
        long? WindowHandle,
        ElementLocator? Locator,
        string? AgentElementId,
        string? PublicElementId,
        ElementLocator? RecoveryLocator,
        ElementHandle? Handle);

    private WpfAgentTarget PrepareWpfAgentTarget(
        string toolName,
        ElementLocator? locator,
        string? elementId,
        long? windowHandle)
    {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException($"{toolName} requires exactly one of: locator OR elementId.");
        }

        if (!hasElementId)
        {
            return new WpfAgentTarget(windowHandle, locator, null, null, null, null);
        }

        var id = elementId!.Trim();
        var handle = RequireHandle(id);
        if (handle.Backend != InspectionBackend.Wpf)
        {
            throw new InvalidOperationException($"elementId '{id}' is not a WPF handle.");
        }

        if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
        {
            throw new ArgumentException("windowHandle does not match the elementId window.");
        }

        var recoveryLocator = CreateWpfHandleRecoveryLocator(handle);
        if (!string.IsNullOrWhiteSpace(handle.WpfAgentElementId))
        {
            return new WpfAgentTarget(handle.WindowHandle, null, handle.WpfAgentElementId, id, recoveryLocator, handle);
        }

        return new WpfAgentTarget(handle.WindowHandle, recoveryLocator, null, id, null, handle);
    }

    private static bool IsWpfAgentStaleOrNotFound(Exception ex)
    {
        var message = GetInternalFailureMessage(ex);
        return message.Contains("wpf_resolve:not_found:", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("wpf_handle_stale:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRetryWpfAgentTarget(WpfAgentTarget target, object? fallbackRequest, Exception ex) =>
        fallbackRequest is not null &&
        target.PublicElementId is not null &&
        target.AgentElementId is not null &&
        IsWpfAgentStaleOrNotFound(ex);

    private async Task<T> CallWpfAgentTargetAsync<T>(
        AgentClient client,
        string method,
        object request,
        object? fallbackRequest,
        WpfAgentTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.CallAsync<T>(method, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanRetryWpfAgentTarget(target, fallbackRequest, ex))
        {
            try
            {
                return await client.CallAsync<T>(method, fallbackRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (target.PublicElementId is not null && IsWpfAgentStaleOrNotFound(retryEx))
            {
                throw CreateStaleElementException(target, retryEx);
            }
        }
        catch (Exception ex) when (target.PublicElementId is not null && IsWpfAgentStaleOrNotFound(ex))
        {
            throw CreateStaleElementException(target, ex);
        }
    }

    private static InvalidOperationException CreateStaleElementException(WpfAgentTarget target, Exception inner)
    {
        var context = target.Handle is null
            ? ""
            : $" Last known WPF identity: type={target.Handle.Type}, automationId={target.Handle.AutomationId}, name={target.Handle.Name}, xpath={target.Handle.XPath}.";
        return new InvalidOperationException(
            $"stale_element: not_found for '{target.PublicElementId}'.{context} Call resolve_element again.");
    }

    private async Task<ElementRef> StripAgentElementIdAsync(
        AgentClient client,
        ElementRef element,
        string? publicElementId)
    {
        if (!string.IsNullOrWhiteSpace(publicElementId))
        {
            var update = _elementHandles.TryUpdateWpfResolution(publicElementId, element);
            await TryReleaseWpfAgentElementAsync(
                client,
                update.WpfAgentElementIdToRelease).ConfigureAwait(false);
        }

        return StripAgentElementId(element);
    }

    private static ElementRef StripAgentElementId(ElementRef element) =>
        element with { ElementIdWpf = null };

    private static async Task TryReleaseWpfAgentElementAsync(
        AgentClient client,
        string? agentElementId)
    {
        if (string.IsNullOrWhiteSpace(agentElementId))
        {
            return;
        }

        try
        {
            _ = await client.CallAsync<ReleaseElementResponse>(
                "wpf/release_element",
                new ReleaseWpfElementRequest(agentElementId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Rebinding succeeded locally; stale in-proc handle cleanup is best-effort.
        }
    }

    public Task<InjectAgentResponse> InjectAgentAsync(CancellationToken cancellationToken = default) =>
        InjectAgentAsync(initialWindowHandle: null, cancellationToken);

    public async Task<InjectAgentResponse> InjectAgentAsync(
        long? initialWindowHandle,
        CancellationToken cancellationToken = default)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var operationToken = operationCancellation.Token;
        var trace = BeginTraceSpan("inject_agent");
        var stage = FailureDiagnostics.Stages.Attachment;
        ProcessIntegrityLevelComparison? integrityComparison = null;
        try
        {
            operationToken.ThrowIfCancellationRequested();
            var application = EnsureAttached();
            var automation = EnsureAutomation();

            var pid = application.ProcessId;
            var processIdentity = EnsureAttachedProcessIdentityCurrent(pid);
            using var process = Process.GetProcessById(pid);
            var pipeName = AgentPipeName.Compute(processIdentity);

            AgentClient? existingClient;
            string? existingPipeName;
            int? existingPid;
            lock (_agentSync)
            {
                existingClient = _agentClient;
                existingPipeName = _agentPipeName;
                existingPid = _agentPid;
            }

            if (existingClient is not null &&
                existingClient.IsConnected &&
                existingPipeName is not null &&
                existingPid == pid)
            {
                stage = FailureDiagnostics.Stages.Protocol;
                try
                {
                    var capabilities = await VerifyAgentForAttachedProcessAsync(
                        existingClient,
                        pid,
                        operationToken);
                    lock (_agentSync)
                    {
                        if (ReferenceEquals(_agentClient, existingClient))
                        {
                            _agentCapabilities = capabilities;
                        }
                    }

                    var response = new InjectAgentResponse(Injected: false, PipeName: existingPipeName);
                    ClearAutoAgentFailure();
                    trace?.SetSummary($"injected={response.Injected} pipe={response.PipeName}");
                    return response;
                }
                catch (Exception) when (!operationToken.IsCancellationRequested)
                {
                    await CleanupAgentAsync().ConfigureAwait(false);
                }
            }

            if (existingClient is not null &&
                (existingPid != pid || !existingClient.IsConnected || existingPipeName is null))
            {
                await CleanupAgentAsync().ConfigureAwait(false);
            }

            // Connect-first lets a restarted MCP server reuse an agent without injecting again.
            stage = FailureDiagnostics.Stages.PipeConnection;
            var connectFirstClient = await TryConnectToAgentWithRetryAsync(
                pipeName,
                totalTimeout: TimeSpan.FromSeconds(2),
                operationToken);

            if (connectFirstClient is not null)
            {
                AgentCapabilitiesResponse capabilities;
                try
                {
                    stage = FailureDiagnostics.Stages.Protocol;
                    capabilities = await VerifyAgentForAttachedProcessAsync(
                        connectFirstClient,
                        pid,
                        operationToken);
                    lock (_agentSync)
                    {
                        _agentClient = connectFirstClient;
                        _agentPipeName = pipeName;
                        _agentPid = pid;
                        _agentCapabilities = capabilities;
                    }
                }
                catch
                {
                    await connectFirstClient.DisposeAsync();
                    throw;
                }

                var response = new InjectAgentResponse(Injected: false, PipeName: pipeName);
                ClearAutoAgentFailure();
                trace?.SetSummary($"injected={response.Injected} pipe={response.PipeName}");
                return response;
            }

            stage = FailureDiagnostics.Stages.Injection;
            if (ProcessIntegrityLevelInspector.TryCompareWithCurrentProcess(pid, out var measuredIntegrity))
            {
                integrityComparison = measuredIntegrity;
                if (measuredIntegrity == ProcessIntegrityLevelComparison.TargetHigher)
                {
                    throw new ActionableFailureException(
                        FailureDiagnostics.AccessDenied(stage, measuredIntegrity));
                }
            }

            var assets = Phase2Assets.ResolveFromAppBase();

            stage = FailureDiagnostics.Stages.Attachment;
            var window = SelectInitialInjectionWindow(
                initialWindowHandle,
                requestedWindowHandle => FindWindowByHandle(application, automation, requestedWindowHandle),
                () => FindMainWindow(application, automation));
            var hwnd = window.Properties.NativeWindowHandle.Value;
            if (hwnd == IntPtr.Zero)
            {
                throw FailureDiagnostics.Exception(
                    FailureDiagnostics.Codes.AttachmentFailed,
                    stage,
                    "The target window handle is unavailable for WPF attachment.",
                    retryable: true,
                    recoveryActions: [FailureDiagnostics.Recovery.Retry, FailureDiagnostics.Recovery.UseUia]);
            }

            stage = FailureDiagnostics.Stages.ArchitectureDetection;
            var architecture = ProcessArchitectureDetector.GetProcessArchitecture(process);

            stage = FailureDiagnostics.Stages.Injection;
            _ = EnsureAttachedProcessIdentityCurrent(pid);
            var injectResult = await SnoopInjector.InjectAsync(
                assets,
                targetPid: pid,
                targetHwnd: hwnd.ToInt64(),
                targetArchitecture: architecture,
                pipeName: pipeName,
                cancellationToken: operationToken);

            if (injectResult.ExitCode != 0)
            {
                throw CreateInjectorFailureException(injectResult, integrityComparison);
            }

            stage = FailureDiagnostics.Stages.PipeConnection;
            var client = await ConnectToAgentWithRetryAsync(pipeName, operationToken);
            AgentCapabilitiesResponse injectedCapabilities;
            try
            {
                stage = FailureDiagnostics.Stages.Protocol;
                injectedCapabilities = await VerifyAgentForAttachedProcessAsync(
                    client,
                    pid,
                    operationToken);
                lock (_agentSync)
                {
                    _agentClient = client;
                    _agentPipeName = pipeName;
                    _agentPid = pid;
                    _agentCapabilities = injectedCapabilities;
                }
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            var finalResponse = new InjectAgentResponse(Injected: true, PipeName: pipeName);
            ClearAutoAgentFailure();
            trace?.SetSummary($"injected={finalResponse.Injected} pipe={finalResponse.PipeName}");
            return finalResponse;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ActionableFailureException ex)
        {
            var failure = PreferTargetStateFailure(ex.Failure);
            var reported = failure == ex.Failure
                ? ex
                : new ActionableFailureException(failure, ex);
            SetAutoAgentFailure(failure);
            trace?.SetError(reported);
            throw reported;
        }
        catch (Exception ex)
        {
            var classified = FailureDiagnostics.Classify(ex, stage, integrityComparison);
            var actionable = new ActionableFailureException(
                PreferTargetStateFailure(classified),
                ex);
            SetAutoAgentFailure(actionable);
            trace?.SetError(actionable);
            throw actionable;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    public async Task<AgentPingResponse> AgentPingAsync(CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("agent_ping");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            var pong = await client.CallAsync<string>("ping", @params: null, cancellationToken);
            var response = new AgentPingResponse(pong);
            trace?.SetSummary($"message={response.Message}");
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

    public async Task<PerformanceStartResponse> PerformanceStartAsync(
        int probeIntervalMs = 50,
        int autoStopAfterMs = 30000,
        bool resetIfRunning = false,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("performance_start");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            var request = new PerformanceStartRequest(probeIntervalMs, autoStopAfterMs, resetIfRunning);
            var response = await CallPerformanceAgentAsync(
                () => client.CallAsync<PerformanceStartResponse>(
                    "wpf/performance_start",
                    request,
                    cancellationToken));
            trace?.SetSummary($"runId={response.RunId} startedAt={response.StartedAtUtc:O}");
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

    public async Task<PerformanceStopResponse> PerformanceStopAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var trace = BeginTraceSpan("performance_stop");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            var request = new PerformanceStopRequest(runId.Trim());
            var response = await CallPerformanceAgentAsync(
                () => client.CallAsync<PerformanceStopResponse>(
                    "wpf/performance_stop",
                    request,
                    cancellationToken));
            trace?.SetSummary($"runId={runId.Trim()}");
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

    internal static async Task<T> CallPerformanceAgentAsync<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (AgentRemoteException ex)
        {
            var publicCode = GetPublicPerformanceErrorCode(ex.RemoteMessage);
            if (publicCode is null)
            {
                throw;
            }

            throw new InvalidOperationException(publicCode, ex);
        }
    }

    private static string? GetPublicPerformanceErrorCode(string? remoteMessage)
    {
        if (string.IsNullOrWhiteSpace(remoteMessage))
        {
            return null;
        }

        foreach (var code in PublicPerformanceErrorCodes)
        {
            if (string.Equals(remoteMessage, code, StringComparison.Ordinal) ||
                remoteMessage.StartsWith(code + ":", StringComparison.Ordinal))
            {
                return code;
            }
        }

        return null;
    }

    public async Task<bool> RefreshWpfBackendCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
        return client is not null;
    }

    public async Task<WpfStateObservation> ObserveStateStartAsync(
        ObserveStateStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = BeginTraceSpan("observe_state_start");
        try
        {
            var target = PrepareWpfAgentTarget(
                "subscribe_property_changes",
                request.Locator,
                request.ElementId,
                request.WindowHandle);
            var processId = EnsureAttached().ProcessId;
            var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
            var effectiveRequest = request with
            {
                WindowHandle = target.WindowHandle,
                Locator = target.Locator,
                ElementId = target.AgentElementId
            };
            var fallbackRequest = target.RecoveryLocator is null
                ? null
                : effectiveRequest with { Locator = target.RecoveryLocator, ElementId = null };
            var response = await CallWpfAgentTargetAsync<ObserveStateStartResponse>(
                client,
                "wpf/observe_state_start",
                effectiveRequest,
                fallbackRequest,
                target,
                cancellationToken).ConfigureAwait(false);
            response = response with
            {
                Element = await StripAgentElementIdAsync(
                    client,
                    response.Element,
                    target.PublicElementId).ConfigureAwait(false)
            };
            var observation = new WpfStateObservation(client, processId, response);
            trace?.SetSummary(
                $"id={response.ObservationId} watches={response.Watches.Count} durationMs={response.DurationMs}");
            return observation;
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

    public async Task<ObserveStatePollResponse> ObserveStatePollAsync(
        WpfStateObservation observation,
        int maxBatch,
        int maxPayloadChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var client = GetOwningObservationClient(observation);
        try
        {
            return await client.CallAsync<ObserveStatePollResponse>(
                "wpf/observe_state_poll",
                new ObserveStatePollRequest(observation.Started.ObservationId, maxBatch, maxPayloadChars),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!client.IsConnected)
            {
                observation.MarkLost();
            }

            throw;
        }
        catch (Exception ex) when (!client.IsConnected)
        {
            observation.MarkLost();
            throw CreateObservationConnectionLostException(observation, ex);
        }
    }

    public async Task<ObserveStateStopResponse> ObserveStateStopAsync(
        WpfStateObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var client = GetOwningObservationClient(observation);
        try
        {
            var response = await client.CallAsync<ObserveStateStopResponse>(
                "wpf/observe_state_stop",
                new ObserveStateStopRequest(observation.Started.ObservationId),
                cancellationToken).ConfigureAwait(false);
            observation.MarkReleased();
            return response;
        }
        catch (OperationCanceledException)
        {
            if (!client.IsConnected)
            {
                observation.MarkLost();
            }

            throw;
        }
        catch (Exception ex) when (!client.IsConnected)
        {
            observation.MarkLost();
            throw CreateObservationConnectionLostException(observation, ex);
        }
    }

    public async Task ReleaseObserveStateAsync(
        WpfStateObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.IsReleased || observation.IsLost)
        {
            return;
        }

        try
        {
            _ = await ObserveStateStopAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!observation.Client.IsConnected)
        {
            observation.MarkLost();
        }
        catch (InvalidOperationException ex) when (IsObservationNotFound(ex))
        {
            observation.MarkReleased();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("observe_state_connection_lost:", StringComparison.Ordinal))
        {
            observation.MarkLost();
        }
    }

    private AgentClient GetOwningObservationClient(WpfStateObservation observation)
    {
        if (observation.IsReleased)
        {
            throw new InvalidOperationException(
                $"observe_state_released: observation '{observation.Started.ObservationId}' has been released.");
        }

        lock (_agentSync)
        {
            if (observation.IsLost ||
                !ReferenceEquals(_agentClient, observation.Client) ||
                _agentPid != observation.ProcessId ||
                !observation.Client.IsConnected)
            {
                observation.MarkLost();
                throw CreateObservationConnectionLostException(observation);
            }

            return observation.Client;
        }
    }

    private static bool IsObservationNotFound(Exception ex) =>
        GetInternalFailureMessage(ex).Contains("observe_state_not_found", StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException CreateObservationConnectionLostException(
        WpfStateObservation observation,
        Exception? inner = null) =>
        new(
            $"observe_state_connection_lost: the agent connection that owns observation " +
            $"'{observation.Started.ObservationId}' is no longer available.",
            inner);

    public async Task<GetBindingInfoResponse> GetBindingInfoAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        bool includeUnbound = false,
        int maxProperties = 2000,
        string valueFormat = "string",
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_binding_info");
        try
        {
        var target = PrepareWpfAgentTarget("get_binding_info", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        EnsureInspectionResponseMetadataCapability(GetAgentCapabilities(client));
        var request = new GetBindingInfoRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            IncludeUnbound: includeUnbound,
            MaxProperties: maxProperties,
            ValueFormat: valueFormat);

        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallWpfAgentTargetAsync<GetBindingInfoResponse>(
            client,
            "wpf/get_binding_info",
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with
        {
            Element = await StripAgentElementIdAsync(
                client,
                response.Element,
                target.PublicElementId).ConfigureAwait(false),
            WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
        };
        trace?.SetSummary(
            $"bindings={response.ReturnedBindings}/{response.DiscoveredBindings} " +
            $"properties={response.ScannedProperties} complete={response.ScanComplete} truncated={response.Truncated}");
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

    public async Task<GetBindingErrorsResponse> GetBindingErrorsAsync(
        long? windowHandle = null,
        string? rootXPath = null,
        int depth = 6,
        int maxErrors = 200,
        int maxNodes = 2000,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_binding_errors");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            EnsureInspectionResponseMetadataCapability(GetAgentCapabilities(client));
            var request = new GetBindingErrorsRequest(
                WindowHandle: windowHandle,
                RootXPath: rootXPath,
                Depth: depth,
                MaxErrors: maxErrors,
                MaxNodes: maxNodes);

            var response = await client.CallAsync<GetBindingErrorsResponse>("wpf/get_binding_errors", request, cancellationToken);
            response = response with
            {
                WindowHandleUsed = windowHandle ?? response.WindowHandleUsed
            };
            trace?.SetSummary(
                $"errors={response.ReturnedErrors}/{response.DiscoveredErrors} " +
                $"nodes={response.ScannedNodes} complete={response.ScanComplete} truncated={response.Truncated}");
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

    public async Task<GetValidationErrorsResponse> GetValidationErrorsAsync(
        long? windowHandle = null,
        string? rootXPath = null,
        int depth = 6,
        bool visibleOnly = false,
        int maxErrors = 100,
        int maxNodes = 2000,
        int maxValueLength = 500,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_validation_errors");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            var request = new GetValidationErrorsRequest(
                WindowHandle: windowHandle,
                RootXPath: rootXPath,
                Depth: depth,
                VisibleOnly: visibleOnly,
                MaxErrors: maxErrors,
                MaxNodes: maxNodes,
                MaxValueLength: maxValueLength);

            var response = await CallGetValidationErrorsWhenSupportedAsync(
                GetAgentCapabilities(client),
                () => client.CallAsync<GetValidationErrorsResponse>(
                    AgentProtocolCapabilities.GetValidationErrors,
                    request,
                    cancellationToken));
            trace?.SetSummary(
                $"errors={response.ReturnedErrors}/{response.DiscoveredErrors} " +
                $"nodes={response.ScannedNodes} complete={response.ScanComplete} truncated={response.Truncated}");
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

    public async Task<GetUiaCoverageReportResponse> GetUiaCoverageReportAsync(
        long? windowHandle = null,
        string? rootXPath = null,
        bool visibleOnly = true,
        bool includeOffViewport = false,
        bool interactiveOnly = true,
        InteractiveMode interactiveMode = InteractiveMode.Heuristic,
        int maxNodes = 5000,
        int maxFindings = 200,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("uia_coverage_report");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            EnsureInspectionResponseMetadataCapability(GetAgentCapabilities(client));
            var request = new GetUiaCoverageReportRequest(
                WindowHandle: windowHandle,
                RootXPath: rootXPath,
                VisibleOnly: visibleOnly,
                IncludeOffViewport: includeOffViewport,
                InteractiveOnly: interactiveOnly,
                InteractiveMode: interactiveMode,
                MaxNodes: maxNodes,
                MaxFindings: maxFindings);

            var response = await client.CallAsync<GetUiaCoverageReportResponse>("wpf/uia_coverage_report", request, cancellationToken);
            response = response with
            {
                Findings = response.Findings
                    .Select(f => f with { Element = StripAgentElementId(f.Element) })
                    .ToArray(),
                WindowHandleUsed = windowHandle ?? response.WindowHandleUsed
            };
            trace?.SetSummary(
                $"findings={response.Summary.ReturnedFindings}/{response.Summary.DiscoveredFindings} " +
                $"nodes={response.Summary.ScannedNodes} complete={response.Summary.ScanComplete} " +
                $"truncated={response.Summary.Truncated}");
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

    public async Task<GetDataContextResponse> GetDataContextAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        DataContextMode mode = DataContextMode.Summary,
        int maxDepth = 2,
        int maxPropertiesPerObject = 50,
        int maxStringLength = 2000,
        bool includeNulls = false,
        bool includeFrameworkProperties = false,
        IReadOnlyList<string>? propertyAllowList = null,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_data_context");
        try
        {
        var target = PrepareWpfAgentTarget("get_data_context", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        EnsureInspectionResponseMetadataCapability(GetAgentCapabilities(client));
        var request = new GetDataContextRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            Mode: mode,
            MaxDepth: maxDepth,
            MaxPropertiesPerObject: maxPropertiesPerObject,
            MaxStringLength: maxStringLength,
            IncludeNulls: includeNulls,
            IncludeFrameworkProperties: includeFrameworkProperties,
            PropertyAllowList: propertyAllowList);

        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallWpfAgentTargetAsync<GetDataContextResponse>(
            client,
            "wpf/get_data_context",
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with
        {
            Element = await StripAgentElementIdAsync(
                client,
                response.Element,
                target.PublicElementId).ConfigureAwait(false),
            WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
        };
        trace?.SetSummary($"type={response.DataContextType ?? "null"}");
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

    public async Task<GetComputedPropertiesResponse> GetComputedPropertiesAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        IReadOnlyList<string>? propertyNames = null,
        bool includeSources = true,
        bool includeDefault = false,
        bool includeUnset = false,
        int maxProperties = 500,
        string valueFormat = "string",
        bool includeProvenance = false,
        int maxProvenanceCandidates = 20,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_computed_properties");
        try
        {
        var target = PrepareWpfAgentTarget("get_computed_properties", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var capabilities = GetAgentCapabilities(client);
        EnsureInspectionResponseMetadataCapability(capabilities);
        var preparedPropertyNames = includeProvenance
            ? PrepareProvenancePropertyNamesForAgent(propertyNames)
            : new PreparedAgentPropertyNames(propertyNames, null);
        var request = new GetComputedPropertiesRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            PropertyNames: preparedPropertyNames.Names,
            IncludeSources: includeSources,
            IncludeDefault: includeDefault,
            IncludeUnset: includeUnset,
            MaxProperties: maxProperties,
            ValueFormat: valueFormat,
            IncludeProvenance: includeProvenance,
            MaxProvenanceCandidates: maxProvenanceCandidates);

        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallGetComputedPropertiesWhenSupportedAsync(
            includeProvenance,
            capabilities,
            () => CallWpfAgentTargetAsync<GetComputedPropertiesResponse>(
                client,
                "wpf/get_computed_properties",
                request,
                fallbackRequest,
                target,
                cancellationToken));
        if (preparedPropertyNames.TruncatedReasons is not null)
        {
            var truncatedReasons = new List<string>(preparedPropertyNames.TruncatedReasons);
            if (response.TruncatedReasons is not null)
            {
                truncatedReasons.AddRange(response.TruncatedReasons.Where(
                    reason => !truncatedReasons.Contains(reason, StringComparer.Ordinal)));
            }
            else if (response.TruncatedReason is not null &&
                     !truncatedReasons.Contains(response.TruncatedReason, StringComparer.Ordinal))
            {
                truncatedReasons.Add(response.TruncatedReason);
            }

            response = response with
            {
                Truncated = true,
                TruncatedReason = truncatedReasons[0],
                TruncatedReasons = truncatedReasons,
                ScanComplete = false
            };
        }

        response = response with
        {
            Element = await StripAgentElementIdAsync(
                client,
                response.Element,
                target.PublicElementId).ConfigureAwait(false),
            WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
        };
        trace?.SetSummary(
            $"props={response.ReturnedProperties}/{response.DiscoveredProperties} " +
            $"scanned={response.ScannedProperties} complete={response.ScanComplete} truncated={response.Truncated}");
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

    internal readonly record struct PreparedAgentPropertyNames(
        IReadOnlyList<string>? Names,
        IReadOnlyList<string>? TruncatedReasons)
    {
        public string? TruncatedReason => TruncatedReasons?.FirstOrDefault();
    }

    internal static PreparedAgentPropertyNames
        PrepareProvenancePropertyNamesForAgent(IReadOnlyList<string>? propertyNames)
    {
        if (propertyNames is null)
        {
            return new PreparedAgentPropertyNames(null, null);
        }

        const int maxPropertyNames = 100;
        const int maxPropertyNameLength = 512;
        var count = Math.Min(propertyNames.Count, maxPropertyNames);
        var names = new List<string>(count);
        var propertyNameLengthTruncated = false;
        for (var i = 0; i < count; i++)
        {
            var rawName = propertyNames[i] ?? string.Empty;
            propertyNameLengthTruncated |= rawName.Length > maxPropertyNameLength;
            var boundedName = TruncateAgentRequestText(rawName, maxPropertyNameLength).Trim();
            if (boundedName.Length > 0)
            {
                names.Add(boundedName);
            }
        }

        var truncatedReasons = new List<string>(2);
        if (propertyNames.Count > maxPropertyNames)
        {
            truncatedReasons.Add("maxProvenancePropertyNames");
        }

        if (propertyNameLengthTruncated)
        {
            truncatedReasons.Add("maxProvenancePropertyNameLength");
        }

        return new PreparedAgentPropertyNames(
            names,
            truncatedReasons.Count > 0 ? truncatedReasons : null);
    }

    private static string TruncateAgentRequestText(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength - 3;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length] + "...";
    }

    public async Task<GetLayoutContextResponse> GetLayoutContextAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        int maxAncestors = 6,
        int maxSiblings = 8,
        int maxGridDefinitions = 32,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_layout_context");
        try
        {
        var target = PrepareWpfAgentTarget("get_layout_context", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var capabilities = GetAgentCapabilities(client);

        var request = new GetLayoutContextRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            MaxAncestors: maxAncestors,
            MaxSiblings: maxSiblings,
            MaxGridDefinitions: maxGridDefinitions);
        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallGetLayoutContextWhenSupportedAsync(
            capabilities,
            () => CallWpfAgentTargetAsync<GetLayoutContextResponse>(
                client,
                AgentProtocolCapabilities.GetLayoutContext,
                request,
                fallbackRequest,
                target,
                cancellationToken));
        var normalizedElement = await StripAgentElementIdAsync(
            client,
            response.Element,
            target.PublicElementId).ConfigureAwait(false);
        if (target.PublicElementId is not null)
        {
            normalizedElement = normalizedElement with { ElementId = target.PublicElementId };
        }

        response = response with
        {
            Element = normalizedElement,
            WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
        };
        trace?.SetSummary(
            $"ancestors={response.Counts.ReturnedAncestors}/{response.Counts.DiscoveredAncestors} " +
            $"siblings={response.Counts.ReturnedSiblings}/{response.Counts.DiscoveredSiblings} " +
            $"grids={response.Counts.ReturnedGridContexts}/{response.Counts.DiscoveredGridContexts} " +
            $"truncated={response.Truncated}");
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

    public async Task<GetStyleChainResponse> GetStyleChainAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        bool includeThemeStyle = true,
        bool includeResourceKeys = false,
        int maxBasedOnDepth = 10,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_style_chain");
        try
        {
        var target = PrepareWpfAgentTarget("get_style_chain", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        EnsureInspectionResponseMetadataCapability(GetAgentCapabilities(client));
        var request = new GetStyleChainRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            IncludeThemeStyle: includeThemeStyle,
            IncludeResourceKeys: includeResourceKeys,
            MaxBasedOnDepth: maxBasedOnDepth);

        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallWpfAgentTargetAsync<GetStyleChainResponse>(
            client,
            "wpf/get_style_chain",
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with
        {
            Element = await StripAgentElementIdAsync(
                client,
                response.Element,
                target.PublicElementId).ConfigureAwait(false),
            WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
        };
        trace?.SetSummary($"entries={response.Styles.Count}");
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

    public async Task<GetTemplateInfoResponse> GetTemplateInfoAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        bool includeNamedElements = false,
        int maxNamedElements = 50,
        bool includeResourceKeys = false,
        bool includePartElementRefs = false,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_template_info");
        try
        {
        var target = PrepareWpfAgentTarget("get_template_info", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        EnsureInspectionResponseMetadataCapability(GetAgentCapabilities(client));
        var request = new GetTemplateInfoRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            IncludeNamedElements: includeNamedElements,
            MaxNamedElements: maxNamedElements,
            IncludeResourceKeys: includeResourceKeys,
            IncludePartElementRefs: includePartElementRefs);

        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallWpfAgentTargetAsync<GetTemplateInfoResponse>(
            client,
            "wpf/get_template_info",
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with
        {
            Element = await StripAgentElementIdAsync(
                client,
                response.Element,
                target.PublicElementId).ConfigureAwait(false),
            WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
        };
        var named = response.Template.NamedElements is null ? 0 : response.Template.NamedElements.Count;
        trace?.SetSummary($"named={named}");
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

    internal async Task<GetVisualTreeResponse> GetVisualTreeWpfAsync(
        GetWpfVisualTreeRequestV2 request,
        bool injectIfMissing,
        CancellationToken cancellationToken)
    {
        var client = injectIfMissing
            ? await EnsureAgentConnectedAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);

        if (client is null)
        {
            throw new InvalidOperationException("WPF agent is not connected.");
        }

        return await client.CallAsync<GetVisualTreeResponse>("wpf/get_visual_tree", request, cancellationToken);
    }

    internal async Task<(GetVisualTreeResponse? Response, bool Attempted, FailureInfo? Failure)> TryGetVisualTreeWpfAsync(
        GetWpfVisualTreeRequestV2 request,
        CancellationToken cancellationToken,
        bool autoInject = false)
    {
        var attemptSequence = GetAutoAgentAttemptSequence();
        var client = autoInject
            ? await EnsureAgentConnectedForAutoAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);
        if (client is null)
        {
            return (
                null,
                autoInject && GetAutoAgentAttemptSequence() != attemptSequence,
                GetWpfBackendFailure());
        }

        try
        {
            var response = await client.CallAsync<GetVisualTreeResponse>(
                "wpf/get_visual_tree",
                request,
                cancellationToken);
            return (response, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsPerWindowAutoWpfMiss(ex))
            {
                throw;
            }

            var failure = CreateAutoWpfFallbackFailure(ex);
            failure = PreferTargetStateFailure(failure);
            if (autoInject && ShouldRecordAutoAgentFailure(ex, client.IsConnected))
            {
                SetAutoAgentFailure(failure);
            }

            return (null, true, failure);
        }
    }

    internal async Task<FindElementsResponse> FindElementsWpfAsync(
        FindElementsWpfRequest request,
        bool injectIfMissing,
        CancellationToken cancellationToken)
    {
        var client = injectIfMissing
            ? await EnsureAgentConnectedAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);

        if (client is null)
        {
            throw new InvalidOperationException("WPF agent is not connected.");
        }

        return await CallFindElementsWpfAsync(client, request, cancellationToken);
    }

    internal async Task<(FindElementsResponse? Response, bool Attempted, FailureInfo? Failure)> TryFindElementsWpfAsync(
        FindElementsWpfRequest request,
        CancellationToken cancellationToken,
        bool autoInject = false)
    {
        var attemptSequence = GetAutoAgentAttemptSequence();
        var client = autoInject
            ? await EnsureAgentConnectedForAutoAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);
        if (client is null)
        {
            return (
                null,
                autoInject && GetAutoAgentAttemptSequence() != attemptSequence,
                GetWpfBackendFailure());
        }

        try
        {
            var response = await CallFindElementsWpfAsync(client, request, cancellationToken);
            return (response, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsPerWindowAutoWpfMiss(ex))
            {
                throw;
            }

            var failure = CreateAutoWpfFallbackFailure(ex);
            failure = PreferTargetStateFailure(failure);
            if (autoInject && ShouldRecordAutoAgentFailure(ex, client.IsConnected))
            {
                SetAutoAgentFailure(failure);
            }

            return (null, true, failure);
        }
    }

    private async Task<FindElementsResponse> CallFindElementsWpfAsync(
        AgentClient client,
        FindElementsWpfRequest request,
        CancellationToken cancellationToken)
    {
        if (AgentSupportsCapability(client, AgentProtocolCapabilities.FindElementsDiscoveryCounts))
        {
            return await client.CallAsync<FindElementsResponse>("wpf/find_elements", request, cancellationToken);
        }

        var requestedMaxResults = Math.Clamp(request.MaxResults, 1, 5000);
        var legacyRequest = request with
        {
            MaxResults = Math.Min(requestedMaxResults + 1, 5000)
        };
        var legacy = await client.CallAsync<FindElementsResponse>(
            "wpf/find_elements",
            legacyRequest,
            cancellationToken);

        return NormalizeLegacyFindElementsResponse(legacy, requestedMaxResults);
    }

    internal static FindElementsResponse NormalizeLegacyFindElementsResponse(
        FindElementsResponse legacy,
        int requestedMaxResults)
    {
        ArgumentNullException.ThrowIfNull(legacy);

        requestedMaxResults = Math.Clamp(requestedMaxResults, 1, 5000);
        var discoveredMatches = legacy.Matches.Count;
        var exceededRequestedLimit = discoveredMatches > requestedMaxResults;
        var matches = exceededRequestedLimit
            ? legacy.Matches.Take(requestedMaxResults).ToArray()
            : legacy.Matches;
        var truncated = exceededRequestedLimit || legacy.Truncated;
        var truncatedReason = !string.IsNullOrWhiteSpace(legacy.TruncatedReason)
            ? legacy.TruncatedReason
            : exceededRequestedLimit
                ? "maxResults"
                : null;

        return legacy with
        {
            Matches = matches,
            ReturnedMatches = matches.Count,
            DiscoveredMatches = discoveredMatches,
            Truncated = truncated,
            TruncatedReason = truncatedReason
        };
    }

    internal async Task<GetPathToElementResponse> GetWpfPathAsync(
        GetWpfPathRequest request,
        bool injectIfMissing,
        CancellationToken cancellationToken)
    {
        var client = injectIfMissing
            ? await EnsureAgentConnectedAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);

        if (client is null)
        {
            throw new InvalidOperationException("WPF agent is not connected.");
        }

        return await client.CallAsync<GetPathToElementResponse>("wpf/get_path", request, cancellationToken);
    }

    internal static async Task<AgentCapabilitiesResponse> VerifyAgentAndGetCapabilitiesAsync(
        AgentClient client,
        CancellationToken cancellationToken)
    {
        var pong = await client.CallAsync<string>("ping", @params: null, cancellationToken);
        if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected agent ping response '{pong}'.");
        }

        try
        {
            var capabilities = await client.CallAsync<AgentCapabilitiesResponse>(
                AgentProtocolCapabilities.GetCapabilitiesMethod,
                @params: null,
                cancellationToken);
            if (capabilities.ProtocolVersion <= 0 || capabilities.Capabilities is null)
            {
                throw new InvalidOperationException("Agent returned an invalid capabilities response.");
            }

            if (capabilities.ProtocolVersion != AgentProtocolCapabilities.CurrentProtocolVersion)
            {
                throw new ActionableFailureException(FailureDiagnostics.ProtocolMismatch());
            }

            return capabilities;
        }
        catch (InvalidOperationException ex) when (IsUnknownCapabilitiesMethod(ex))
        {
            return new AgentCapabilitiesResponse(ProtocolVersion: 0, Capabilities: []);
        }
    }

    internal async Task<AgentCapabilitiesResponse> VerifyAgentForAttachedProcessAsync(
        AgentClient client,
        int expectedPid,
        CancellationToken cancellationToken)
    {
        var capabilities = await VerifyAgentAndGetCapabilitiesAsync(client, cancellationToken)
            .ConfigureAwait(false);
        _ = EnsureAttachedProcessIdentityCurrent(expectedPid);
        return capabilities;
    }

    private static bool IsUnknownCapabilitiesMethod(InvalidOperationException exception) =>
        string.Equals(
            exception.Message,
            $"Unknown method '{AgentProtocolCapabilities.GetCapabilitiesMethod}'.",
            StringComparison.Ordinal);

    internal static InvalidOperationException CreateGetLayoutContextCapabilityException() =>
        new(
            "agent_capability_unavailable: get_layout_context requires the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal static InvalidOperationException CreateInspectionResponseMetadataCapabilityException() =>
        new(
            "agent_capability_unavailable: truthful inspection response metadata requires the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal static void EnsureInspectionResponseMetadataCapability(AgentCapabilitiesResponse? capabilities)
    {
        if (capabilities is null ||
            !capabilities.Capabilities.Contains(
                AgentProtocolCapabilities.InspectionResponseMetadata,
                StringComparer.Ordinal))
        {
            throw CreateInspectionResponseMetadataCapabilityException();
        }
    }

    internal static Task<T> CallGetLayoutContextWhenSupportedAsync<T>(
        AgentCapabilitiesResponse? capabilities,
        Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return capabilities is not null &&
               capabilities.Capabilities.Contains(AgentProtocolCapabilities.GetLayoutContext, StringComparer.Ordinal)
            ? call()
            : Task.FromException<T>(CreateGetLayoutContextCapabilityException());
    }

    internal static InvalidOperationException CreateComputedPropertyProvenanceCapabilityException() =>
        new(
            "agent_capability_unavailable: get_computed_properties with includeProvenance=true requires the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal static InvalidOperationException CreateGetValidationErrorsCapabilityException() =>
        new(
            "agent_capability_unavailable: get_validation_errors requires the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal static Task<T> CallGetValidationErrorsWhenSupportedAsync<T>(
        AgentCapabilitiesResponse? capabilities,
        Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return capabilities is not null &&
               capabilities.Capabilities.Contains(
                   AgentProtocolCapabilities.GetValidationErrors,
                   StringComparer.Ordinal)
            ? call()
            : Task.FromException<T>(CreateGetValidationErrorsCapabilityException());
    }

    internal static InvalidOperationException CreateObserveStateCapabilityException() =>
        new(
            "agent_capability_unavailable: typed WPF waits require the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal void EnsureObserveStateCapability(AgentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        EnsureObserveStateCapability(GetAgentCapabilities(client));
    }

    internal static void EnsureObserveStateCapability(AgentCapabilitiesResponse? capabilities)
    {
        if (capabilities is null ||
            !capabilities.Capabilities.Contains(AgentProtocolCapabilities.ObserveState, StringComparer.Ordinal))
        {
            throw CreateObserveStateCapabilityException();
        }
    }

    internal static Task<T> CallGetComputedPropertiesWhenSupportedAsync<T>(
        bool includeProvenance,
        AgentCapabilitiesResponse? capabilities,
        Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return !includeProvenance ||
               capabilities is not null &&
               capabilities.Capabilities.Contains(
                   AgentProtocolCapabilities.GetComputedPropertyProvenance,
                   StringComparer.Ordinal)
            ? call()
            : Task.FromException<T>(CreateComputedPropertyProvenanceCapabilityException());
    }

    private AgentCapabilitiesResponse? GetAgentCapabilities(AgentClient client)
    {
        lock (_agentSync)
        {
            return ReferenceEquals(client, _agentClient) ? _agentCapabilities : null;
        }
    }

    private bool AgentSupportsCapability(AgentClient client, string capability)
    {
        var capabilities = GetAgentCapabilities(client);
        return capabilities is not null &&
               capabilities.Capabilities.Contains(capability, StringComparer.Ordinal);
    }

    private async Task<AgentClient?> EnsureAgentConnectedOrNullAsync(CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var pid = application.ProcessId;

        AgentClient? client;
        int? existingPid;
        lock (_agentSync)
        {
            client = _agentClient;
            existingPid = _agentPid;
        }

        if (client is not null && client.IsConnected && existingPid == pid)
        {
            try
            {
                var capabilities = await VerifyAgentForAttachedProcessAsync(
                        client,
                        pid,
                        cancellationToken)
                    .ConfigureAwait(false);
                lock (_agentSync)
                {
                    if (ReferenceEquals(_agentClient, client))
                    {
                        _agentCapabilities = capabilities;
                    }
                }

                ClearAutoAgentFailure();
                return client;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetAutoAgentFailure(
                    FailureDiagnostics.CreateException(ex, FailureDiagnostics.Stages.Protocol));
                await CleanupAgentAsync(clearFailure: false).ConfigureAwait(false);
                return null;
            }
        }

        if (client is not null)
        {
            bool preserveFailure;
            lock (_agentSync)
            {
                preserveFailure = _agentAutoConnectFailure is not null;
            }

            await CleanupAgentAsync(clearFailure: !preserveFailure).ConfigureAwait(false);
        }

        var processIdentity = EnsureAttachedProcessIdentityCurrent(pid);
        var pipeName = AgentPipeName.Compute(processIdentity);

        // Try quick reconnect to an already-injected agent (do not inject here).
        try
        {
            var connectClient = await AgentClient.ConnectAsync(
                pipeName,
                timeout: TimeSpan.FromMilliseconds(250),
                cancellationToken);

            AgentCapabilitiesResponse capabilities;
            try
            {
                capabilities = await VerifyAgentForAttachedProcessAsync(
                    connectClient,
                    pid,
                    cancellationToken);
                lock (_agentSync)
                {
                    _agentClient = connectClient;
                    _agentPipeName = pipeName;
                    _agentPid = pid;
                    _agentCapabilities = capabilities;
                }
            }
            catch (OperationCanceledException)
            {
                await connectClient.DisposeAsync();
                throw;
            }
            catch (Exception ex)
            {
                await connectClient.DisposeAsync();
                SetAutoAgentFailure(
                    FailureDiagnostics.CreateException(ex, FailureDiagnostics.Stages.Protocol));
                return null;
            }

            ClearAutoAgentFailure();
            return connectClient;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<AgentClient> EnsureAgentConnectedAsync(CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var pid = application.ProcessId;

        AgentClient? client;
        lock (_agentSync)
        {
            client = _agentClient;
            if (client is not null && client.IsConnected && _agentPid == pid)
            {
                return client;
            }
        }

        _ = await InjectAgentAsync(cancellationToken);

        lock (_agentSync)
        {
            client = _agentClient;
        }

        return client ?? throw new InvalidOperationException("Agent injection succeeded, but the pipe client was not initialized.");
    }

    private async Task<AgentClient?> EnsureAgentConnectedForAutoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var application = EnsureAttached();
        var pid = application.ProcessId;

        lock (_agentSync)
        {
            if (_agentAutoConnectFailure is { } failure)
            {
                if (!ShouldRetryAutoAgentConnection(
                        failure,
                        _agentAutoConnectFailureAtUtc,
                        DateTimeOffset.UtcNow))
                {
                    return null;
                }

                _agentAutoConnectFailure = null;
                _agentAutoConnectFailureAtUtc = null;
            }
        }

        _ = Interlocked.Increment(ref _agentAutoConnectAttemptSequence);
        var existing = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ClearAutoAgentFailure();
            return existing;
        }

        lock (_agentSync)
        {
            if (_agentAutoConnectFailure is { } passiveFailure &&
                !ShouldRetryAutoAgentConnection(
                    passiveFailure,
                    _agentAutoConnectFailureAtUtc,
                    DateTimeOffset.UtcNow))
            {
                return null;
            }
        }

        try
        {
            _ = await InjectAgentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetAutoAgentFailure(ex);
            return null;
        }

        lock (_agentSync)
        {
            if (_agentClient is not null && _agentClient.IsConnected && _agentPid == pid)
            {
                return _agentClient;
            }
        }

        SetAutoAgentFailure(
            FailureDiagnostics.Exception(
                FailureDiagnostics.Codes.AgentConnectionFailed,
                FailureDiagnostics.Stages.PipeConnection,
                "The WPF backend initialized but its pipe connection is unavailable.",
                retryable: true,
                recoveryActions: [FailureDiagnostics.Recovery.UseUia, FailureDiagnostics.Recovery.Retry],
                retryAfterMs: 1_000));
        return null;
    }

    internal static bool ShouldRetryAutoAgentConnection(
        FailureInfo failure,
        DateTimeOffset? recordedAtUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Retryable is not true)
        {
            return false;
        }

        return recordedAtUtc is null ||
               nowUtc - recordedAtUtc.Value >= GetAutoAgentFailureRetryDelay(failure);
    }

    private long GetAutoAgentAttemptSequence() =>
        Interlocked.Read(ref _agentAutoConnectAttemptSequence);

    private static TimeSpan GetAutoAgentFailureRetryDelay(FailureInfo failure) =>
        failure.RetryAfterMs is >= 0 and var retryAfterMs
            ? TimeSpan.FromMilliseconds(retryAfterMs)
            : AutoAgentFailureRetryDelay;

    private string GetAutoAgentFallbackWarning(FailureInfo? failure = null)
    {
        if (failure is null)
        {
            failure = GetWpfBackendFailure();
        }

        return failure is null
            ? "backend=auto: WPF agent not connected; used UIA."
            : $"backend=auto: WPF backend unavailable ({failure.Code} at {failure.Stage}); used UIA.";
    }

    private void ClearAutoAgentFailure()
    {
        lock (_agentSync)
        {
            _agentAutoConnectFailure = null;
            _agentAutoConnectFailureAtUtc = null;
        }
    }

    internal void SetAutoAgentFailure(
        Exception ex,
        string stage = FailureDiagnostics.Stages.Injection)
    {
        var failure = FailureDiagnostics.Classify(ex, stage);
        failure = PreferTargetStateFailure(failure);
        lock (_agentSync)
        {
            _agentAutoConnectFailure = failure;
            _agentAutoConnectFailureAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetAutoAgentFailure(FailureInfo failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        failure = PreferTargetStateFailure(failure);
        lock (_agentSync)
        {
            _agentAutoConnectFailure = failure;
            _agentAutoConnectFailureAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private FailureInfo PreferTargetStateFailure(FailureInfo fallback)
    {
        var identity = _processIdentity;
        return identity is not null &&
               ProcessTargetResolver.Observe(identity.Value) == ProcessInstanceState.ExitedOrReused
            ? FailureDiagnostics.TargetExited() with { Cause = fallback.Cause }
            : fallback;
    }

    private static async Task<AgentClient> ConnectToAgentWithRetryAsync(string pipeName, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(3);

        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await AgentClient.ConnectAsync(pipeName, timeout: TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch
            {
                await Task.Delay(75, cancellationToken);
            }
        }

        return await AgentClient.ConnectAsync(pipeName, timeout: TimeSpan.FromSeconds(1), cancellationToken);
    }

    private static async Task<AgentClient?> TryConnectToAgentWithRetryAsync(
        string pipeName,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        if (totalTimeout <= TimeSpan.Zero)
        {
            totalTimeout = TimeSpan.FromSeconds(1);
        }

        var start = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(start) < totalTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await AgentClient.ConnectAsync(pipeName, timeout: TimeSpan.FromMilliseconds(350), cancellationToken);
            }
            catch
            {
                await Task.Delay(75, cancellationToken);
            }
        }

        return null;
    }

    internal static ActionableFailureException CreateInjectorFailureException(
        InjectionRunResult result,
        ProcessIntegrityLevelComparison? integrityComparison = null)
    {
        var diagnosticCause = new InvalidOperationException(BuildInjectorFailureDetails(result));
        var failure = FailureDiagnostics.WithDiagnosticCause(
            FailureDiagnostics.ClassifyInjectorFailure(result, integrityComparison),
            diagnosticCause);
        return new ActionableFailureException(failure, diagnosticCause);
    }

    internal static string BuildInjectorFailureDetails(InjectionRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        var executablePath = string.IsNullOrWhiteSpace(result.ExecutablePath)
            ? "<unknown>"
            : result.ExecutablePath;
        var processId = result.ProcessId > 0
            ? result.ProcessId.ToString(CultureInfo.InvariantCulture)
            : "<unknown>";

        sb.AppendLine();
        sb.Append("Launcher: '").Append(executablePath).AppendLine("'");
        sb.Append("Process: PID ")
            .Append(processId)
            .Append("; duration=")
            .Append(Math.Max(0, result.Duration.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture))
            .AppendLine(" ms");
        sb.Append("Exit: ").AppendLine(FormatInjectorExitCode(result.ExitCode));

        AppendInjectorOutput(sb, "stdout", result.Stdout);
        AppendInjectorOutput(sb, "stderr", result.Stderr);

        return sb.ToString().TrimEnd();
    }

    private static string FormatInjectorExitCode(int exitCode)
    {
        const uint unhandledClrException = 0xE0434352;
        var unsignedExitCode = unchecked((uint)exitCode);
        var description = unsignedExitCode == unhandledClrException
            ? ", unhandled CLR exception"
            : "";
        return $"exit code {exitCode.ToString(CultureInfo.InvariantCulture)} " +
               $"(0x{unsignedExitCode.ToString("X8", CultureInfo.InvariantCulture)}{description})";
    }

    private static void AppendInjectorOutput(StringBuilder builder, string name, string value)
    {
        builder.Append("--- ").Append(name).AppendLine(" ---");
        builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "<empty>" : value.TrimEnd());
    }

    internal static T SelectInitialInjectionWindow<T>(
        long? initialWindowHandle,
        Func<long, T> findByHandle,
        Func<T> findMainWindow)
    {
        ArgumentNullException.ThrowIfNull(findByHandle);
        ArgumentNullException.ThrowIfNull(findMainWindow);

        return initialWindowHandle is long requestedWindowHandle
            ? findByHandle(requestedWindowHandle)
            : findMainWindow();
    }

    private async Task CleanupAgentAsync(bool clearFailure = true)
    {
        var client = DetachAgentClient(clearFailure);
        if (client is not null)
        {
            await DisposeAgentClientAsync(client).ConfigureAwait(false);
        }
    }

    private void QueueAgentCleanup(bool clearFailure = true)
    {
        var client = DetachAgentClient(clearFailure);
        if (client is null)
        {
            return;
        }

        var disposal = DisposeAgentClientAsync(client);
        lock (_agentDisposalSync)
        {
            _pendingAgentDisposals.Add(disposal);
        }

        _ = RemoveCompletedAgentDisposalAsync(disposal);
    }

    private async Task AwaitPendingAgentDisposalsAsync()
    {
        Task[] disposals;
        lock (_agentDisposalSync)
        {
            disposals = [.. _pendingAgentDisposals];
        }

        await Task.WhenAll(disposals).ConfigureAwait(false);
    }

    private AgentClient? DetachAgentClient(bool clearFailure)
    {
        AgentClient? client;
        lock (_agentSync)
        {
            client = _agentClient;
            _agentClient = null;
            _agentPipeName = null;
            _agentPid = null;
            _agentCapabilities = null;
            if (clearFailure)
            {
                _agentAutoConnectFailure = null;
                _agentAutoConnectFailureAtUtc = null;
            }
        }

        return client;
    }

    private static async Task DisposeAgentClientAsync(AgentClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }

    private async Task RemoveCompletedAgentDisposalAsync(Task disposal)
    {
        await disposal.ConfigureAwait(false);
        lock (_agentDisposalSync)
        {
            _pendingAgentDisposals.Remove(disposal);
        }
    }
}
