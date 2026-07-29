using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

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
    public async Task Controller_dispose_cancels_lifetime_before_waiting_for_in_flight_work()
    {
        var controller = new AutomationController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = controller.RunExclusiveAsync(async () =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, controller.LifetimeToken);
        });
        Task? disposeTask = null;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            disposeTask = Task.Run(controller.Dispose);

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await inFlight.WaitAsync(TimeSpan.FromSeconds(2)));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(controller.IsDisposing, Is.True);
        }
        finally
        {
            if (!controller.IsDisposing)
            {
                controller.Dispose();
            }

            try
            {
                await inFlight.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }

            if (disposeTask is not null)
            {
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
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

            var queuedCallTask = client.CallAsync<string>("queued", null, CancellationToken.None);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = await callTask);
            Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = await queuedCallTask);
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
    public async Task Remote_error_hides_diagnostics_but_retains_them_for_internal_handling()
    {
        const string method = "wpf/private_operation";
        const string remoteMessage = @"Backend failed at C:\work\secret-project with api-key=message-secret.";
        const string remoteDetails = @"stderr: token=details-secret; source=C:\Users\operator\private.log";
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2), timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            var callTask = client.CallAsync<string>(method, null, timeout.Token);
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(request.Method, Is.EqualTo(method));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    request.Id,
                    Ok: false,
                    Error: new AgentError(remoteMessage, remoteDetails)),
                timeout.Token);

            var exception = Assert.ThrowsAsync<AgentRemoteException>(async () =>
                _ = await callTask.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Is.EqualTo("Agent call failed."));
                Assert.That(exception.Message, Does.Not.Contain("message-secret"));
                Assert.That(exception.Message, Does.Not.Contain("details-secret"));
                Assert.That(exception.Message, Does.Not.Contain(@"C:\work\secret-project"));
                Assert.That(exception.Method, Is.EqualTo(method));
                Assert.That(exception.RemoteMessage, Is.EqualTo(remoteMessage));
                Assert.That(exception.RemoteDetails, Is.EqualTo(remoteDetails));
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

    [Test]
    public async Task Missing_layout_capability_does_not_write_or_poison_agent_pipe()
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2), timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            var layoutCallInvoked = false;
            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                _ = await AutomationController.CallGetLayoutContextWhenSupportedAsync(
                    new AgentCapabilitiesResponse(ProtocolVersion: 0, Capabilities: []),
                    () =>
                    {
                        layoutCallInvoked = true;
                        return client.CallAsync<string>("wpf/get_layout_context", null, CancellationToken.None);
                    }));

            Assert.Multiple(() =>
            {
                Assert.That(layoutCallInvoked, Is.False);
                Assert.That(exception!.Message, Does.Contain("Restart the target application"));
                Assert.That(exception.Message, Does.Contain("start a new MCP session"));
            });

            var pingTask = client.CallAsync<string>("ping", null, CancellationToken.None);
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(request.Method, Is.EqualTo("ping"), "No layout request should reach a capability-missing agent.");
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(request.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);
            Assert.That(await pingTask.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo("pong"));
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
    public async Task Production_computed_properties_path_gates_an_old_agent_before_the_property_pipe_write()
    {
        var pipeName = $"wpf-tools-mcp-agent-client-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        AutomationController? controller = null;
        AgentClient? client = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(2), timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            var capabilitiesTask = AutomationController.VerifyAgentAndGetCapabilitiesAsync(client, timeout.Token);
            var pingRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(pingRequest.Method, Is.EqualTo("ping"));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(pingRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);

            var capabilitiesRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(capabilitiesRequest.Method, Is.EqualTo(AgentProtocolCapabilities.GetCapabilitiesMethod));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    capabilitiesRequest.Id,
                    Ok: false,
                    Error: new AgentError(
                        $"Unknown method '{AgentProtocolCapabilities.GetCapabilitiesMethod}'.")),
                timeout.Token);
            var capabilities = await capabilitiesTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(capabilities.ProtocolVersion, Is.Zero);

            controller = new AutomationController();
            SetPrivateField(
                controller,
                "_application",
                FlaUI.Core.Application.Attach(Environment.ProcessId));
            SetPrivateField(controller, "_agentClient", client);
            SetPrivateField(controller, "_agentPipeName", pipeName);
            SetPrivateField(controller, "_agentPid", (int?)Environment.ProcessId);
            SetPrivateField(controller, "_agentCapabilities", capabilities);

            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                _ = await controller.GetComputedPropertiesAsync(
                    locator: new ElementLocator(AutomationId: "never-sent"),
                    includeProvenance: true,
                    cancellationToken: timeout.Token));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain("Restart the target application"));
                Assert.That(exception.Message, Does.Contain("start a new MCP session"));
            });

            var pingTask = client.CallAsync<string>("ping", null, CancellationToken.None);
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(
                request.Method,
                Is.EqualTo("ping"),
                "No provenance-enabled computed-properties request should reach a capability-missing agent.");
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(request.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);
            Assert.That(await pingTask.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo("pong"));
        }
        finally
        {
            if (controller is not null)
            {
                controller.Dispose();
                client = null;
            }

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

    private static void SetPrivateField<T>(AutomationController controller, string name, T value)
    {
        var field = typeof(AutomationController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Missing AutomationController field '{name}'.");
        field.SetValue(controller, value);
    }
}
