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
        var code = exception is AgentEndpointException endpointException
            ? endpointException.Code
            : InferCode(exception.Message);

        return Failure(requestId, code, exception.Message, exception.ToString());
    }

    public static AgentResponse UnknownMethod(string requestId, string method) =>
        Failure(requestId, AgentErrorCodes.UnknownMethod, $"Unknown method '{method}'.");

    private static string InferCode(string message)
    {
        if (message.Contains("wpf_handle_stale:", StringComparison.OrdinalIgnoreCase))
        {
            return AgentErrorCodes.WpfHandleStale;
        }

        if (message.Contains("wpf_resolve:not_found:", StringComparison.OrdinalIgnoreCase))
        {
            return AgentErrorCodes.WpfResolveNotFound;
        }

        if (message.Contains("wpf_resolve:ambiguous:", StringComparison.OrdinalIgnoreCase))
        {
            return AgentErrorCodes.WpfResolveAmbiguous;
        }

        if (message.Contains("invalid_request:", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("requires exactly one", StringComparison.OrdinalIgnoreCase))
        {
            return AgentErrorCodes.InvalidRequest;
        }

        return AgentErrorCodes.OperationFailed;
    }
}
