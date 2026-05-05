namespace WpfToolsMcp.Agent;

internal sealed class AgentEndpointRegistry
{
    private readonly Dictionary<string, IAgentEndpoint> _endpoints;

    public AgentEndpointRegistry(IEnumerable<IAgentEndpoint> endpoints)
    {
        _endpoints = new Dictionary<string, IAgentEndpoint>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            if (!_endpoints.TryAdd(endpoint.Method, endpoint))
            {
                throw new InvalidOperationException($"Duplicate agent method '{endpoint.Method}'.");
            }
        }
    }

    public bool TryGet(string method, out IAgentEndpoint endpoint) =>
        _endpoints.TryGetValue(method, out endpoint!);
}
