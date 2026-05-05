using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static class AgentEndpointValidation
{
    public static void RequireElementTarget(ElementLocator? locator, string? elementId, long? windowHandle)
    {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: provide exactly one of locator OR elementId.");
        }

        if (hasElementId && windowHandle is not > 0)
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: windowHandle is required with elementId.");
        }
    }
}
