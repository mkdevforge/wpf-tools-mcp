using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static class AgentEndpointValidation
{
    public static void RequireElementTarget(ElementLocator? locator, string? elementId, long? windowHandle)
    {
        try
        {
            _ = ElementTarget.Parse(
                locator,
                elementId,
                windowHandle,
                requireWindowHandleForElementId: true);
        }
        catch (ArgumentException ex)
        {
            throw AgentEndpointException.InvalidRequest(ex.Message);
        }
    }
}
