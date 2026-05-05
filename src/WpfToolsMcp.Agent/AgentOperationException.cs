using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal sealed class AgentOperationException : InvalidOperationException
{
    public AgentOperationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static AgentOperationException DispatcherUnavailable() =>
        new(
            AgentErrorCodes.DispatcherUnavailable,
            "Application.Current.Dispatcher is not available. Is the target a WPF app?");

    public static AgentOperationException InvalidRequest(string message) =>
        new(AgentErrorCodes.InvalidRequest, message);

    public static AgentOperationException MissingParams(string method) =>
        new(AgentErrorCodes.MissingParams, $"Agent method '{method}' requires request params.");
}
