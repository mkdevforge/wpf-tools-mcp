using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static class AgentServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly UiThreadLatencyRecorder UiThreadLatency = new();

    public static async Task RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        // Allow multiple concurrent MCP sessions to connect to the same injected agent.
        // Each connection is handled on its own task, but all WPF operations are still serialized
        // via Dispatcher in RunOnUiAsync.

        var connectionTasks = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                continue;
            }

            RemoveCompletedConnectionTasks(connectionTasks);
            connectionTasks.Add(Task.Run(() => RunConnectionAsync(pipe, cancellationToken), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(connectionTasks).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort shutdown.
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task RunConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using var _ = pipe.ConfigureAwait(false);
        var ownerId = Guid.NewGuid().ToString("N");

        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                AgentRequest request;
                try
                {
                    request = await PipeProtocol.ReadAsync<AgentRequest>(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var disconnectMonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var disconnectTask = MonitorClientDisconnectAsync(
                    pipe,
                    requestCancellation,
                    disconnectMonitorCancellation.Token);

                AgentResponse response;
                bool clientDisconnected;
                try
                {
                    response = await HandleAsync(ownerId, request, requestCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    disconnectMonitorCancellation.Cancel();
                    clientDisconnected = await disconnectTask.ConfigureAwait(false);
                }

                if (clientDisconnected)
                {
                    break;
                }

                try
                {
                    await PipeProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                UiThreadLatency.ReleaseOwner(ownerId);
            }
            catch
            {
                // Best-effort teardown; WPF-owned resources still need cleanup below.
            }

            await ReleaseOwnerResourcesAsync(ownerId).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> MonitorClientDisconnectAsync(
        Stream pipe,
        CancellationTokenSource requestCancellation,
        CancellationToken monitorCancellation)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(requestCancellation);

        var probe = new byte[1];
        try
        {
            // AgentClient does not pipeline calls, so completion here means disconnect or invalid framing.
            _ = await pipe.ReadAsync(probe, monitorCancellation).ConfigureAwait(false);
            requestCancellation.Cancel();
            return true;
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            requestCancellation.Cancel();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            requestCancellation.Cancel();
            return true;
        }
    }

    private static async Task<AgentResponse> HandleAsync(
        string ownerId,
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method)
            {
                case "ping":
                    return new AgentResponse(request.Id, Ok: true, Result: JsonValue.Create("pong"));
                case AgentProtocolCapabilities.GetCapabilitiesMethod:
                    return new AgentResponse(
                        request.Id,
                        Ok: true,
                        Result: JsonSerializer.SerializeToNode(
                            new AgentCapabilitiesResponse(
                                AgentProtocolCapabilities.CurrentProtocolVersion,
                                AgentProtocolCapabilities.Current),
                            JsonOptions));
                case "wpf/get_visual_tree":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetWpfVisualTreeRequestV2>(JsonOptions)
                            ?? new GetWpfVisualTreeRequestV2();

                        var response = WpfVisualTreeInspector.GetVisualTree(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/performance_start":
                    {
                        var typedRequest = request.Params?.Deserialize<PerformanceStartRequest>(JsonOptions)
                            ?? new PerformanceStartRequest();

                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher is null)
                        {
                            return new AgentResponse(
                                Id: request.Id,
                                Ok: false,
                                Error: new AgentError("Application.Current.Dispatcher is not available. Is the target a WPF app?"));
                        }

                        var response = UiThreadLatency.Start(ownerId, dispatcher, typedRequest);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }
                case "wpf/performance_stop":
                    {
                        var typedRequest = request.Params?.Deserialize<PerformanceStopRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");

                        var response = UiThreadLatency.Stop(ownerId, typedRequest.RunId);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }
                case "wpf/find_elements":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<FindElementsWpfRequest>(JsonOptions)
                            ?? new FindElementsWpfRequest();

                        var response = WpfVisualTreeInspector.FindElements(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.MapUiaToWpf:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<MapUiaToWpfAgentRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");

                        var response = WpfVisualTreeInspector.MapUiaToWpf(
                            ownerId,
                            typedRequest,
                            cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/get_path":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetWpfPathRequest>(JsonOptions)
                            ?? new GetWpfPathRequest();

                        var response = WpfVisualTreeInspector.GetPath(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/resolve_element":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<ResolveWpfElementRequest>(JsonOptions)
                            ?? new ResolveWpfElementRequest();

                        var response = WpfVisualTreeInspector.ResolveElement(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.ResolveElementDetailed:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<ResolveWpfElementRequest>(JsonOptions)
                            ?? new ResolveWpfElementRequest();

                        var response = WpfVisualTreeInspector.ResolveElementDetailed(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/set_value":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<SetWpfValueRequest>(JsonOptions)
                            ?? new SetWpfValueRequest();

                        var response = WpfVisualTreeInspector.SetValue(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.FocusElement:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<FocusWpfElementRequest>(JsonOptions)
                            ?? new FocusWpfElementRequest();

                        var response = WpfVisualTreeInspector.FocusElement(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/invoke":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<InvokeWpfRequest>(JsonOptions)
                            ?? new InvokeWpfRequest();

                        var response = WpfVisualTreeInspector.Invoke(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/bring_into_view":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<BringIntoViewWpfRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");

                        var response = WpfVisualTreeInspector.BringIntoView(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/release_element":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<ReleaseWpfElementRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");

                        var response = WpfVisualTreeInspector.ReleaseElement(ownerId, typedRequest);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/highlight_element":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<HighlightWpfElementRequest>(JsonOptions)
                            ?? new HighlightWpfElementRequest();

                        var response = WpfVisualTreeInspector.HighlightElement(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/pick_element_at_point":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<PickWpfElementAtPointRequest>(JsonOptions)
                            ?? new PickWpfElementAtPointRequest();

                        var response = WpfVisualTreeInspector.PickElementAtPoint(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.CorrelateScreenshotRegion:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<CorrelateWpfScreenshotRegionRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");

                        var response = WpfVisualTreeInspector.CorrelateScreenshotRegion(
                            ownerId,
                            typedRequest,
                            cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/get_binding_info":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetBindingInfoRequest>(JsonOptions)
                            ?? new GetBindingInfoRequest();

                        var response = WpfVisualTreeInspector.GetBindingInfo(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/get_binding_errors":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetBindingErrorsRequest>(JsonOptions)
                            ?? new GetBindingErrorsRequest();

                        var response = WpfVisualTreeInspector.GetBindingErrors(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.GetValidationErrors:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetValidationErrorsRequest>(JsonOptions)
                            ?? new GetValidationErrorsRequest();

                        var response = WpfVisualTreeInspector.GetValidationErrors(
                            ownerId,
                            typedRequest,
                            cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.GetCommandInfo:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetCommandInfoRequest>(JsonOptions)
                            ?? new GetCommandInfoRequest();

                        var response = WpfVisualTreeInspector.GetCommandInfo(
                            ownerId,
                            typedRequest,
                            cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/uia_coverage_report":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetUiaCoverageReportRequest>(JsonOptions)
                            ?? new GetUiaCoverageReportRequest();

                        var response = WpfVisualTreeInspector.GetUiaCoverageReport(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/get_data_context":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetDataContextRequest>(JsonOptions)
                            ?? new GetDataContextRequest();

                        var response = WpfVisualTreeInspector.GetDataContext(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/get_computed_properties":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetComputedPropertiesRequest>(JsonOptions)
                            ?? new GetComputedPropertiesRequest();

                        var response = WpfVisualTreeInspector.GetComputedProperties(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.GetLayoutContext:
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetLayoutContextRequest>(JsonOptions)
                            ?? new GetLayoutContextRequest();

                        var response = WpfVisualTreeInspector.GetLayoutContext(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case AgentProtocolCapabilities.CaptureDiagnosticSnapshot:
                    {
                        var typedRequest = request.Params?.Deserialize<CaptureWpfDiagnosticSnapshotRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");
                        var dispatcher = WpfVisualTreeInspector.ResolveObservationDispatcher(
                            typedRequest.WindowHandle);

                        return await RunOnDispatcherAsync(dispatcher, () =>
                        {
                            var response = WpfVisualTreeInspector.CaptureDiagnosticSnapshot(
                                ownerId,
                                typedRequest,
                                cancellationToken);
                            return new AgentResponse(
                                request.Id,
                                Ok: true,
                                Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                        }, request.Id, cancellationToken);
                    }
                case "wpf/observe_state_start":
                    {
                        var typedRequest = request.Params?.Deserialize<ObserveStateStartRequest>(JsonOptions)
                            ?? new ObserveStateStartRequest();
                        var dispatcher = WpfVisualTreeInspector.ResolveObservationDispatcher(
                            typedRequest.WindowHandle);

                        return await RunOnDispatcherAsync(dispatcher, () =>
                        {
                            var response = WpfVisualTreeInspector.StartObserveState(
                                ownerId,
                                typedRequest,
                                cancellationToken);
                            return new AgentResponse(
                                request.Id,
                                Ok: true,
                                Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                        }, request.Id, cancellationToken);
                    }
                case "wpf/observe_state_poll":
                    {
                        var typedRequest = request.Params?.Deserialize<ObserveStatePollRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");

                        var response = WpfVisualTreeInspector.PollObserveState(ownerId, typedRequest);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }
                case "wpf/observe_state_stop":
                    {
                        var typedRequest = request.Params?.Deserialize<ObserveStateStopRequest>(JsonOptions)
                            ?? throw new InvalidOperationException("Missing request params.");
                        var dispatcher = WpfVisualTreeInspector.ResolveObserveStateDispatcher(
                            ownerId,
                            typedRequest.ObservationId);

                        return await RunOnDispatcherAsync(dispatcher, () =>
                        {
                            var response = WpfVisualTreeInspector.StopObserveState(ownerId, typedRequest);
                            return new AgentResponse(
                                request.Id,
                                Ok: true,
                                Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                        }, request.Id, cancellationToken);
                    }
                case "wpf/get_style_chain":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetStyleChainRequest>(JsonOptions)
                            ?? new GetStyleChainRequest();

                        var response = WpfVisualTreeInspector.GetStyleChain(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/get_template_info":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetTemplateInfoRequest>(JsonOptions)
                            ?? new GetTemplateInfoRequest();

                        var response = WpfVisualTreeInspector.GetTemplateInfo(ownerId, typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                default:
                    return new AgentResponse(
                        request.Id,
                        Ok: false,
                        Error: new AgentError($"Unknown method '{request.Method}'."));
            }
        }
        catch (Exception ex)
        {
            return new AgentResponse(
                request.Id,
                Ok: false,
                Error: new AgentError(ex.Message, ex.ToString()));
        }
    }

    private static async Task ReleaseOwnerResourcesAsync(string ownerId)
    {
        try
        {
            await WpfVisualTreeInspector.ReleaseOwnerObservationsAsync(ownerId).ConfigureAwait(false);
        }
        catch
        {
            // Observation teardown is best-effort while owning dispatchers shut down.
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
                return;
            }

            var operation = dispatcher.InvokeAsync(
                () => WpfVisualTreeInspector.ReleaseOwnerResources(ownerId),
                DispatcherPriority.Send);
            await operation.Task.ConfigureAwait(false);
        }
        catch
        {
            // The target dispatcher may be shutting down while the pipe disconnects.
        }
    }

    private static void RemoveCompletedConnectionTasks(List<Task> connectionTasks)
    {
        for (var i = connectionTasks.Count - 1; i >= 0; i--)
        {
            var task = connectionTasks[i];
            if (!task.IsCompleted)
            {
                continue;
            }

            _ = task.Exception;
            connectionTasks.RemoveAt(i);
        }
    }

    private static async Task<AgentResponse> RunOnUiAsync(Func<AgentResponse> action, string requestId, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return new AgentResponse(
                Id: requestId,
                Ok: false,
                Error: new AgentError("Application.Current.Dispatcher is not available. Is the target a WPF app?"));
        }

        return await RunOnDispatcherAsync(dispatcher, action, requestId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentResponse> RunOnDispatcherAsync(
        Dispatcher dispatcher,
        Func<AgentResponse> action,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return new AgentResponse(
                Id: requestId,
                Ok: false,
                Error: new AgentError("The target WPF dispatcher is shutting down."));
        }

        if (dispatcher.CheckAccess())
        {
            return action();
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken);
        return await op.Task.ConfigureAwait(false);
    }
}
