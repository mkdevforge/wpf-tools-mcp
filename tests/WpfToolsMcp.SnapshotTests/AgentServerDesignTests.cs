using System.IO;
using WpfToolsMcp.Agent;
using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class AgentServerDesignTests
{
    [Test]
    public async Task HandleAsync_unknown_method_returns_typed_error()
    {
        var response = await AgentServer.HandleAsync(
            new AgentRequest("request-1", "missing/method"),
            CancellationToken.None);

        Assert.That(response.Ok, Is.False);
        Assert.That(response.Error?.Code, Is.EqualTo(AgentErrorCodes.UnknownMethod));
    }

    [Test]
    public async Task Required_endpoint_reports_missing_params_before_execution()
    {
        var registry = AgentEndpoints.Create();
        Assert.That(registry.TryGet(AgentMethods.ReleaseElement, out var endpoint), Is.True);

        var context = new AgentEndpointContext(new UiThreadLatencyRecorder());
        var invocation = endpoint.Bind(new AgentRequest("request-1", AgentMethods.ReleaseElement));
        var response = await invocation.ExecuteAsync(context, CancellationToken.None);

        Assert.That(response.Ok, Is.False);
        Assert.That(response.Error?.Code, Is.EqualTo(AgentErrorCodes.MissingParams));
    }

    [Test]
    public void AgentResponses_maps_wpf_errors_to_protocol_codes()
    {
        var notFound = AgentResponses.FromException(
            "request-1",
            new InvalidOperationException("wpf_resolve:not_found: Locator did not match any elements."));
        var stale = AgentResponses.FromException(
            "request-2",
            new InvalidOperationException("wpf_handle_stale:not_found: '<element>'."));

        Assert.That(notFound.Error?.Code, Is.EqualTo(AgentErrorCodes.WpfResolveNotFound));
        Assert.That(stale.Error?.Code, Is.EqualTo(AgentErrorCodes.WpfHandleStale));
    }

    [Test]
    public void Connection_tasks_do_not_retain_completed_tasks()
    {
        var tasks = new AgentConnectionTasks();

        tasks.Track(Task.CompletedTask);
        tasks.Track(Task.FromException(new InvalidOperationException("connection failed")));

        Assert.That(tasks.Count, Is.Zero);
    }

    [Test]
    public void Pipe_retry_policy_retries_only_transient_io_failures()
    {
        Assert.That(AgentPipeRetryPolicy.CanRetry(new IOException("temporary pipe failure")), Is.True);
        Assert.That(AgentPipeRetryPolicy.CanRetry(new UnauthorizedAccessException("bad pipe permissions")), Is.False);
        Assert.That(
            AgentPipeRetryPolicy.NextDelay(AgentPipeRetryPolicy.InitialDelay),
            Is.GreaterThan(AgentPipeRetryPolicy.InitialDelay));
    }
}
