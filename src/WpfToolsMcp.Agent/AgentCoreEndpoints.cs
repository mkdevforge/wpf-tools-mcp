using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Agent;

internal sealed class PingEndpoint : AgentEndpoint<string>
{
    public override string Method => AgentMethods.Ping;

    protected override string Execute(AgentEndpointContext context, CancellationToken cancellationToken) =>
        "pong";
}

internal sealed class PerformanceStartEndpoint : AgentEndpoint<PerformanceStartRequest, PerformanceStartResponse>
{
    public override string Method => AgentMethods.PerformanceStart;

    protected override PerformanceStartRequest CreateDefaultRequest() =>
        new();

    protected override PerformanceStartResponse Execute(
        PerformanceStartRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        context.UiThreadLatency.Start(context.Dispatcher, request);
}

internal sealed class PerformanceStopEndpoint : AgentEndpoint<PerformanceStopRequest, PerformanceStopResponse>
{
    public override string Method => AgentMethods.PerformanceStop;

    protected override void Validate(PerformanceStopRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw AgentEndpointException.InvalidRequest("invalid_request: runId is required.");
        }
    }

    protected override PerformanceStopResponse Execute(
        PerformanceStopRequest request,
        AgentEndpointContext context,
        CancellationToken cancellationToken) =>
        context.UiThreadLatency.Stop(request.RunId);
}
