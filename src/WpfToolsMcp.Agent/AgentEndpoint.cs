using System.Text.Json;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal interface IAgentEndpoint
{
    string Method { get; }
    Task<AgentResponse> HandleAsync(AgentRequest request, AgentEndpointContext context, CancellationToken cancellationToken);
}

internal abstract class AgentEndpoint<TResponse> : IAgentEndpoint
{
    public abstract string Method { get; }

    public Task<AgentResponse> HandleAsync(
        AgentRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken)
    {
        var response = Execute(context, cancellationToken);
        return Task.FromResult(AgentResponses.Success(request.Id, response));
    }

    protected abstract TResponse Execute(AgentEndpointContext context, CancellationToken cancellationToken);
}

internal abstract class AgentEndpoint<TRequest, TResponse> : IAgentEndpoint
{
    public abstract string Method { get; }

    public Task<AgentResponse> HandleAsync(
        AgentRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken)
    {
        var typedRequest = ReadRequest(request);
        Validate(typedRequest);

        var response = Execute(typedRequest, context, cancellationToken);
        return Task.FromResult(AgentResponses.Success(request.Id, response));
    }

    protected virtual TRequest CreateDefaultRequest() =>
        throw AgentEndpointException.MissingParams(Method);

    protected virtual void Validate(TRequest request)
    {
    }

    protected abstract TResponse Execute(
        TRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken);

    private TRequest ReadRequest(AgentRequest request)
    {
        if (request.Params is null)
        {
            return CreateDefaultRequest();
        }

        var typedRequest = request.Params.Deserialize<TRequest>(AgentJson.Options);
        return typedRequest is null
            ? throw AgentEndpointException.InvalidRequest($"Agent method '{Method}' params could not be deserialized.")
            : typedRequest;
    }
}
