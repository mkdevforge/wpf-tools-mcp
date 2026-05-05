using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal sealed class AgentEndpointException : InvalidOperationException, IAgentErrorCodeException
{
    public AgentEndpointException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static AgentEndpointException DispatcherUnavailable() =>
        new(
            AgentErrorCodes.DispatcherUnavailable,
            "Application.Current.Dispatcher is not available. Is the target a WPF app?");

    public static AgentEndpointException InvalidRequest(string message) =>
        new(AgentErrorCodes.InvalidRequest, message);

    public static AgentEndpointException MissingParams(string method) =>
        new(AgentErrorCodes.MissingParams, $"Agent method '{method}' requires request params.");
}
