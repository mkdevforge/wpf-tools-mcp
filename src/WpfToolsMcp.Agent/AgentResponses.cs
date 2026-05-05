using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Agent;

internal static class AgentResponses
{
    public static AgentResponse Success<T>(string requestId, T result) =>
        AgentResponse.Success(
            requestId,
            result is JsonNode node
                ? node
                : JsonSerializer.SerializeToNode(result, AgentJson.Options));

    public static AgentResponse Failure(string requestId, string code, string message, string? details = null) =>
        AgentResponse.Failure(
            requestId,
            new AgentError(message, details, code));

    public static AgentResponse FromException(string requestId, Exception exception)
    {
        var code = exception is IAgentErrorCodeException { Code: { } typedCode } &&
                   !string.IsNullOrWhiteSpace(typedCode)
            ? typedCode
            : AgentErrorCodes.OperationFailed;

        return Failure(requestId, code, exception.Message, exception.ToString());
    }

    public static AgentResponse UnknownMethod(string requestId, string method) =>
        Failure(requestId, AgentErrorCodes.UnknownMethod, $"Unknown method '{method}'.");

}
