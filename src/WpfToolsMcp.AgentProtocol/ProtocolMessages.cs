using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WpfToolsMcp.AgentProtocol;

public sealed record AgentRequest(string Id, string Method, JsonNode? Params = null);

public sealed class AgentResponse
{
    [JsonConstructor]
    public AgentResponse(string id, bool ok, JsonNode? result = null, AgentError? error = null)
    {
        if (GetInvalidState(id, ok, result, error) is { } invalidState)
        {
            throw new ArgumentException(invalidState);
        }

        Id = id;
        Ok = ok;
        Result = result;
        Error = error;
    }

    public string Id { get; }
    public bool Ok { get; }
    public JsonNode? Result { get; }
    public AgentError? Error { get; }

    public static AgentResponse Success(string id, JsonNode? result = null) =>
        new(id, ok: true, result: result);

    public static AgentResponse Failure(string id, AgentError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new AgentResponse(id, ok: false, error: error);
    }

    public void EnsureValid()
    {
        if (GetInvalidState(Id, Ok, Result, Error) is { } invalidState)
        {
            throw new InvalidOperationException($"Agent protocol error: {invalidState}");
        }
    }

    private static string? GetInvalidState(string? id, bool ok, JsonNode? result, AgentError? error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "Agent response id is required.";
        }

        if (ok && error is not null)
        {
            return "A successful response cannot include an error.";
        }

        if (!ok && error is null)
        {
            return "A failed response must include an error.";
        }

        if (!ok && result is not null)
        {
            return "A failed response cannot include a result.";
        }

        return null;
    }
}

public sealed record AgentError(string Message, string? Details = null, string? Code = null);
