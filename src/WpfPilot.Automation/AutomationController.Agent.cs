using System.Diagnostics;
using System.Text;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    private readonly object _agentSync = new();
    private AgentClient? _agentClient;
    private string? _agentPipeName;
    private int? _agentPid;

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
                _ = await existingClient.CallAsync<string>("ping", @params: null, cancellationToken);
                var response = new InjectAgentResponse(Injected: false, PipeName: existingPipeName);
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
                var pong = await connectFirstClient.CallAsync<string>("ping", @params: null, cancellationToken);
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
            var pong = await client.CallAsync<string>("ping", @params: null, cancellationToken);
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
            var response = await client.CallAsync<PerformanceStartResponse>("wpf/performance_start", request, cancellationToken);
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
            var response = await client.CallAsync<PerformanceStopResponse>("wpf/performance_stop", request, cancellationToken);
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
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_binding_info requires exactly one of: locator OR elementId.");
        }

        string? resolvedElementId = null;
        long? effectiveWindowHandle = windowHandle;
        ElementLocator effectiveLocator = locator ?? new ElementLocator();

        if (hasElementId)
        {
            var id = elementId!.Trim();
            resolvedElementId = id;
            var handle = RequireHandle(id);
            if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{id}' is not a WPF handle.");
            }

            if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            effectiveWindowHandle = handle.WindowHandle;
            effectiveLocator = new ElementLocator(XPath: handle.XPath);
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var request = new GetBindingInfoRequest(
            WindowHandle: effectiveWindowHandle,
            Locator: effectiveLocator,
            IncludeUnbound: includeUnbound,
            MaxProperties: maxProperties,
            ValueFormat: valueFormat);

        try
        {
            var response = await client.CallAsync<GetBindingInfoResponse>("wpf/get_binding_info", request, cancellationToken);
            trace?.SetSummary($"bindings={response.Bindings.Count} truncated={response.Truncated}");
            return response;
        }
        catch (InvalidOperationException ex) when (hasElementId &&
                                                  resolvedElementId is not null &&
                                                  ex.Message.StartsWith("wpf_resolve:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{resolvedElementId}'. Call resolve_element again.");
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

            var response = await client.CallAsync<GetBindingErrorsResponse>("wpf/get_binding_errors", request, cancellationToken);
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
                InteractiveOnly: interactiveOnly,
                InteractiveMode: interactiveMode,
                MaxNodes: maxNodes,
                MaxFindings: maxFindings);

            var response = await client.CallAsync<GetUiaCoverageReportResponse>("wpf/uia_coverage_report", request, cancellationToken);
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
        int maxDepth = 2,
        int maxPropertiesPerObject = 50,
        int maxStringLength = 2000,
        bool includeNulls = false,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_data_context");
        try
        {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_data_context requires exactly one of: locator OR elementId.");
        }

        string? resolvedElementId = null;
        long? effectiveWindowHandle = windowHandle;
        ElementLocator effectiveLocator = locator ?? new ElementLocator();

        if (hasElementId)
        {
            var id = elementId!.Trim();
            resolvedElementId = id;
            var handle = RequireHandle(id);
            if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{id}' is not a WPF handle.");
            }

            if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            effectiveWindowHandle = handle.WindowHandle;
            effectiveLocator = new ElementLocator(XPath: handle.XPath);
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var request = new GetDataContextRequest(
            WindowHandle: effectiveWindowHandle,
            Locator: effectiveLocator,
            MaxDepth: maxDepth,
            MaxPropertiesPerObject: maxPropertiesPerObject,
            MaxStringLength: maxStringLength,
            IncludeNulls: includeNulls);

        try
        {
            var response = await client.CallAsync<GetDataContextResponse>("wpf/get_data_context", request, cancellationToken);
            trace?.SetSummary($"type={response.DataContextType ?? "null"}");
            return response;
        }
        catch (InvalidOperationException ex) when (hasElementId &&
                                                  resolvedElementId is not null &&
                                                  ex.Message.StartsWith("wpf_resolve:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{resolvedElementId}'. Call resolve_element again.");
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
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_computed_properties requires exactly one of: locator OR elementId.");
        }

        string? resolvedElementId = null;
        long? effectiveWindowHandle = windowHandle;
        ElementLocator effectiveLocator = locator ?? new ElementLocator();

        if (hasElementId)
        {
            var id = elementId!.Trim();
            resolvedElementId = id;
            var handle = RequireHandle(id);
            if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{id}' is not a WPF handle.");
            }

            if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            effectiveWindowHandle = handle.WindowHandle;
            effectiveLocator = new ElementLocator(XPath: handle.XPath);
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var request = new GetComputedPropertiesRequest(
            WindowHandle: effectiveWindowHandle,
            Locator: effectiveLocator,
            PropertyNames: propertyNames,
            IncludeSources: includeSources,
            IncludeDefault: includeDefault,
            IncludeUnset: includeUnset,
            MaxProperties: maxProperties,
            ValueFormat: valueFormat);

        try
        {
            var response = await client.CallAsync<GetComputedPropertiesResponse>("wpf/get_computed_properties", request, cancellationToken);
            trace?.SetSummary($"props={response.Properties.Count} truncated={response.Truncated}");
            return response;
        }
        catch (InvalidOperationException ex) when (hasElementId &&
                                                  resolvedElementId is not null &&
                                                  ex.Message.StartsWith("wpf_resolve:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{resolvedElementId}'. Call resolve_element again.");
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
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_style_chain requires exactly one of: locator OR elementId.");
        }

        string? resolvedElementId = null;
        long? effectiveWindowHandle = windowHandle;
        ElementLocator effectiveLocator = locator ?? new ElementLocator();

        if (hasElementId)
        {
            var id = elementId!.Trim();
            resolvedElementId = id;
            var handle = RequireHandle(id);
            if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{id}' is not a WPF handle.");
            }

            if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            effectiveWindowHandle = handle.WindowHandle;
            effectiveLocator = new ElementLocator(XPath: handle.XPath);
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var request = new GetStyleChainRequest(
            WindowHandle: effectiveWindowHandle,
            Locator: effectiveLocator,
            IncludeThemeStyle: includeThemeStyle,
            IncludeResourceKeys: includeResourceKeys,
            MaxBasedOnDepth: maxBasedOnDepth);

        try
        {
            var response = await client.CallAsync<GetStyleChainResponse>("wpf/get_style_chain", request, cancellationToken);
            trace?.SetSummary($"entries={response.Styles.Count}");
            return response;
        }
        catch (InvalidOperationException ex) when (hasElementId &&
                                                  resolvedElementId is not null &&
                                                  ex.Message.StartsWith("wpf_resolve:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{resolvedElementId}'. Call resolve_element again.");
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
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw new ArgumentException("get_template_info requires exactly one of: locator OR elementId.");
        }

        string? resolvedElementId = null;
        long? effectiveWindowHandle = windowHandle;
        ElementLocator effectiveLocator = locator ?? new ElementLocator();

        if (hasElementId)
        {
            var id = elementId!.Trim();
            resolvedElementId = id;
            var handle = RequireHandle(id);
            if (handle.Backend != InspectionBackend.Wpf)
            {
                throw new InvalidOperationException($"elementId '{id}' is not a WPF handle.");
            }

            if (windowHandle is long requestedHandle && requestedHandle != handle.WindowHandle)
            {
                throw new ArgumentException("windowHandle does not match the elementId window.");
            }

            effectiveWindowHandle = handle.WindowHandle;
            effectiveLocator = new ElementLocator(XPath: handle.XPath);
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var request = new GetTemplateInfoRequest(
            WindowHandle: effectiveWindowHandle,
            Locator: effectiveLocator,
            IncludeNamedElements: includeNamedElements,
            MaxNamedElements: maxNamedElements,
            IncludeResourceKeys: includeResourceKeys,
            IncludePartElementRefs: includePartElementRefs);

        try
        {
            var response = await client.CallAsync<GetTemplateInfoResponse>("wpf/get_template_info", request, cancellationToken);
            var named = response.Template.NamedElements is null ? 0 : response.Template.NamedElements.Count;
            trace?.SetSummary($"named={named}");
            return response;
        }
        catch (InvalidOperationException ex) when (hasElementId &&
                                                  resolvedElementId is not null &&
                                                  ex.Message.StartsWith("wpf_resolve:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"stale_element: not_found for '{resolvedElementId}'. Call resolve_element again.");
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

    internal async Task<GetVisualTreeResponse?> TryGetVisualTreeWpfAsync(GetWpfVisualTreeRequestV2 request, CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedOrNullAsync(cancellationToken);
        if (client is null)
        {
            return null;
        }

        try
        {
            return await client.CallAsync<GetVisualTreeResponse>("wpf/get_visual_tree", request, cancellationToken);
        }
        catch
        {
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

        return await client.CallAsync<FindElementsResponse>("wpf/find_elements", request, cancellationToken);
    }

    internal async Task<FindElementsResponse?> TryFindElementsWpfAsync(FindElementsWpfRequest request, CancellationToken cancellationToken)
    {
        var client = await EnsureAgentConnectedOrNullAsync(cancellationToken);
        if (client is null)
        {
            return null;
        }

        try
        {
            return await client.CallAsync<FindElementsResponse>("wpf/find_elements", request, cancellationToken);
        }
        catch
        {
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

        return await client.CallAsync<GetPathToElementResponse>("wpf/get_path", request, cancellationToken);
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
                var pong = await connectClient.CallAsync<string>("ping", @params: null, cancellationToken);
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
