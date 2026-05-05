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

    public AgentEndpointInvocation Bind(AgentRequest request) =>
        new UiThreadAgentEndpointInvocation(_inner.Bind(request));

    private sealed class UiThreadAgentEndpointInvocation : AgentEndpointInvocation
    {
        private readonly AgentEndpointInvocation _inner;

        public UiThreadAgentEndpointInvocation(AgentEndpointInvocation inner)
            : base(inner.RequestId)
        {
            _inner = inner;
        }

        public override async Task<AgentResponse> ExecuteAsync(
            AgentEndpointContext context,
            CancellationToken cancellationToken)
        {
            var dispatcher = context.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                return await _inner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }

            var operation = dispatcher.InvokeAsync(
                () => _inner.ExecuteAsync(context, cancellationToken),
                DispatcherPriority.Send,
                cancellationToken);

            var responseTask = await operation.Task.ConfigureAwait(false);
            return await responseTask.ConfigureAwait(false);
        }
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

    public AgentEndpointInvocation Bind(AgentRequest request)
    {
        try
        {
            return new ErrorMappingAgentEndpointInvocation(_inner.Bind(request));
        }
        catch (Exception ex)
        {
            return AgentEndpointInvocation.FromException(request.Id, ex);
        }
    }

    private sealed class ErrorMappingAgentEndpointInvocation : AgentEndpointInvocation
    {
        private readonly AgentEndpointInvocation _inner;

        public ErrorMappingAgentEndpointInvocation(AgentEndpointInvocation inner)
            : base(inner.RequestId)
        {
            _inner = inner;
        }

        public override async Task<AgentResponse> ExecuteAsync(
            AgentEndpointContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                return AgentResponses.Failure(RequestId, AgentErrorCodes.OperationCanceled, ex.Message, ex.ToString());
            }
            catch (Exception ex)
            {
                return AgentResponses.FromException(RequestId, ex);
            }
        }
    }
}
