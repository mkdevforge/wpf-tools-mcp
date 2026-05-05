using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.Agent;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;

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
    public void AgentResponses_maps_coded_wpf_errors_to_protocol_codes()
    {
        var notFound = AgentResponses.FromException(
            "request-1",
            AgentEndpointException.WpfResolveNotFound("Locator did not match any elements."));
        var ambiguous = AgentResponses.FromException(
            "request-2",
            AgentEndpointException.WpfResolveAmbiguous("Locator is ambiguous."));
        var stale = AgentResponses.FromException(
            "request-3",
            AgentEndpointException.WpfHandleStale("Element handle is stale."));
        var invalid = AgentResponses.FromException(
            "request-4",
            AgentEndpointException.InvalidRequest("Provide exactly one target."));

        Assert.That(notFound.Error?.Code, Is.EqualTo(AgentErrorCodes.WpfResolveNotFound));
        Assert.That(ambiguous.Error?.Code, Is.EqualTo(AgentErrorCodes.WpfResolveAmbiguous));
        Assert.That(stale.Error?.Code, Is.EqualTo(AgentErrorCodes.WpfHandleStale));
        Assert.That(invalid.Error?.Code, Is.EqualTo(AgentErrorCodes.InvalidRequest));
    }

    [Test]
    public void AgentResponses_does_not_infer_migrated_wpf_codes_from_message_prefixes()
    {
        var notFound = AgentResponses.FromException(
            "request-1",
            new InvalidOperationException("wpf_resolve:not_found: Locator did not match any elements."));
        var ambiguous = AgentResponses.FromException(
            "request-2",
            new InvalidOperationException("wpf_resolve:ambiguous: Locator is ambiguous."));
        var stale = AgentResponses.FromException(
            "request-3",
            new InvalidOperationException("wpf_handle_stale:not_found: '<element>'."));

        Assert.That(notFound.Error?.Code, Is.EqualTo(AgentErrorCodes.OperationFailed));
        Assert.That(ambiguous.Error?.Code, Is.EqualTo(AgentErrorCodes.OperationFailed));
        Assert.That(stale.Error?.Code, Is.EqualTo(AgentErrorCodes.OperationFailed));
    }

    [Test]
    public void AgentResponse_factories_preserve_wire_shape()
    {
        var success = AgentResponse.Success("request-1", new JsonObject { ["value"] = 42 });
        var failure = AgentResponse.Failure(
            "request-2",
            new AgentError("failed", "details", AgentErrorCodes.OperationFailed));

        var successJson = JsonSerializer.Serialize(success);
        var failureJson = JsonSerializer.Serialize(failure);

        Assert.That(success.Ok, Is.True);
        Assert.That(success.Result?["value"]?.GetValue<int>(), Is.EqualTo(42));
        Assert.That(success.Error, Is.Null);
        Assert.That(successJson, Does.Contain("\"Id\":\"request-1\""));
        Assert.That(successJson, Does.Contain("\"Ok\":true"));
        Assert.That(successJson, Does.Contain("\"Result\""));
        Assert.That(successJson, Does.Contain("\"Error\":null"));

        Assert.That(failure.Ok, Is.False);
        Assert.That(failure.Result, Is.Null);
        Assert.That(failure.Error?.Code, Is.EqualTo(AgentErrorCodes.OperationFailed));
        Assert.That(failureJson, Does.Contain("\"Id\":\"request-2\""));
        Assert.That(failureJson, Does.Contain("\"Ok\":false"));
        Assert.That(failureJson, Does.Contain("\"Result\":null"));
        Assert.That(failureJson, Does.Contain("\"Error\""));
    }

    [Test]
    public void AgentResponse_rejects_invalid_states()
    {
        Assert.That(
            () => new AgentResponse("request-1", true, new JsonObject(), new AgentError("failed")),
            NUnit.Framework.Throws.ArgumentException.With.Message.Contains("successful response cannot include an error"));

        Assert.That(
            () => new AgentResponse("request-2", false),
            NUnit.Framework.Throws.ArgumentException.With.Message.Contains("failed response must include an error"));

        Assert.That(
            () => new AgentResponse("request-3", false, new JsonObject(), new AgentError("failed")),
            NUnit.Framework.Throws.ArgumentException.With.Message.Contains("failed response cannot include a result"));
    }

    [Test]
    public void AgentResponse_deserialization_rejects_invalid_wire_shape()
    {
        const string json = """
            {
              "Id": "request-1",
              "Ok": false,
              "Result": { "value": 42 },
              "Error": null
            }
            """;

        Assert.That(
            () => JsonSerializer.Deserialize<AgentResponse>(json),
            NUnit.Framework.Throws.Exception);
    }

    [Test]
    public void AgentClient_validates_response_id_and_typed_failure()
    {
        var mismatch = AgentResponse.Success("response-2", new JsonObject { ["value"] = 42 });

        Assert.That(
            () => AgentClient.ReadResultOrThrow("method", "request-1", mismatch),
            NUnit.Framework.Throws.InvalidOperationException.With.Message.EqualTo("Agent protocol error: response ID mismatch."));

        var failure = AgentResponse.Failure(
            "request-1",
            new AgentError("failed", "details", AgentErrorCodes.OperationFailed));

        var ex = Assert.Throws<AgentCallException>(
            () => AgentClient.ReadResultOrThrow("method", "request-1", failure));

        Assert.That(ex?.Code, Is.EqualTo(AgentErrorCodes.OperationFailed));
        Assert.That(ex?.Details, Is.EqualTo("details"));
        Assert.That(ex?.Message, Does.Contain("failed"));
        Assert.That(ex?.Message, Does.Contain("details"));
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
