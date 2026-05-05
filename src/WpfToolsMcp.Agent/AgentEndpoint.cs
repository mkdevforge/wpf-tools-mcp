using System.Text.Json;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal interface IAgentEndpoint
{
    string Method { get; }
    AgentEndpointInvocation Bind(AgentRequest request);
}

internal abstract class AgentEndpointInvocation
{
    protected AgentEndpointInvocation(string requestId)
    {
        RequestId = requestId;
    }

    public string RequestId { get; }

    public abstract Task<AgentResponse> ExecuteAsync(
        AgentEndpointContext context,
        CancellationToken cancellationToken);

    public static AgentEndpointInvocation Failure(string requestId, string code, string message, string? details = null) =>
        new FailureAgentEndpointInvocation(requestId, code, message, details);

    public static AgentEndpointInvocation FromException(string requestId, Exception exception)
    {
        var response = AgentResponses.FromException(requestId, exception);
        return new CompletedAgentEndpointInvocation(response);
    }
}

internal sealed class CompletedAgentEndpointInvocation : AgentEndpointInvocation
{
    private readonly AgentResponse _response;

    public CompletedAgentEndpointInvocation(AgentResponse response)
        : base(response.Id)
    {
        _response = response;
    }

    public override Task<AgentResponse> ExecuteAsync(
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(_response);
}

internal sealed class FailureAgentEndpointInvocation : AgentEndpointInvocation
{
    private readonly string _code;
    private readonly string _message;
    private readonly string? _details;

    public FailureAgentEndpointInvocation(string requestId, string code, string message, string? details)
        : base(requestId)
    {
        _code = code;
        _message = message;
        _details = details;
    }

    public override Task<AgentResponse> ExecuteAsync(
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(AgentResponses.Failure(RequestId, _code, _message, _details));
}

internal abstract class AgentEndpoint<TResponse> : IAgentEndpoint
{
    public abstract string Method { get; }

    public AgentEndpointInvocation Bind(AgentRequest request) =>
        new BoundAgentEndpointInvocation(this, request.Id);

    protected abstract TResponse Execute(AgentEndpointContext context, CancellationToken cancellationToken);

    private sealed class BoundAgentEndpointInvocation : AgentEndpointInvocation
    {
        private readonly AgentEndpoint<TResponse> _endpoint;

        public BoundAgentEndpointInvocation(AgentEndpoint<TResponse> endpoint, string requestId)
            : base(requestId)
        {
            _endpoint = endpoint;
        }

        public override Task<AgentResponse> ExecuteAsync(
            AgentEndpointContext context,
            CancellationToken cancellationToken)
        {
            var response = _endpoint.Execute(context, cancellationToken);
            return Task.FromResult(AgentResponses.Success(RequestId, response));
        }
    }
}

internal abstract class AgentEndpoint<TRequest, TResponse> : IAgentEndpoint
{
    public abstract string Method { get; }

    public AgentEndpointInvocation Bind(AgentRequest request)
    {
        var typedRequest = ReadRequest(request);
        Validate(typedRequest);
        return new BoundAgentEndpointInvocation(this, request.Id, typedRequest);
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

    private sealed class BoundAgentEndpointInvocation : AgentEndpointInvocation
    {
        private readonly AgentEndpoint<TRequest, TResponse> _endpoint;
        private readonly TRequest _request;

        public BoundAgentEndpointInvocation(
            AgentEndpoint<TRequest, TResponse> endpoint,
            string requestId,
            TRequest request)
            : base(requestId)
        {
            _endpoint = endpoint;
            _request = request;
        }

        public override Task<AgentResponse> ExecuteAsync(
            AgentEndpointContext context,
            CancellationToken cancellationToken)
        {
            var response = _endpoint.Execute(_request, context, cancellationToken);
            return Task.FromResult(AgentResponses.Success(RequestId, response));
        }
    }
}
