using System.IO.Pipes;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class AgentClientLifecycleTests
{
    [Test]
    public async Task Controller_dispose_waits_for_in_flight_work_and_rejects_later_work()
    {
        var controller = new AutomationController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? inFlight = null;
        Task? disposeTask = null;
        try
        {
            inFlight = controller.RunExclusiveAsync(async () =>
            {
                entered.SetResult();
                await release.Task;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = Task.Run(() =>
            {
                disposeEntered.SetResult();
                controller.Dispose();
            });
            await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilControllerDisposalStartsAsync(controller);
            Assert.That(disposeTask.IsCompleted, Is.False);

            release.SetResult();
            await Task.WhenAll(inFlight, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await controller.RunExclusiveAsync(() => Task.CompletedTask));
        }
        finally
        {
            release.TrySetResult();
            if (inFlight is not null && disposeTask is not null)
            {
                await Task.WhenAll(inFlight, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                controller.Dispose();
            }
        }
    }

    [Test]
    public async Task Dispose_interrupts_an_in_flight_agent_call_and_releases_promptly()
    {
        var pipeName = $"wpf-tools-mcp-agent-client-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        AgentClient? client = null;
        try
        {
            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var acceptTask = server.WaitForConnectionAsync(connectionTimeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2), connectionTimeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            var callTask = client.CallAsync<string>("ping", null, CancellationToken.None);
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, requestTimeout.Token);
            Assert.That(request.Method, Is.EqualTo("ping"));

            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = await callTask);
            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                _ = await client.CallAsync<string>("ping", null, CancellationToken.None));

            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var readBuffer = new byte[1];
            var bytesRead = await server.ReadAsync(readBuffer, disconnectTimeout.Token);
            Assert.That(bytesRead, Is.Zero, "The server should observe EOF after the agent client is disposed.");
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
    public async Task Cancel_after_request_write_disconnects_and_rejects_reuse_before_a_late_response()
    {
        var pipeName = $"wpf-tools-mcp-agent-client-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        AgentClient? client = null;
        try
        {
            using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var acceptTask = server.WaitForConnectionAsync(connectionTimeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2), connectionTimeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            using var callCancellation = new CancellationTokenSource();
            var callTask = client.CallAsync<string>("first", null, callCancellation.Token);
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, requestTimeout.Token);
            Assert.That(request.Method, Is.EqualTo("first"));

            callCancellation.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                _ = await callTask.WaitAsync(TimeSpan.FromSeconds(2)));

            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var readBuffer = new byte[1];
            var bytesRead = await server.ReadAsync(readBuffer, disconnectTimeout.Token);
            Assert.That(bytesRead, Is.Zero, "Cancellation after request I/O should poison and disconnect the pipe.");

            var lateWriteException = Assert.CatchAsync(async () =>
                await PipeProtocol.WriteAsync(
                    server,
                    new AgentResponse(request.Id, Ok: true, Result: JsonValue.Create("late")),
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(lateWriteException, Is.TypeOf<IOException>());

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                _ = await client.CallAsync<string>("second", null, CancellationToken.None));

            await server.DisposeAsync();

            await using var recoveryServer = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var recoveryAcceptTask = recoveryServer.WaitForConnectionAsync(recoveryTimeout.Token);
            var recoveredClientTask = AgentClient.ConnectAsync(
                pipeName,
                TimeSpan.FromSeconds(2),
                recoveryTimeout.Token);
            await Task.WhenAll(recoveryAcceptTask, recoveredClientTask).WaitAsync(TimeSpan.FromSeconds(2));
            await using var recoveredClient = await recoveredClientTask;

            var recoveredCall = recoveredClient.CallAsync<string>("recovered", null, CancellationToken.None);
            var recoveryRequest = await PipeProtocol.ReadAsync<AgentRequest>(
                recoveryServer,
                recoveryTimeout.Token);
            Assert.That(recoveryRequest.Method, Is.EqualTo("recovered"));
            await PipeProtocol.WriteAsync(
                    recoveryServer,
                    new AgentResponse(recoveryRequest.Id, Ok: true, Result: JsonValue.Create("ready")),
                    recoveryTimeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(
                await recoveredCall.WaitAsync(TimeSpan.FromSeconds(2)),
                Is.EqualTo("ready"));
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static async Task WaitUntilControllerDisposalStartsAsync(AutomationController controller)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (controller.IsDisposing)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The controller did not enter disposal within the bounded wait.");
    }
}
