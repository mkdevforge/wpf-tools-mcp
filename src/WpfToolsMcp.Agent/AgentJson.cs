using System.Text.Json;

namespace WpfToolsMcp.Agent;

internal static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
