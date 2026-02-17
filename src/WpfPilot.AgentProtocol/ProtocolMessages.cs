using System.Text.Json.Nodes;

namespace WpfPilot.AgentProtocol;

public sealed record AgentRequest(string Id, string Method, JsonNode? Params = null);

public sealed record AgentResponse(string Id, bool Ok, JsonNode? Result = null, AgentError? Error = null);

public sealed record AgentError(string Message, string? Details = null);

