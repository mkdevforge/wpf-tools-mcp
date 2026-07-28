using System.Text.Json.Nodes;

namespace WpfToolsMcp.AgentProtocol;

public sealed record AgentRequest(string Id, string Method, JsonNode? Params = null);

public sealed record AgentResponse(string Id, bool Ok, JsonNode? Result = null, AgentError? Error = null);

public sealed record AgentError(string Message, string? Details = null);

public sealed record AgentCapabilitiesResponse(
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities);

public static class AgentProtocolCapabilities
{
    public const int CurrentProtocolVersion = 1;
    public const string GetCapabilitiesMethod = "capabilities";
    public const string ResolveElementDetailed = "wpf/resolve_element_detailed";
    public const string FindElementsDiscoveryCounts = "wpf/find_elements:discovery-counts";
    public const string GetLayoutContext = "wpf/get_layout_context";

    public static IReadOnlyList<string> Current { get; } = Array.AsReadOnly<string>(
    [
        ResolveElementDetailed,
        FindElementsDiscoveryCounts,
        GetLayoutContext
    ]);
}
