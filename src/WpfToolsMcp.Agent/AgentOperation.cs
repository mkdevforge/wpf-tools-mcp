using System.Text.Json;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal interface IAgentOperation
{
    string Method { get; }
    bool RequiresUiThread { get; }
    AgentResponse Handle(AgentRequest request, AgentOperationContext context, CancellationToken cancellationToken);
}

internal static class AgentOperation
{
    public static IAgentOperation NoParams<TResponse>(
        string method,
        bool requiresUiThread,
        Func<AgentOperationContext, CancellationToken, TResponse> execute) =>
        new NoParamsAgentOperation<TResponse>(method, requiresUiThread, execute);

    public static IAgentOperation OptionalParams<TRequest, TResponse>(
        string method,
        bool requiresUiThread,
        Func<TRequest> defaultRequest,
        Func<TRequest, AgentOperationContext, CancellationToken, TResponse> execute,
        Action<TRequest>? validate = null) =>
        new TypedAgentOperation<TRequest, TResponse>(
            method,
            requiresUiThread,
            defaultRequest,
            execute,
            validate);

    public static IAgentOperation RequiredParams<TRequest, TResponse>(
        string method,
        bool requiresUiThread,
        Func<TRequest, AgentOperationContext, CancellationToken, TResponse> execute,
        Action<TRequest>? validate = null) =>
        new TypedAgentOperation<TRequest, TResponse>(
            method,
            requiresUiThread,
            defaultRequest: null,
            execute,
            validate);
}

internal sealed class NoParamsAgentOperation<TResponse> : IAgentOperation
{
    private readonly Func<AgentOperationContext, CancellationToken, TResponse> _execute;

    public NoParamsAgentOperation(
        string method,
        bool requiresUiThread,
        Func<AgentOperationContext, CancellationToken, TResponse> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Method = method;
        RequiresUiThread = requiresUiThread;
        _execute = execute;
    }

    public string Method { get; }
    public bool RequiresUiThread { get; }

    public AgentResponse Handle(AgentRequest request, AgentOperationContext context, CancellationToken cancellationToken)
    {
        var response = _execute(context, cancellationToken);
        return AgentResponses.Success(request.Id, response);
    }
}

internal sealed class TypedAgentOperation<TRequest, TResponse> : IAgentOperation
{
    private readonly Func<TRequest>? _defaultRequest;
    private readonly Func<TRequest, AgentOperationContext, CancellationToken, TResponse> _execute;
    private readonly Action<TRequest>? _validate;

    public TypedAgentOperation(
        string method,
        bool requiresUiThread,
        Func<TRequest>? defaultRequest,
        Func<TRequest, AgentOperationContext, CancellationToken, TResponse> execute,
        Action<TRequest>? validate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Method = method;
        RequiresUiThread = requiresUiThread;
        _defaultRequest = defaultRequest;
        _execute = execute;
        _validate = validate;
    }

    public string Method { get; }
    public bool RequiresUiThread { get; }

    public AgentResponse Handle(AgentRequest request, AgentOperationContext context, CancellationToken cancellationToken)
    {
        var typedRequest = ReadRequest(request);
        _validate?.Invoke(typedRequest);

        var response = _execute(typedRequest, context, cancellationToken);
        return AgentResponses.Success(request.Id, response);
    }

    private TRequest ReadRequest(AgentRequest request)
    {
        if (request.Params is null)
        {
            return _defaultRequest is null
                ? throw AgentOperationException.MissingParams(Method)
                : _defaultRequest();
        }

        var typedRequest = request.Params.Deserialize<TRequest>(AgentJson.Options);
        return typedRequest is null
            ? throw AgentOperationException.InvalidRequest($"Agent method '{Method}' params could not be deserialized.")
            : typedRequest;
    }
}
