using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Automation;

internal sealed class AgentCallException : InvalidOperationException, IAgentErrorCodeException
{
    public AgentCallException(string message, string? code, string? details)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string? Code { get; }
    public string? Details { get; }
}
