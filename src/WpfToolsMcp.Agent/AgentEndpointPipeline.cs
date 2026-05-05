using System.Windows.Threading;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal readonly record struct AgentEndpointRegistration(IAgentEndpoint Endpoint, bool RequiresUiThread)
{
    public static AgentEndpointRegistration Background(IAgentEndpoint endpoint) =>
        new(endpoint, RequiresUiThread: false);

    public static AgentEndpointRegistration UiThread(IAgentEndpoint endpoint) =>
        new(endpoint, RequiresUiThread: true);
}

internal static class AgentEndpointPipeline
{
    public static IEnumerable<IAgentEndpoint> Build(IEnumerable<AgentEndpointRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            var endpoint = registration.RequiresUiThread
                ? new UiThreadAgentEndpoint(registration.Endpoint)
                : registration.Endpoint;

            yield return new ErrorMappingAgentEndpoint(endpoint);
        }
    }
}

internal sealed class UiThreadAgentEndpoint : IAgentEndpoint
{
    private readonly IAgentEndpoint _inner;

    public UiThreadAgentEndpoint(IAgentEndpoint inner)
    {
        _inner = inner;
    }

    public string Method => _inner.Method;

    public async Task<AgentResponse> HandleAsync(
        AgentRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken)
    {
        var dispatcher = context.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            return await _inner.HandleAsync(request, context, cancellationToken).ConfigureAwait(false);
        }

        var operation = dispatcher.InvokeAsync(
            () => _inner.HandleAsync(request, context, cancellationToken),
            DispatcherPriority.Send,
            cancellationToken);

        var responseTask = await operation.Task.ConfigureAwait(false);
        return await responseTask.ConfigureAwait(false);
    }
}

internal sealed class ErrorMappingAgentEndpoint : IAgentEndpoint
{
    private readonly IAgentEndpoint _inner;

    public ErrorMappingAgentEndpoint(IAgentEndpoint inner)
    {
        _inner = inner;
    }

    public string Method => _inner.Method;

    public async Task<AgentResponse> HandleAsync(
        AgentRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.HandleAsync(request, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return AgentResponses.Failure(request.Id, AgentErrorCodes.OperationCanceled, ex.Message, ex.ToString());
        }
        catch (Exception ex)
        {
            return AgentResponses.FromException(request.Id, ex);
        }
    }
}
