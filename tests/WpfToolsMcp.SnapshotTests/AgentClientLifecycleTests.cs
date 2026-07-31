using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class AgentClientLifecycleTests
{
    [Test]
    public void Connect_timeout_is_distinct_from_caller_cancellation()
    {
        var unavailablePipe = $"wpf-tools-mcp-unavailable-{Guid.NewGuid():N}";
        var timeout = Assert.ThrowsAsync<TimeoutException>(async () =>
            _ = await AgentClient.ConnectAsync(
                unavailablePipe,
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        Assert.Multiple(() =>
        {
            Assert.That(timeout!.Message, Does.Contain("timed out"));
            Assert.That(timeout.Message, Does.Not.Contain(unavailablePipe));
            Assert.CatchAsync<OperationCanceledException>(async () =>
                _ = await AgentClient.ConnectAsync(
                    unavailablePipe,
                    TimeSpan.FromSeconds(1),
                    callerCancellation.Token));
        });
    }

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
    public async Task Remote_error_preserves_diagnostics_for_local_callers()
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
                Assert.That(exception!.Message, Is.EqualTo(remoteMessage));
                Assert.That(exception.Message, Does.Contain("message-secret"));
                Assert.That(exception.Message, Does.Not.Contain("details-secret"));
                Assert.That(exception.Message, Does.Contain(@"C:\work\secret-project"));
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

    [TestCase("performance_already_running: runId=private-run", "performance_already_running")]
    [TestCase("performance_not_running", "performance_not_running")]
    [TestCase("performance_run_not_owned", "performance_run_not_owned")]
    [TestCase("performance_run_id_mismatch: activeRunId=private-run", "performance_run_id_mismatch")]
    [TestCase("performance_stop_failed", "performance_stop_failed")]
    public async Task Performance_agent_errors_keep_stable_codes_and_preserve_remote_causes(
        string remoteMessage,
        string expectedCode)
    {
        const string remoteDetails = @"stderr: token=details-secret; source=C:\Users\operator\private.log";

        var exception = await CapturePerformanceAgentErrorAsync(remoteMessage, remoteDetails);

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Is.EqualTo(expectedCode));
            Assert.That(exception.Message, Does.Not.Contain("private-run"));
            Assert.That(exception.Message, Does.Not.Contain("details-secret"));
            Assert.That(exception.InnerException, Is.TypeOf<AgentRemoteException>());
            Assert.That(exception.InnerException!.Message, Is.EqualTo(remoteMessage));
            Assert.That(((AgentRemoteException)exception.InnerException).RemoteDetails, Is.EqualTo(remoteDetails));
        });
    }

    [Test]
    public async Task Performance_agent_errors_preserve_unrecognized_remote_content()
    {
        const string remoteMessage = @"performance_private_failure: C:\work\secret-project api-key=message-secret";
        const string remoteDetails = @"stderr: token=details-secret; source=C:\Users\operator\private.log";

        var exception = await CapturePerformanceAgentErrorAsync(remoteMessage, remoteDetails);

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Is.EqualTo(remoteMessage));
            Assert.That(exception.Message, Does.Contain("message-secret"));
            Assert.That(exception.Message, Does.Not.Contain("details-secret"));
            Assert.That(exception.Message, Does.Contain(@"C:\work\secret-project"));
        });
    }

    [Test]
    public async Task Capability_handshake_rejects_an_incompatible_positive_protocol_version()
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

            var verifyTask = AutomationController.VerifyAgentAndGetCapabilitiesAsync(client, timeout.Token);
            var pingRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(pingRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);

            var capabilitiesRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            var incompatible = new AgentCapabilitiesResponse(
                AgentProtocolCapabilities.CurrentProtocolVersion + 1,
                []);
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    capabilitiesRequest.Id,
                    Ok: true,
                    Result: JsonSerializer.SerializeToNode(incompatible)),
                timeout.Token);

            var exception = Assert.ThrowsAsync<ActionableFailureException>(async () =>
                _ = await verifyTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Multiple(() =>
            {
                Assert.That(exception!.Failure.Code, Is.EqualTo("protocol_mismatch"));
                Assert.That(exception.Failure.Stage, Is.EqualTo("protocol"));
                Assert.That(exception.Failure.Retryable, Is.False);
                Assert.That(exception.Message, Does.Not.Contain((AgentProtocolCapabilities.CurrentProtocolVersion + 1).ToString()));
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

    [Test]
    public async Task Auto_wpf_request_failure_is_local_and_caller_cancellation_is_propagated()
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
            using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptTask = server.WaitForConnectionAsync(testTimeout.Token);
            var clientTask = AgentClient.ConnectAsync(
                pipeName,
                TimeSpan.FromSeconds(2),
                testTimeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            controller = new AutomationController();
            SetPrivateField(
                controller,
                "_application",
                FlaUI.Core.Application.Attach(Environment.ProcessId));
            SetPrivateField(controller, "_agentClient", client);
            SetPrivateField(controller, "_agentPipeName", pipeName);
            SetPrivateField(controller, "_agentPid", (int?)Environment.ProcessId);

            var capabilities = new AgentCapabilitiesResponse(ProtocolVersion: 1, Capabilities: []);
            var failedCall = controller.TryGetVisualTreeWpfAsync(
                new GetWpfVisualTreeRequestV2(),
                testTimeout.Token,
                autoInject: true);

            await RespondToHandshakeAsync(capabilities);
            var failedTreeRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, testTimeout.Token);
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    failedTreeRequest.Id,
                    Ok: false,
                    Error: new AgentError(
                        "The requested operation failed.",
                        "private target stack")),
                testTimeout.Token);

            var failedAttempt = await failedCall.WaitAsync(TimeSpan.FromSeconds(2));
            var healthyCapability = controller.GetWpfBackendCapabilityState();
            Assert.Multiple(() =>
            {
                Assert.That(failedAttempt.Response, Is.Null);
                Assert.That(failedAttempt.Attempted, Is.True);
                Assert.That(failedAttempt.Failure?.Code, Is.EqualTo("backend_operation_failed"));
                Assert.That(failedAttempt.Failure?.Retryable, Is.Null);
                Assert.That(healthyCapability.State, Is.EqualTo("ready"));
                Assert.That(healthyCapability.Failure, Is.Null);
            });

            using var callerCancellation = new CancellationTokenSource();
            var canceledCall = controller.TryGetVisualTreeWpfAsync(
                new GetWpfVisualTreeRequestV2(),
                callerCancellation.Token,
                autoInject: true);
            await RespondToHandshakeAsync(capabilities);
            var canceledTreeRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, testTimeout.Token);
            Assert.That(canceledTreeRequest.Method, Is.EqualTo("wpf/get_visual_tree"));
            callerCancellation.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                _ = await canceledCall.WaitAsync(TimeSpan.FromSeconds(2)));
            var capability = controller.GetWpfBackendCapabilityState();
            Assert.Multiple(() =>
            {
                Assert.That(capability.State, Is.EqualTo("not_initialized"));
                Assert.That(capability.Failure, Is.Null);
            });

            async Task RespondToHandshakeAsync(AgentCapabilitiesResponse response)
            {
                var pingRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, testTimeout.Token);
                Assert.That(pingRequest.Method, Is.EqualTo("ping"));
                await PipeProtocol.WriteAsync(
                    server,
                    new AgentResponse(pingRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
                    testTimeout.Token);

                var capabilitiesRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, testTimeout.Token);
                Assert.That(
                    capabilitiesRequest.Method,
                    Is.EqualTo(AgentProtocolCapabilities.GetCapabilitiesMethod));
                await PipeProtocol.WriteAsync(
                    server,
                    new AgentResponse(
                        capabilitiesRequest.Id,
                        Ok: true,
                        Result: JsonSerializer.SerializeToNode(response)),
                    testTimeout.Token);
            }
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

    [Test]
    public async Task Malformed_auto_wpf_result_marks_the_backend_unavailable()
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(
                pipeName,
                TimeSpan.FromSeconds(2),
                timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            controller = new AutomationController();
            SetPrivateField(
                controller,
                "_application",
                FlaUI.Core.Application.Attach(Environment.ProcessId));
            SetPrivateField(controller, "_agentClient", client);
            SetPrivateField(controller, "_agentPipeName", pipeName);
            SetPrivateField(controller, "_agentPid", (int?)Environment.ProcessId);

            var call = controller.TryGetVisualTreeWpfAsync(
                new GetWpfVisualTreeRequestV2(),
                timeout.Token,
                autoInject: true);

            var pingRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(pingRequest.Method, Is.EqualTo("ping"));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(pingRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);

            var capabilitiesRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(
                capabilitiesRequest.Method,
                Is.EqualTo(AgentProtocolCapabilities.GetCapabilitiesMethod));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    capabilitiesRequest.Id,
                    Ok: true,
                    Result: JsonSerializer.SerializeToNode(
                        new AgentCapabilitiesResponse(
                            AgentProtocolCapabilities.CurrentProtocolVersion,
                            []))),
                timeout.Token);

            var treeRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(treeRequest.Method, Is.EqualTo("wpf/get_visual_tree"));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    treeRequest.Id,
                    Ok: true,
                    Result: JsonValue.Create("not-a-visual-tree-response")),
                timeout.Token);

            var attempt = await call.WaitAsync(TimeSpan.FromSeconds(2));
            var capability = controller.GetWpfBackendCapabilityState();
            Assert.Multiple(() =>
            {
                Assert.That(attempt.Response, Is.Null);
                Assert.That(attempt.Attempted, Is.True);
                Assert.That(attempt.Failure?.Code, Is.EqualTo("protocol_error"));
                Assert.That(capability.State, Is.EqualTo("unavailable"));
                Assert.That(capability.Failure?.Code, Is.EqualTo("protocol_error"));
            });
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

    [Test]
    public async Task Legacy_agent_ambiguity_keeps_structured_metadata_and_remote_diagnostics()
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(
                pipeName,
                TimeSpan.FromSeconds(2),
                timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            var identity = ProcessTargetResolver.ResolveByPid(Environment.ProcessId).Identity;
            controller = new AutomationController();
            SetPrivateField(
                controller,
                "_application",
                FlaUI.Core.Application.Attach(Environment.ProcessId));
            SetPrivateField(controller, "_processIdentity", (ProcessInstanceIdentity?)identity);
            SetPrivateField(controller, "_agentClient", client);
            SetPrivateField(controller, "_agentPipeName", pipeName);
            SetPrivateField(controller, "_agentPid", (int?)Environment.ProcessId);
            SetPrivateField(
                controller,
                "_agentCapabilities",
                new AgentCapabilitiesResponse(
                    AgentProtocolCapabilities.CurrentProtocolVersion,
                    []));

            const long windowHandle = 42;
            var resolveTask = controller.ResolveWpfElementRefDetailedAsync(
                new ElementLocator(Name: "Save", Strict: true),
                windowHandle,
                visibleOnly: true,
                includeOffViewport: true,
                interactiveOnly: false,
                InteractiveMode.Heuristic,
                timeout.Token);
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(request.Method, Is.EqualTo("wpf/resolve_element"));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    request.Id,
                    Ok: false,
                    Error: new AgentError(
                        @"wpf_resolve:ambiguous: Locator is ambiguous (found 4). Secret C:\work\customer",
                        "private target stack")),
                timeout.Token);

            var exception = Assert.ThrowsAsync<ElementResolutionAmbiguityException>(async () =>
                _ = await resolveTask.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Ambiguity.Code, Is.EqualTo("ambiguous_element"));
                Assert.That(exception.Ambiguity.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
                Assert.That(exception.Ambiguity.WindowHandleUsed, Is.EqualTo(windowHandle));
                Assert.That(exception.Ambiguity.ReturnedCandidates, Is.Zero);
                Assert.That(exception.Ambiguity.DiscoveredCandidates, Is.EqualTo(4));
                Assert.That(exception.Ambiguity.Truncated, Is.True);
                Assert.That(exception.Ambiguity.TruncatedReason, Is.EqualTo("legacyAgent"));
                Assert.That(exception.Message, Does.Not.Contain("customer"));
                Assert.That(exception.Message, Does.Not.Contain("private target stack"));
                Assert.That(exception.InnerException, Is.TypeOf<AgentRemoteException>());
                Assert.That(exception.InnerException!.Message, Does.Contain(@"C:\work\customer"));
                Assert.That(((AgentRemoteException)exception.InnerException).RemoteDetails, Is.EqualTo("private target stack"));
            });
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

    [Test]
    public async Task Agent_handshake_revalidates_the_attached_process_before_reporting_ready()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-handshake-{Guid.NewGuid():N}.log");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = TestAppPaths.FindLifecycleProbeTestAppExecutable(),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--marker-path");
        startInfo.ArgumentList.Add(markerPath);

        using var target = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start lifecycle probe.");
        var identity = ProcessTargetResolver.ResolveByPid(target.Id).Identity;
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptTask = server.WaitForConnectionAsync(timeout.Token);
            var clientTask = AgentClient.ConnectAsync(
                pipeName,
                TimeSpan.FromSeconds(2),
                timeout.Token);
            await Task.WhenAll(acceptTask, clientTask).WaitAsync(TimeSpan.FromSeconds(2));
            client = await clientTask;

            using var controller = new AutomationController();
            SetPrivateField(controller, "_processIdentity", (ProcessInstanceIdentity?)identity);
            var verifyTask = controller.VerifyAgentForAttachedProcessAsync(
                client,
                target.Id,
                timeout.Token);

            var pingRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(pingRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
                timeout.Token);
            var capabilitiesRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);

            target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(2));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    capabilitiesRequest.Id,
                    Ok: true,
                    Result: JsonSerializer.SerializeToNode(
                        new AgentCapabilitiesResponse(
                            AgentProtocolCapabilities.CurrentProtocolVersion,
                            []))),
                timeout.Token);

            var exception = Assert.ThrowsAsync<ActionableFailureException>(async () =>
                _ = await verifyTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Multiple(() =>
            {
                Assert.That(exception!.Failure.Code, Is.EqualTo("target_exited"));
                Assert.That(exception.Failure.Stage, Is.EqualTo("target_shutdown"));
            });
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                _ = target.WaitForExit(2_000);
            }

            if (client is not null)
            {
                await client.DisposeAsync();
            }

            try
            {
                File.Delete(markerPath);
            }
            catch
            {
            }
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Passive_agent_refresh_clears_a_transient_unavailable_failure_after_reconnect()
    {
        var identity = ProcessTargetResolver.ResolveByPid(Environment.ProcessId).Identity;
        var pipeName = AgentPipeName.Compute(identity);
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using var controller = new AutomationController();
        SetPrivateField(
            controller,
            "_application",
            FlaUI.Core.Application.Attach(Environment.ProcessId));
        SetPrivateField(controller, "_processIdentity", (ProcessInstanceIdentity?)identity);
        SetPrivateField(controller, "_agentAutoConnectFailure", FailureDiagnostics.PipeFailure());
        SetPrivateField(
            controller,
            "_agentAutoConnectFailureAtUtc",
            (DateTimeOffset?)DateTimeOffset.UtcNow);

        Assert.That(controller.GetWpfBackendCapabilityState().State, Is.EqualTo("unavailable"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptTask = server.WaitForConnectionAsync(timeout.Token);
        var refreshTask = controller.RefreshWpfBackendCapabilityAsync(timeout.Token);
        await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));

        var pingRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
        await PipeProtocol.WriteAsync(
            server,
            new AgentResponse(pingRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
            timeout.Token);
        var capabilitiesRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
        await PipeProtocol.WriteAsync(
            server,
            new AgentResponse(
                capabilitiesRequest.Id,
                Ok: true,
                Result: JsonSerializer.SerializeToNode(
                    new AgentCapabilitiesResponse(
                        AgentProtocolCapabilities.CurrentProtocolVersion,
                        []))),
            timeout.Token);

        Assert.That(await refreshTask.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
        var capability = controller.GetWpfBackendCapabilityState();
        Assert.Multiple(() =>
        {
            Assert.That(capability.State, Is.EqualTo("ready"));
            Assert.That(capability.Failure, Is.Null);
        });
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

    private static async Task<InvalidOperationException> CapturePerformanceAgentErrorAsync(
        string remoteMessage,
        string? remoteDetails)
    {
        var pipeName = $"wpf-tools-mcp-performance-error-test-{Guid.NewGuid():N}";
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

            var callTask = AutomationController.CallPerformanceAgentAsync(
                () => client.CallAsync<string>("wpf/performance_stop", null, timeout.Token));
            var request = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(request.Method, Is.EqualTo("wpf/performance_stop"));
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    request.Id,
                    Ok: false,
                    Error: new AgentError(remoteMessage, remoteDetails)),
                timeout.Token);

            return Assert.CatchAsync<InvalidOperationException>(async () =>
                       _ = await callTask.WaitAsync(TimeSpan.FromSeconds(2)))
                   ?? throw new AssertionException("Expected the performance agent call to fail.");
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static void SetPrivateField<T>(AutomationController controller, string name, T value)
    {
        var field = typeof(AutomationController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Missing AutomationController field '{name}'.");
        field.SetValue(controller, value);
    }
}
