namespace WpfToolsMcp.Agent;

internal static class AgentEndpoints
{
    public static AgentEndpointRegistry Create() =>
        new(AgentEndpointPipeline.Build([
            AgentEndpointRegistration.Background(new PingEndpoint()),
            AgentEndpointRegistration.UiThread(new GetVisualTreeEndpoint()),
            AgentEndpointRegistration.Background(new PerformanceStartEndpoint()),
            AgentEndpointRegistration.Background(new PerformanceStopEndpoint()),
            AgentEndpointRegistration.UiThread(new FindElementsEndpoint()),
            AgentEndpointRegistration.UiThread(new GetPathEndpoint()),
            AgentEndpointRegistration.UiThread(new ResolveElementEndpoint()),
            AgentEndpointRegistration.UiThread(new SetValueEndpoint()),
            AgentEndpointRegistration.UiThread(new BringIntoViewEndpoint()),
            AgentEndpointRegistration.UiThread(new ReleaseElementEndpoint()),
            AgentEndpointRegistration.UiThread(new HighlightElementEndpoint()),
            AgentEndpointRegistration.UiThread(new PickElementAtPointEndpoint()),
            AgentEndpointRegistration.UiThread(new GetBindingInfoEndpoint()),
            AgentEndpointRegistration.UiThread(new GetBindingErrorsEndpoint()),
            AgentEndpointRegistration.UiThread(new GetUiaCoverageReportEndpoint()),
            AgentEndpointRegistration.UiThread(new GetDataContextEndpoint()),
            AgentEndpointRegistration.UiThread(new GetComputedPropertiesEndpoint()),
            AgentEndpointRegistration.UiThread(new GetStyleChainEndpoint()),
            AgentEndpointRegistration.UiThread(new GetTemplateInfoEndpoint()),
        ]));
}
