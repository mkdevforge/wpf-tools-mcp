namespace WpfToolsMcp.Agent;

internal sealed class AgentOperationRegistry
{
    private readonly Dictionary<string, IAgentOperation> _operations;

    public AgentOperationRegistry(IEnumerable<IAgentOperation> operations)
    {
        _operations = new Dictionary<string, IAgentOperation>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            if (!_operations.TryAdd(operation.Method, operation))
            {
                throw new InvalidOperationException($"Duplicate agent method '{operation.Method}'.");
            }
        }
    }

    public bool TryGet(string method, out IAgentOperation operation) =>
        _operations.TryGetValue(method, out operation!);
}
