using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using WpfPilot.AgentProtocol;
using WpfPilot.Contracts;

namespace WpfPilot.Agent;

internal static class AgentServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }

            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                AgentRequest request;
                try
                {
                    request = await PipeProtocol.ReadAsync<AgentRequest>(pipe, cancellationToken);
                }
                catch
                {
                    break;
                }

                var response = await HandleAsync(request, cancellationToken);

                try
                {
                    await PipeProtocol.WriteAsync(pipe, response, cancellationToken);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private static async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method)
            {
                case "ping":
                    return new AgentResponse(request.Id, Ok: true, Result: JsonValue.Create("pong"));
                case "wpf/get_visual_tree":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<GetWpfVisualTreeRequestV2>(JsonOptions)
                            ?? new GetWpfVisualTreeRequestV2();

                        var response = WpfVisualTreeInspector.GetVisualTree(typedRequest, cancellationToken);
                        return new AgentResponse(
                            request.Id,
                            Ok: true,
                            Result: JsonSerializer.SerializeToNode(response, JsonOptions));
                    }, request.Id, cancellationToken);
                case "wpf/find_elements":
                    return await RunOnUiAsync(() =>
                    {
                        var typedRequest = request.Params?.Deserialize<FindElementsWpfRequest>(JsonOptions)
                            ?? new FindElementsWpfRequest();

                        var response = WpfVisualTreeInspector.FindElements(typedRequest, cancellationToken);
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

                        var response = WpfVisualTreeInspector.GetPath(typedRequest, cancellationToken);
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

                        var response = WpfVisualTreeInspector.ResolveElement(typedRequest, cancellationToken);
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

                        var response = WpfVisualTreeInspector.PickElementAtPoint(typedRequest, cancellationToken);
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

                        var response = WpfVisualTreeInspector.GetBindingInfo(typedRequest, cancellationToken);
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

                        var response = WpfVisualTreeInspector.GetBindingErrors(typedRequest, cancellationToken);
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

                        var response = WpfVisualTreeInspector.GetDataContext(typedRequest, cancellationToken);
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

        if (dispatcher.CheckAccess())
        {
            return action();
        }

        var op = dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken);
        return await op.Task;
    }
}
