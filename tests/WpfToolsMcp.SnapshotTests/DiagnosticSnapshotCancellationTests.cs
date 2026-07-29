using System.IO.Pipes;
using System.Text.Json.Nodes;
using WpfToolsMcp.Agent;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DiagnosticSnapshotCancellationTests
{
    [Test]
    public async Task Cancelling_an_in_flight_agent_call_cancels_the_server_request_token()
    {
        var pipeName = $"wpf-tools-mcp-agent-cancellation-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        AgentClient? client = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(5), timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(5));
            client = await clientTask;

            using var callCancellation = new CancellationTokenSource();
            var call = client.CallAsync<string>("blocking-test-call", null, callCancellation.Token);
            _ = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);

            using var requestCancellation = new CancellationTokenSource();
            var disconnectTask = AgentServer.MonitorClientDisconnectAsync(
                server,
                requestCancellation,
                timeout.Token);

            callCancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                _ = await call.WaitAsync(TimeSpan.FromSeconds(5)));
            var disconnected = await disconnectTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(disconnected, Is.True);
                Assert.That(requestCancellation.IsCancellationRequested, Is.True);
            });
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task Stopping_the_disconnect_monitor_keeps_a_completed_call_pipe_reusable()
    {
        var pipeName = $"wpf-tools-mcp-agent-cancellation-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        AgentClient? client = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(5), timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(5));
            client = await clientTask;

            var pingCall = client.CallAsync<string>("ping", null, timeout.Token);
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            using var requestCancellation = new CancellationTokenSource();
            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var disconnectTask = AgentServer.MonitorClientDisconnectAsync(
                server,
                requestCancellation,
                monitorCancellation.Token);

            monitorCancellation.Cancel();
            var disconnected = await disconnectTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(disconnected, Is.False);
                Assert.That(requestCancellation.IsCancellationRequested, Is.False);
            });

            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(request.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);
            Assert.That(await pingCall.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("pong"));
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Test]
    public void Coordinator_rechecks_cancellation_after_a_section_returns()
    {
        using var cancellation = new CancellationTokenSource();
        var classifierCalls = 0;
        var clock = TimeProvider.System;
        var startedAtUtc = clock.GetUtcNow();
        var startedTimestamp = clock.GetTimestamp();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            _ = await DiagnosticSnapshotCoordinator.CaptureAsync(
                [DiagnosticSection.Screenshot],
                startedAtUtc,
                startedTimestamp,
                _ => DiagnosticCaptureSource.Screenshot,
                _ => "take_screenshot/v1",
                _ => "screenshot-1",
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(
                        new DiagnosticSectionEvidence(new JsonObject { ["path"] = "captured.png" }));
                },
                exception =>
                {
                    classifierCalls++;
                    return new DiagnosticSectionFailure(
                        DiagnosticSectionStatus.Failed,
                        "incorrectlyClassified",
                        exception.Message);
                },
                cancellation.Token,
                clock));

        Assert.That(classifierCalls, Is.Zero);
    }
}
