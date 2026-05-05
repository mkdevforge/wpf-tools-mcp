using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal sealed class GetVisualTreeEndpoint : AgentEndpoint<GetWpfVisualTreeRequestV2, GetVisualTreeResponse>
{
    public override string Method => AgentMethods.GetVisualTree;

    protected override GetWpfVisualTreeRequestV2 CreateDefaultRequest() =>
        new();

    protected override GetVisualTreeResponse Execute(
        GetWpfVisualTreeRequestV2 request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetVisualTree(request, cancellationToken);
}

internal sealed class FindElementsEndpoint : AgentEndpoint<FindElementsWpfRequest, FindElementsResponse>
{
    public override string Method => AgentMethods.FindElements;

    protected override FindElementsWpfRequest CreateDefaultRequest() =>
        new();

    protected override FindElementsResponse Execute(
        FindElementsWpfRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.FindElements(request, cancellationToken);
}

internal sealed class GetPathEndpoint : AgentEndpoint<GetWpfPathRequest, GetPathToElementResponse>
{
    public override string Method => AgentMethods.GetPath;

    protected override GetWpfPathRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(GetWpfPathRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override GetPathToElementResponse Execute(
        GetWpfPathRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetPath(request, cancellationToken);
}

internal sealed class ResolveElementEndpoint : AgentEndpoint<ResolveWpfElementRequest, ElementRef>
{
    public override string Method => AgentMethods.ResolveElement;

    protected override ResolveWpfElementRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(ResolveWpfElementRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override ElementRef Execute(
        ResolveWpfElementRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.ResolveElement(request, cancellationToken);
}

internal sealed class PickElementAtPointEndpoint : AgentEndpoint<PickWpfElementAtPointRequest, PickWpfElementAtPointResponse>
{
    public override string Method => AgentMethods.PickElementAtPoint;

    protected override PickWpfElementAtPointRequest CreateDefaultRequest() =>
        new();

    protected override PickWpfElementAtPointResponse Execute(
        PickWpfElementAtPointRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.PickElementAtPoint(request, cancellationToken);
}
