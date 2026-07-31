using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    public async Task<GetCommandInfoResponse> GetCommandInfoAsync(
        ElementLocator? locator = null,
        string? elementId = null,
        long? windowHandle = null,
        int maxAncestors = 8,
        int maxBindings = 128,
        int maxValueLength = 500,
        CancellationToken cancellationToken = default)
    {
        var trace = BeginTraceSpan("get_command_info");
        try
        {
            var target = PrepareWpfAgentTarget("get_command_info", locator, elementId, windowHandle);
            var client = await EnsureAgentConnectedAsync(cancellationToken).ConfigureAwait(false);
            var request = new GetCommandInfoRequest(
                WindowHandle: target.WindowHandle,
                Locator: target.Locator,
                ElementId: target.AgentElementId,
                MaxAncestors: maxAncestors,
                MaxBindings: maxBindings,
                MaxValueLength: maxValueLength);
            var fallbackRequest = target.RecoveryLocator is null
                ? null
                : request with { Locator = target.RecoveryLocator, ElementId = null };

            var response = await CallGetCommandInfoWhenSupportedAsync(
                GetAgentCapabilities(client),
                () => CallWpfAgentTargetAsync<GetCommandInfoResponse>(
                    client,
                    AgentProtocolCapabilities.GetCommandInfo,
                    request,
                    fallbackRequest,
                    target,
                    cancellationToken)).ConfigureAwait(false);
            var normalizedElement = await NormalizeCommandElementAsync(
                client,
                response.Element,
                target.PublicElementId,
                target.WindowHandle ?? response.WindowHandleUsed).ConfigureAwait(false);

            response = response with
            {
                Element = normalizedElement,
                WindowHandleUsed = target.WindowHandle ?? response.WindowHandleUsed
            };
            trace?.SetSummary(
                $"source={response.Source.Status} canExecute={response.CanExecute.Status} " +
                $"contexts={response.Counts.ReturnedContexts} " +
                $"bindings={response.Counts.ReturnedCommandBindings + response.Counts.ReturnedInputBindings} " +
                $"truncated={response.Truncated}");
            return response;
        }
        catch (Exception ex)
        {
            trace?.SetError(ex);
            throw;
        }
        finally
        {
            trace?.Dispose();
        }
    }

    private async Task<ElementRef> NormalizeCommandElementAsync(
        AgentClient client,
        ElementRef element,
        string? publicElementId,
        long windowHandle)
    {
        if (!string.IsNullOrWhiteSpace(publicElementId))
        {
            var normalized = await StripAgentElementIdAsync(
                client,
                element,
                publicElementId).ConfigureAwait(false);
            return WithPublicCommandElementId(normalized, publicElementId);
        }

        if (windowHandle == 0)
        {
            throw new InvalidOperationException(
                "wpf_command_info: the agent did not report the resolved element window handle.");
        }

        return RegisterCommandElement(element, windowHandle);
    }

    internal ElementRef RegisterCommandElement(ElementRef element, long windowHandle)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (windowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHandle), "windowHandle must be non-zero.");
        }

        var publicElementId = _elementHandles.RegisterWpf(
            windowHandle,
            element.XPath,
            element.ElementIdWpf,
            element.Type,
            element.AutomationId,
            element.Name,
            element.ClassName,
            element.Bounds);
        return WithPublicCommandElementId(element, publicElementId);
    }

    internal static ElementRef WithPublicCommandElementId(ElementRef element, string publicElementId) =>
        element with { ElementId = publicElementId, ElementIdWpf = null };

    internal static InvalidOperationException CreateGetCommandInfoCapabilityException() =>
        new(
            "agent_capability_unavailable: get_command_info requires the current WPF agent. " +
            "Restart the target application, start a new MCP session, and attach again so the current agent can be injected.");

    internal static Task<T> CallGetCommandInfoWhenSupportedAsync<T>(
        AgentCapabilitiesResponse? capabilities,
        Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return capabilities is not null &&
               capabilities.Capabilities.Contains(
                   AgentProtocolCapabilities.GetCommandInfo,
                   StringComparer.Ordinal)
            ? call()
            : Task.FromException<T>(CreateGetCommandInfoCapabilityException());
    }
}
