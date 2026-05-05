using System.Diagnostics;
using System.Text;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    private static readonly TimeSpan AutoAgentFailureRetryDelay = TimeSpan.FromSeconds(10);
    private readonly object _agentSync = new();
    private AgentClient? _agentClient;
    private string? _agentPipeName;
    private int? _agentPid;
    private string? _agentAutoConnectFailure;
    private DateTimeOffset? _agentAutoConnectFailureAtUtc;

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

                return string.IsNullOrWhiteSpace(_agentAutoConnectFailure)
                    ? "not_initialized"
                    : "unavailable";
            }
        }
    }

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
        => WpfAgentErrorClassifier.IsStaleOrNotFound(ex);

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
        var lastAgentError = (inner.GetBaseException().Message ?? inner.Message ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];

        return new InvalidOperationException(
            $"stale_element: not_found for '{target.PublicElementId}'.{context} Call resolve_element again. Last agent error: {lastAgentError}");
    }

    private ElementRef StripAgentElementId(ElementRef element, string? publicElementId = null)
    {
        if (!string.IsNullOrWhiteSpace(publicElementId))
        {
            _elementHandles.TryUpdateWpfResolution(publicElementId, element);
        }

        return element with { ElementIdWpf = null };
    }

    public async Task<InjectAgentResponse> InjectAgentAsync(CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("inject_agent");
        try
        {
            var application = EnsureAttached();
            var automation = EnsureAutomation();

        var pid = application.ProcessId;
        using var process = Process.GetProcessById(pid);
        var pipeName = AgentPipeName.Compute(process);

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
            // Ensure the agent is still responsive
            try
            {
                _ = await existingClient.CallAsync<string>(AgentMethods.Ping, @params: null, cancellationToken);
                var response = new InjectAgentResponse(Injected: false, PipeName: existingPipeName);
                ClearAutoAgentFailure();
                trace?.SetSummary($"injected={response.Injected} pipe={response.PipeName}");
                return response;
            }
            catch
            {
                CleanupAgent();
            }
        }

        if (existingClient is not null &&
            (existingPid != pid || !existingClient.IsConnected || existingPipeName is null))
        {
            CleanupAgent();
        }

        // Connect-first: if the app was already injected (e.g. MCP server restarted), we should reconnect without re-injecting.
        var connectFirstClient = await TryConnectToAgentWithRetryAsync(
            pipeName,
            totalTimeout: TimeSpan.FromSeconds(2),
            cancellationToken);

        if (connectFirstClient is not null)
        {
            try
            {
                var pong = await connectFirstClient.CallAsync<string>(AgentMethods.Ping, @params: null, cancellationToken);
                if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unexpected agent ping response '{pong}'.");
                }
            }
            catch
            {
                await connectFirstClient.DisposeAsync();
                throw;
            }

            lock (_agentSync)
            {
                _agentClient = connectFirstClient;
                _agentPipeName = pipeName;
                _agentPid = pid;
            }

            var response = new InjectAgentResponse(Injected: false, PipeName: pipeName);
            ClearAutoAgentFailure();
            trace?.SetSummary($"injected={response.Injected} pipe={response.PipeName}");
            return response;
        }

        var assets = Phase2Assets.ResolveFromAppBase();

        var window = FindMainWindow(application, automation);
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Main window handle is not available.");
        }

        var architecture = ProcessArchitectureDetector.GetProcessArchitecture(process);

        var injectResult = await SnoopInjector.InjectAsync(
            assets,
            targetPid: pid,
            targetHwnd: hwnd.ToInt64(),
            targetArchitecture: architecture,
            pipeName: pipeName,
            cancellationToken: cancellationToken);

        if (injectResult.ExitCode != 0)
        {
            var details = BuildInjectorFailureDetails(injectResult);
            throw new InvalidOperationException($"Snoop injection failed (exit code {injectResult.ExitCode}).{details}");
        }

        var client = await ConnectToAgentWithRetryAsync(pipeName, cancellationToken);
        try
        {
            var pong = await client.CallAsync<string>(AgentMethods.Ping, @params: null, cancellationToken);
            if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected agent ping response '{pong}'.");
            }
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        lock (_agentSync)
        {
            _agentClient = client;
            _agentPipeName = pipeName;
            _agentPid = pid;
        }

        var finalResponse = new InjectAgentResponse(Injected: true, PipeName: pipeName);
        ClearAutoAgentFailure();
        trace?.SetSummary($"injected={finalResponse.Injected} pipe={finalResponse.PipeName}");
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

    public async Task<AgentPingResponse> AgentPingAsync(CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("agent_ping");
        try
        {
            var client = await EnsureAgentConnectedAsync(cancellationToken);
            var pong = await client.CallAsync<string>(AgentMethods.Ping, @params: null, cancellationToken);
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
            var response = await client.CallAsync<PerformanceStartResponse>(AgentMethods.PerformanceStart, request, cancellationToken);
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
            var response = await client.CallAsync<PerformanceStopResponse>(AgentMethods.PerformanceStop, request, cancellationToken);
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

    public async Task<bool> RefreshWpfBackendCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureAgentConnectedForAutoAsync(cancellationToken).ConfigureAwait(false);
        return client is not null;
    }

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
            AgentMethods.GetBindingInfo,
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with { Element = StripAgentElementId(response.Element, target.PublicElementId) };
        trace?.SetSummary($"bindings={response.Bindings.Count} truncated={response.Truncated}");
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
            var request = new GetBindingErrorsRequest(
                WindowHandle: windowHandle,
                RootXPath: rootXPath,
                Depth: depth,
                MaxErrors: maxErrors,
                MaxNodes: maxNodes);

            var response = await client.CallAsync<GetBindingErrorsResponse>(AgentMethods.GetBindingErrors, request, cancellationToken);
            trace?.SetSummary($"errors={response.Errors.Count} truncated={response.Truncated}");
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
            var request = new GetUiaCoverageReportRequest(
                WindowHandle: windowHandle,
                RootXPath: rootXPath,
                VisibleOnly: visibleOnly,
                IncludeOffViewport: includeOffViewport,
                InteractiveOnly: interactiveOnly,
                InteractiveMode: interactiveMode,
                MaxNodes: maxNodes,
                MaxFindings: maxFindings);

            var response = await client.CallAsync<GetUiaCoverageReportResponse>(AgentMethods.GetUiaCoverageReport, request, cancellationToken);
            response = response with
            {
                Findings = response.Findings
                    .Select(f => f with { Element = StripAgentElementId(f.Element) })
                    .ToArray()
            };
            trace?.SetSummary($"findings={response.Summary.FindingsCount} truncated={response.Summary.Truncated}");
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
            AgentMethods.GetDataContext,
            request,
            fallbackRequest,
            target,
            cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_computed_properties");
        try
        {
        var target = PrepareWpfAgentTarget("get_computed_properties", locator, elementId, windowHandle);

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var request = new GetComputedPropertiesRequest(
            WindowHandle: target.WindowHandle,
            Locator: target.Locator,
            ElementId: target.AgentElementId,
            PropertyNames: propertyNames,
            IncludeSources: includeSources,
            IncludeDefault: includeDefault,
            IncludeUnset: includeUnset,
            MaxProperties: maxProperties,
            ValueFormat: valueFormat);

        var fallbackRequest = target.RecoveryLocator is null
            ? null
            : request with { Locator = target.RecoveryLocator, ElementId = null };
        var response = await CallWpfAgentTargetAsync<GetComputedPropertiesResponse>(
            client,
            AgentMethods.GetComputedProperties,
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with { Element = StripAgentElementId(response.Element, target.PublicElementId) };
        trace?.SetSummary($"props={response.Properties.Count} truncated={response.Truncated}");
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
            AgentMethods.GetStyleChain,
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with { Element = StripAgentElementId(response.Element, target.PublicElementId) };
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
            AgentMethods.GetTemplateInfo,
            request,
            fallbackRequest,
            target,
            cancellationToken);
        response = response with { Element = StripAgentElementId(response.Element, target.PublicElementId) };
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

        return await client.CallAsync<GetVisualTreeResponse>(AgentMethods.GetVisualTree, request, cancellationToken);
    }

    internal async Task<GetVisualTreeResponse?> TryGetVisualTreeWpfAsync(
        GetWpfVisualTreeRequestV2 request,
        CancellationToken cancellationToken,
        bool autoInject = false)
    {
        var client = autoInject
            ? await EnsureAgentConnectedForAutoAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);
        if (client is null)
        {
            return null;
        }

        try
        {
            return await client.CallAsync<GetVisualTreeResponse>(AgentMethods.GetVisualTree, request, cancellationToken);
        }
        catch (Exception ex)
        {
            if (autoInject)
            {
                SetAutoAgentFailure(ex);
            }

            return null;
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

        return await client.CallAsync<FindElementsResponse>(AgentMethods.FindElements, request, cancellationToken);
    }

    internal async Task<FindElementsResponse?> TryFindElementsWpfAsync(
        FindElementsWpfRequest request,
        CancellationToken cancellationToken,
        bool autoInject = false)
    {
        var client = autoInject
            ? await EnsureAgentConnectedForAutoAsync(cancellationToken)
            : await EnsureAgentConnectedOrNullAsync(cancellationToken);
        if (client is null)
        {
            return null;
        }

        try
        {
            return await client.CallAsync<FindElementsResponse>(AgentMethods.FindElements, request, cancellationToken);
        }
        catch (Exception ex)
        {
            if (autoInject)
            {
                SetAutoAgentFailure(ex);
            }

            return null;
        }
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

        return await client.CallAsync<GetPathToElementResponse>(AgentMethods.GetPath, request, cancellationToken);
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
            if (client is not null && client.IsConnected && existingPid == pid)
            {
                return client;
            }
        }

        using var process = Process.GetProcessById(pid);
        var pipeName = AgentPipeName.Compute(process);

        // Try quick reconnect to an already-injected agent (do not inject here).
        try
        {
            var connectClient = await AgentClient.ConnectAsync(
                pipeName,
                timeout: TimeSpan.FromMilliseconds(250),
                cancellationToken);

            try
            {
                var pong = await connectClient.CallAsync<string>(AgentMethods.Ping, @params: null, cancellationToken);
                if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
                {
                    await connectClient.DisposeAsync();
                    return null;
                }
            }
            catch
            {
                await connectClient.DisposeAsync();
                return null;
            }

            lock (_agentSync)
            {
                _agentClient = connectClient;
                _agentPipeName = pipeName;
                _agentPid = pid;
            }

            return connectClient;
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
            if (_agentClient is not null && _agentClient.IsConnected && _agentPid == pid)
            {
                return _agentClient;
            }

            if (!string.IsNullOrWhiteSpace(_agentAutoConnectFailure))
            {
                var failureAge = _agentAutoConnectFailureAtUtc is { } recordedAt
                    ? DateTimeOffset.UtcNow - recordedAt
                    : AutoAgentFailureRetryDelay;
                if (failureAge < AutoAgentFailureRetryDelay)
                {
                    return null;
                }

                _agentAutoConnectFailure = null;
                _agentAutoConnectFailureAtUtc = null;
            }
        }

        var existing = await EnsureAgentConnectedOrNullAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ClearAutoAgentFailure();
            return existing;
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
            return _agentClient is not null && _agentClient.IsConnected ? _agentClient : null;
        }
    }

    private string GetAutoAgentFallbackWarning()
    {
        string? failure;
        lock (_agentSync)
        {
            failure = _agentAutoConnectFailure;
        }

        return string.IsNullOrWhiteSpace(failure)
            ? "backend=auto: WPF agent not connected; used UIA."
            : $"backend=auto: WPF auto-injection failed; used UIA. {failure}";
    }

    private void ClearAutoAgentFailure()
    {
        lock (_agentSync)
        {
            _agentAutoConnectFailure = null;
            _agentAutoConnectFailureAtUtc = null;
        }
    }

    private void SetAutoAgentFailure(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        lock (_agentSync)
        {
            _agentAutoConnectFailure = string.IsNullOrWhiteSpace(message)
                ? ex.GetType().Name
                : message.Trim();
            _agentAutoConnectFailureAtUtc = DateTimeOffset.UtcNow;
        }
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

    private static string BuildInjectorFailureDetails(InjectionRunResult result)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            sb.AppendLine();
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(result.Stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            sb.AppendLine();
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(result.Stderr.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private void CleanupAgent()
    {
        AgentClient? client;
        lock (_agentSync)
        {
            client = _agentClient;
            _agentClient = null;
            _agentPipeName = null;
            _agentPid = null;
            _agentAutoConnectFailure = null;
            _agentAutoConnectFailureAtUtc = null;
        }

        if (client is not null)
        {
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort
            }
        }
    }
}
