using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal sealed class SetValueEndpoint : AgentEndpoint<SetWpfValueRequest, SetValueResponse>
{
    public override string Method => AgentMethods.SetValue;

    protected override SetWpfValueRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(SetWpfValueRequest request)
    {
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

        var hasText = request.Text is not null;
        var hasValue = request.Value.HasValue;
        if (hasText == hasValue)
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: set_value requires exactly one of text OR value.");
        }
    }

    protected override SetValueResponse Execute(
        SetWpfValueRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.SetValue(request, cancellationToken);
}

internal sealed class BringIntoViewEndpoint : AgentEndpoint<BringIntoViewWpfRequest, BringIntoViewWpfResponse>
{
    public override string Method => AgentMethods.BringIntoView;

    protected override void Validate(BringIntoViewWpfRequest request)
    {
        if (request.WindowHandle == 0)
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: windowHandle is required.");
        }

        var hasXPath = !string.IsNullOrWhiteSpace(request.XPath);
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasXPath == hasElementId)
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: provide exactly one of elementId OR xpath.");
        }
    }

    protected override BringIntoViewWpfResponse Execute(
        BringIntoViewWpfRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.BringIntoView(request, cancellationToken);
}

internal sealed class ReleaseElementEndpoint : AgentEndpoint<ReleaseWpfElementRequest, ReleaseElementResponse>
{
    public override string Method => AgentMethods.ReleaseElement;

    protected override void Validate(ReleaseWpfElementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ElementId))
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: elementId is required.");
        }
    }

    protected override ReleaseElementResponse Execute(
        ReleaseWpfElementRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.ReleaseElement(request);
}

internal sealed class HighlightElementEndpoint : AgentEndpoint<HighlightWpfElementRequest, HighlightWpfElementResponse>
{
    public override string Method => AgentMethods.HighlightElement;

    protected override HighlightWpfElementRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(HighlightWpfElementRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override HighlightWpfElementResponse Execute(
        HighlightWpfElementRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.HighlightElement(request, cancellationToken);
}
