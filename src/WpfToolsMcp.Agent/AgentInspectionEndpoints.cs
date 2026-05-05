using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal sealed class GetBindingInfoEndpoint : AgentEndpoint<GetBindingInfoRequest, GetBindingInfoResponse>
{
    public override string Method => AgentMethods.GetBindingInfo;

    protected override GetBindingInfoRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(GetBindingInfoRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override GetBindingInfoResponse Execute(
        GetBindingInfoRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetBindingInfo(request, cancellationToken);
}

internal sealed class GetBindingErrorsEndpoint : AgentEndpoint<GetBindingErrorsRequest, GetBindingErrorsResponse>
{
    public override string Method => AgentMethods.GetBindingErrors;

    protected override GetBindingErrorsRequest CreateDefaultRequest() =>
        new();

    protected override GetBindingErrorsResponse Execute(
        GetBindingErrorsRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetBindingErrors(request, cancellationToken);
}

internal sealed class GetUiaCoverageReportEndpoint : AgentEndpoint<GetUiaCoverageReportRequest, GetUiaCoverageReportResponse>
{
    public override string Method => AgentMethods.GetUiaCoverageReport;

    protected override GetUiaCoverageReportRequest CreateDefaultRequest() =>
        new();

    protected override GetUiaCoverageReportResponse Execute(
        GetUiaCoverageReportRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetUiaCoverageReport(request, cancellationToken);
}

internal sealed class GetDataContextEndpoint : AgentEndpoint<GetDataContextRequest, GetDataContextResponse>
{
    public override string Method => AgentMethods.GetDataContext;

    protected override GetDataContextRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(GetDataContextRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override GetDataContextResponse Execute(
        GetDataContextRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetDataContext(request, cancellationToken);
}

internal sealed class GetComputedPropertiesEndpoint : AgentEndpoint<GetComputedPropertiesRequest, GetComputedPropertiesResponse>
{
    public override string Method => AgentMethods.GetComputedProperties;

    protected override GetComputedPropertiesRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(GetComputedPropertiesRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override GetComputedPropertiesResponse Execute(
        GetComputedPropertiesRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetComputedProperties(request, cancellationToken);
}

internal sealed class GetStyleChainEndpoint : AgentEndpoint<GetStyleChainRequest, GetStyleChainResponse>
{
    public override string Method => AgentMethods.GetStyleChain;

    protected override GetStyleChainRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(GetStyleChainRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override GetStyleChainResponse Execute(
        GetStyleChainRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetStyleChain(request, cancellationToken);
}

internal sealed class GetTemplateInfoEndpoint : AgentEndpoint<GetTemplateInfoRequest, GetTemplateInfoResponse>
{
    public override string Method => AgentMethods.GetTemplateInfo;

    protected override GetTemplateInfoRequest CreateDefaultRequest() =>
        new();

    protected override void Validate(GetTemplateInfoRequest request) =>
        AgentEndpointValidation.RequireElementTarget(request.Locator, request.ElementId, request.WindowHandle);

    protected override GetTemplateInfoResponse Execute(
        GetTemplateInfoRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        WpfVisualTreeInspector.GetTemplateInfo(request, cancellationToken);
}
