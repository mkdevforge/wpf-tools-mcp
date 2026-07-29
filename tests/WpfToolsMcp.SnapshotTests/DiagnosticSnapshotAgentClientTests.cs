using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpfToolsMcp.AgentProtocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class DiagnosticSnapshotAgentClientTests
{
    [Test]
    public async Task Multi_section_snapshot_uses_one_agent_call_and_round_trips_response()
    {
        var pipeName = $"wpf-tools-mcp-diagnostic-snapshot-test-{Guid.NewGuid():N}";
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

            var snapshotRequest = new CaptureWpfDiagnosticSnapshotRequest(
                WindowHandle: 0x1234,
                Locator: null,
                ElementId: "agent-element-42",
                RootXPath: "/Window/Button[1]",
                Sections:
                [
                    DiagnosticSection.VisualTree,
                    DiagnosticSection.WpfProperties,
                    DiagnosticSection.Layout,
                    DiagnosticSection.Bindings,
                    DiagnosticSection.DataContext,
                    DiagnosticSection.BindingErrors
                ],
                Budget: new DiagnosticSnapshotBudget(
                    MaxDepth: 4,
                    MaxItems: 30,
                    MaxNodes: 250,
                    MaxValueLength: 800,
                    MaxPayloadChars: 24_000),
                PropertyNames: ["IsEnabled", "Visibility"],
                DataContextProperties: ["Name", "Status"]);
            var startedAt = new DateTimeOffset(2026, 7, 29, 8, 30, 0, TimeSpan.Zero);
            var expectedResponse = new CaptureWpfDiagnosticSnapshotResponse(
                Target: new ElementRef(
                    Type: "Button",
                    AutomationId: "SaveButton",
                    Name: "Save",
                    XPath: "/Window/Button[1]",
                    ClassName: "Button",
                    Bounds: new Rect(10, 20, 120, 32),
                    ElementIdWpf: "agent-element-42"),
                StartedAtUtc: startedAt,
                CompletedAtUtc: startedAt.AddMilliseconds(7),
                Sections:
                [
                    new DiagnosticSectionResult(
                        Section: DiagnosticSection.VisualTree,
                        Status: DiagnosticSectionStatus.Success,
                        Source: DiagnosticCaptureSource.WpfDispatcher,
                        EvidenceSchema: "get_visual_tree/v1",
                        CaptureGroup: "wpf-dispatcher-1",
                        StartedAtUtc: startedAt,
                        CompletedAtUtc: startedAt.AddMilliseconds(3),
                        StartedOffsetMs: 0,
                        CompletedOffsetMs: 3,
                        DurationMs: 3,
                        Data: new JsonObject { ["returnedNodes"] = 2 },
                        PayloadChars: 19),
                    new DiagnosticSectionResult(
                        Section: DiagnosticSection.Bindings,
                        Status: DiagnosticSectionStatus.Truncated,
                        Source: DiagnosticCaptureSource.WpfDispatcher,
                        EvidenceSchema: "get_binding_info/v1",
                        CaptureGroup: "wpf-dispatcher-1",
                        StartedAtUtc: startedAt.AddMilliseconds(3),
                        CompletedAtUtc: startedAt.AddMilliseconds(7),
                        StartedOffsetMs: 3,
                        CompletedOffsetMs: 7,
                        DurationMs: 4,
                        Data: new JsonObject { ["returnedBindings"] = 30 },
                        Code: "maxItems",
                        PayloadChars: 23)
                ]);

            var delegateCalls = 0;
            var snapshotCall = AutomationController.CallCaptureDiagnosticSnapshotWhenSupportedAsync(
                new AgentCapabilitiesResponse(
                    AgentProtocolCapabilities.CurrentProtocolVersion,
                    [AgentProtocolCapabilities.CaptureDiagnosticSnapshot]),
                () =>
                {
                    delegateCalls++;
                    return client.CallAsync<CaptureWpfDiagnosticSnapshotResponse>(
                        AgentProtocolCapabilities.CaptureDiagnosticSnapshot,
                        snapshotRequest,
                        timeout.Token);
                });

            var pipeRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.Multiple(() =>
            {
                Assert.That(delegateCalls, Is.EqualTo(1));
                Assert.That(pipeRequest.Method, Is.EqualTo(AgentProtocolCapabilities.CaptureDiagnosticSnapshot));
                Assert.That(
                    JsonNode.DeepEquals(
                        pipeRequest.Params,
                        JsonSerializer.SerializeToNode(snapshotRequest)),
                    Is.True,
                    "All requested sections must travel in one agent request.");
            });

            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(
                    pipeRequest.Id,
                    Ok: true,
                    Result: JsonSerializer.SerializeToNode(expectedResponse)),
                timeout.Token);
            var actualResponse = await snapshotCall.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(
                JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(actualResponse),
                    JsonSerializer.SerializeToNode(expectedResponse)),
                Is.True);

            var pingCall = client.CallAsync<string>("ping", null, timeout.Token);
            var nextRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(
                nextRequest.Method,
                Is.EqualTo("ping"),
                "The snapshot call must write exactly one pipe request.");
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(nextRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
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
    public async Task Missing_snapshot_capability_does_not_invoke_delegate_or_poison_pipe()
    {
        var pipeName = $"wpf-tools-mcp-diagnostic-snapshot-test-{Guid.NewGuid():N}";
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

            var delegateCalls = 0;
            var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                _ = await AutomationController.CallCaptureDiagnosticSnapshotWhenSupportedAsync(
                    new AgentCapabilitiesResponse(ProtocolVersion: 0, Capabilities: []),
                    () =>
                    {
                        delegateCalls++;
                        return client.CallAsync<string>(
                            AgentProtocolCapabilities.CaptureDiagnosticSnapshot,
                            new { Sections = new[] { DiagnosticSection.VisualTree, DiagnosticSection.Layout } },
                            timeout.Token);
                    }));

            Assert.Multiple(() =>
            {
                Assert.That(delegateCalls, Is.Zero);
                Assert.That(exception!.Message, Does.Contain("Restart the target application"));
                Assert.That(exception.Message, Does.Contain("attach a new session"));
            });

            var pingCall = client.CallAsync<string>("ping", null, timeout.Token);
            var firstPipeRequest = await PipeProtocol.ReadAsync<AgentRequest>(server, timeout.Token);
            Assert.That(
                firstPipeRequest.Method,
                Is.EqualTo("ping"),
                "A capability-missing snapshot call must not write to the pipe.");
            await PipeProtocol.WriteAsync(
                server,
                new AgentResponse(firstPipeRequest.Id, Ok: true, Result: JsonValue.Create("pong")),
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
}
