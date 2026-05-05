using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal static class AgentOperations
{
    public static AgentOperationRegistry Create() =>
        new([
            AgentOperation.NoParams(
                AgentMethods.Ping,
                requiresUiThread: false,
                static (_, _) => "pong"),

            AgentOperation.OptionalParams<GetWpfVisualTreeRequestV2, GetVisualTreeResponse>(
                AgentMethods.GetVisualTree,
                requiresUiThread: true,
                static () => new GetWpfVisualTreeRequestV2(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetVisualTree(request, cancellationToken)),

            AgentOperation.OptionalParams<PerformanceStartRequest, PerformanceStartResponse>(
                AgentMethods.PerformanceStart,
                requiresUiThread: false,
                static () => new PerformanceStartRequest(),
                static (request, context, _) => context.UiThreadLatency.Start(context.Dispatcher, request)),

            AgentOperation.RequiredParams<PerformanceStopRequest, PerformanceStopResponse>(
                AgentMethods.PerformanceStop,
                requiresUiThread: false,
                static (request, context, _) => context.UiThreadLatency.Stop(request.RunId),
                ValidatePerformanceStop),

            AgentOperation.OptionalParams<FindElementsWpfRequest, FindElementsResponse>(
                AgentMethods.FindElements,
                requiresUiThread: true,
                static () => new FindElementsWpfRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.FindElements(request, cancellationToken)),

            AgentOperation.OptionalParams<GetWpfPathRequest, GetPathToElementResponse>(
                AgentMethods.GetPath,
                requiresUiThread: true,
                static () => new GetWpfPathRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetPath(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<ResolveWpfElementRequest, ElementRef>(
                AgentMethods.ResolveElement,
                requiresUiThread: true,
                static () => new ResolveWpfElementRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.ResolveElement(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<SetWpfValueRequest, SetValueResponse>(
                AgentMethods.SetValue,
                requiresUiThread: true,
                static () => new SetWpfValueRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.SetValue(request, cancellationToken),
                ValidateSetValue),

            AgentOperation.RequiredParams<BringIntoViewWpfRequest, BringIntoViewWpfResponse>(
                AgentMethods.BringIntoView,
                requiresUiThread: true,
                static (request, _, cancellationToken) => WpfVisualTreeInspector.BringIntoView(request, cancellationToken),
                ValidateBringIntoView),

            AgentOperation.RequiredParams<ReleaseWpfElementRequest, ReleaseElementResponse>(
                AgentMethods.ReleaseElement,
                requiresUiThread: true,
                static (request, _, _) => WpfVisualTreeInspector.ReleaseElement(request),
                ValidateReleaseElement),

            AgentOperation.OptionalParams<HighlightWpfElementRequest, HighlightWpfElementResponse>(
                AgentMethods.HighlightElement,
                requiresUiThread: true,
                static () => new HighlightWpfElementRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.HighlightElement(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<PickWpfElementAtPointRequest, PickWpfElementAtPointResponse>(
                AgentMethods.PickElementAtPoint,
                requiresUiThread: true,
                static () => new PickWpfElementAtPointRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.PickElementAtPoint(request, cancellationToken)),

            AgentOperation.OptionalParams<GetBindingInfoRequest, GetBindingInfoResponse>(
                AgentMethods.GetBindingInfo,
                requiresUiThread: true,
                static () => new GetBindingInfoRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetBindingInfo(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<GetBindingErrorsRequest, GetBindingErrorsResponse>(
                AgentMethods.GetBindingErrors,
                requiresUiThread: true,
                static () => new GetBindingErrorsRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetBindingErrors(request, cancellationToken)),

            AgentOperation.OptionalParams<GetUiaCoverageReportRequest, GetUiaCoverageReportResponse>(
                AgentMethods.GetUiaCoverageReport,
                requiresUiThread: true,
                static () => new GetUiaCoverageReportRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetUiaCoverageReport(request, cancellationToken)),

            AgentOperation.OptionalParams<GetDataContextRequest, GetDataContextResponse>(
                AgentMethods.GetDataContext,
                requiresUiThread: true,
                static () => new GetDataContextRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetDataContext(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<GetComputedPropertiesRequest, GetComputedPropertiesResponse>(
                AgentMethods.GetComputedProperties,
                requiresUiThread: true,
                static () => new GetComputedPropertiesRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetComputedProperties(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<GetStyleChainRequest, GetStyleChainResponse>(
                AgentMethods.GetStyleChain,
                requiresUiThread: true,
                static () => new GetStyleChainRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetStyleChain(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),

            AgentOperation.OptionalParams<GetTemplateInfoRequest, GetTemplateInfoResponse>(
                AgentMethods.GetTemplateInfo,
                requiresUiThread: true,
                static () => new GetTemplateInfoRequest(),
                static (request, _, cancellationToken) => WpfVisualTreeInspector.GetTemplateInfo(request, cancellationToken),
                static request => ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle)),
        ]);

    private static void ValidatePerformanceStop(PerformanceStopRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw AgentOperationException.InvalidRequest("invalid_request: runId is required.");
        }
    }

    private static void ValidateSetValue(SetWpfValueRequest request)
    {
        ValidateElementTarget(request.Locator, request.ElementId, request.WindowHandle);

        var hasText = request.Text is not null;
        var hasValue = request.Value.HasValue;
        if (hasText == hasValue)
        {
            throw AgentOperationException.InvalidRequest("invalid_request: set_value requires exactly one of text OR value.");
        }
    }

    private static void ValidateBringIntoView(BringIntoViewWpfRequest request)
    {
        if (request.WindowHandle == 0)
        {
            throw AgentOperationException.InvalidRequest("invalid_request: windowHandle is required.");
        }

        var hasXPath = !string.IsNullOrWhiteSpace(request.XPath);
        var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
        if (hasXPath == hasElementId)
        {
            throw AgentOperationException.InvalidRequest("invalid_request: provide exactly one of elementId OR xpath.");
        }
    }

    private static void ValidateReleaseElement(ReleaseWpfElementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ElementId))
        {
            throw AgentOperationException.InvalidRequest("invalid_request: elementId is required.");
        }
    }

    private static void ValidateElementTarget(ElementLocator? locator, string? elementId, long? windowHandle)
    {
        var hasLocator = locator is not null;
        var hasElementId = !string.IsNullOrWhiteSpace(elementId);
        if (hasLocator == hasElementId)
        {
            throw AgentOperationException.InvalidRequest("invalid_request: provide exactly one of locator OR elementId.");
        }

        if (hasElementId && windowHandle is not > 0)
        {
            throw AgentOperationException.InvalidRequest("invalid_request: windowHandle is required with elementId.");
        }
    }
}
