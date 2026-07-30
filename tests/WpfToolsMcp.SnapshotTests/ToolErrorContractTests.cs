using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class ToolErrorContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string PrivateSentinel = @"C:\Users\private\work\token=super-secret";

    [Test]
    public async Task Core_and_diagnostics_failures_use_the_common_envelope()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, "diagnostics");

        var actionableResult = await core.CallToolResultAsync("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = Path.Combine(PrivateSentinel, "missing.exe")
        });
        var argumentResult = await diagnostics.CallToolResultAsync(
            "subscribe_property_changes",
            new Dictionary<string, object?>());
        var bindingResult = await core.CallToolResultAsync("list_windows", new Dictionary<string, object?>
        {
            ["sessionId"] = 123
        });
        var actionable = Deserialize(actionableResult);
        var invalidArguments = Deserialize(argumentResult);
        var invalidBinding = Deserialize(bindingResult);

        Assert.Multiple(() =>
        {
            AssertCommonResult(actionableResult, actionable, "process_not_found");
            Assert.That(actionable.Error.Stage, Is.EqualTo("process_discovery"));
            Assert.That(actionable.Error.Retryable, Is.False);
            Assert.That(actionableResult.StructuredContent!.Value.GetRawText(), Does.Not.Contain(PrivateSentinel));
            AssertCommonResult(argumentResult, invalidArguments, "invalid_request");
            Assert.That(invalidArguments.Error.RecoveryActions, Is.EqualTo(new[] { "correct_arguments" }));
            AssertCommonResult(bindingResult, invalidBinding, "invalid_request");
            Assert.That(invalidBinding.Error.RecoveryActions, Is.EqualTo(new[] { "correct_arguments" }));
        });
    }

    [Test]
    public async Task Unvalidated_request_identities_are_not_echoed_in_failures()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, "diagnostics");

        var result = await mcp.CallToolResultAsync("get_active_window", new Dictionary<string, object?>
        {
            ["sessionId"] = PrivateSentinel
        });
        var envelope = Deserialize(result);
        var wire = result.StructuredContent!.Value.GetRawText();

        Assert.Multiple(() =>
        {
            AssertCommonResult(result, envelope, "stale_session");
            Assert.That(envelope.Error.Context, Is.Null);
            Assert.That(wire, Does.Not.Contain(PrivateSentinel));
            Assert.That(result.Content.OfType<TextContentBlock>().Single().Text, Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public async Task Unknown_tools_remain_protocol_errors()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, "core");

        Assert.ThrowsAsync<McpProtocolException>(async () =>
            _ = await mcp.CallToolResultAsync("wpf_tools_mcp_unknown_tool"));
    }

    [Test]
    public async Task Request_cancellation_is_not_converted_to_a_tool_error()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        var appExe = TestAppPaths.FindTestAppExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, "diagnostics");
        var launch = await mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = appExe,
            ["workingDirectory"] = Path.GetDirectoryName(appExe)!
        });

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            Assert.CatchAsync<OperationCanceledException>(async () =>
                _ = await mcp.CallToolResultAsync("wait_for", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["backend"] = "uia",
                    ["locator"] = new Dictionary<string, object?>
                    {
                        ["automationId"] = "ToolErrorContract_MissingElement"
                    },
                    ["state"] = "visible",
                    ["timeoutMs"] = 5_000,
                    ["pollIntervalMs"] = 25
                }, cancellation.Token));
        }
        finally
        {
            try
            {
                _ = await mcp.CallToolResultAsync("close_session", new Dictionary<string, object?>
                {
                    ["sessionId"] = launch.SessionId,
                    ["force"] = true
                });
            }
            catch
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(launch.Pid);
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(5_000);
                }
                catch
                {
                }
            }
        }
    }

    [Test]
    public void Typed_ambiguities_project_only_bounded_reusable_candidate_identity()
    {
        var processFailure = new ProcessSelectionAmbiguityException(new ProcessSelectionAmbiguity(
            Code: "ambiguous_process",
            RequestedProcessName: PrivateSentinel,
            DiscoveredCandidates: 1,
            ReturnedCandidates: 1,
            Truncated: false,
            TruncatedReason: null,
            Candidates:
            [
                new ProcessCandidateInfo(
                    Index: 0,
                    ProcessInstanceId: "123:456",
                    Pid: 123,
                    ProcessName: PrivateSentinel,
                    StartTimeUtc: "2026-01-01T00:00:00Z",
                    MainWindowHandle: 789,
                    MainWindowTitle: PrivateSentinel)
            ],
            Recovery: PrivateSentinel));
        var elementFailure = new ElementResolutionAmbiguityException(new ResolveElementAmbiguity(
            Code: "ambiguous_element",
            BackendUsed: InspectionBackend.Uia,
            WindowHandleUsed: 789,
            ReturnedCandidates: 1,
            DiscoveredCandidates: 1,
            Truncated: false,
            Candidates:
            [
                new ResolveElementCandidate(
                    0,
                    new ElementRef(
                        Type: PrivateSentinel,
                        AutomationId: PrivateSentinel,
                        Name: PrivateSentinel,
                        XPath: PrivateSentinel,
                        ElementId: "uia_abcdefghijklmnop"))
            ]));

        var process = MapException(processFailure);
        var element = MapException(elementFailure);
        var processJson = JsonSerializer.Serialize(process, JsonOptions);
        var elementJson = JsonSerializer.Serialize(element, JsonOptions);

        Assert.Multiple(() =>
        {
            Assert.That(process.Code, Is.EqualTo("ambiguous_process"));
            Assert.That(process.Context!.Candidates!.Single(), Is.EqualTo(
                new ToolErrorCandidate(ToolErrorCandidateKind.Process, 0)
                {
                    ProcessInstanceId = "123:456",
                    Pid = 123,
                    WindowHandle = 789
                }));
            Assert.That(element.Code, Is.EqualTo("ambiguous_element"));
            Assert.That(element.Context!.Candidates!.Single(), Is.EqualTo(
                new ToolErrorCandidate(ToolErrorCandidateKind.Element, 0)
                {
                    ElementId = "uia_abcdefghijklmnop"
                }));
            Assert.That(processJson, Does.Not.Contain(PrivateSentinel));
            Assert.That(elementJson, Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public void Unknown_and_known_code_mappings_never_copy_exception_suffixes()
    {
        var unknown = MapException(new InvalidOperationException(PrivateSentinel));
        var stale = MapException(new InvalidOperationException($"wpf_handle_stale: {PrivateSentinel}"));
        var capability = MapException(new InvalidOperationException($"agent_capability_unavailable: {PrivateSentinel}"));

        Assert.Multiple(() =>
        {
            Assert.That(unknown.Code, Is.EqualTo("tool_failed"));
            Assert.That(unknown.Detail, Is.EqualTo("The tool operation failed."));
            Assert.That(stale.Code, Is.EqualTo("stale_element"));
            Assert.That(stale.Detail, Is.EqualTo("The element handle is no longer valid."));
            Assert.That(capability.Code, Is.EqualTo("agent_capability_unavailable"));
            Assert.That(capability.RecoveryActions, Is.EqualTo(new[] { "restart_and_reattach" }));
            Assert.That(JsonSerializer.Serialize(new[] { unknown, stale, capability }, JsonOptions), Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public void Error_mapping_never_invokes_overridden_exception_message_getters()
    {
        var directHostile = new HostileMessageException();
        var aggregateHostile = new HostileMessageException();

        var direct = MapException(directHostile);
        var aggregate = MapException(new AggregateException(aggregateHostile));

        Assert.Multiple(() =>
        {
            Assert.That(direct.Code, Is.EqualTo("tool_failed"));
            Assert.That(aggregate.Code, Is.EqualTo("tool_failed"));
            Assert.That(directHostile.GetterCalls, Is.Zero);
            Assert.That(aggregateHostile.GetterCalls, Is.Zero);
        });
    }

    [Test]
    public void Error_mapping_bounds_aggregate_exception_traversal()
    {
        var actionableOutsideTraversalBudget = new ActionableFailureException(
            new FailureInfo(
                "process_not_found",
                "process_discovery",
                "This entry must not be reached."));
        var innerExceptions = Enumerable.Range(0, 31)
            .Select(_ => (Exception)new InvalidOperationException("ordinary"))
            .Append(actionableOutsideTraversalBudget)
            .ToArray();

        var mapped = MapException(new AggregateException(innerExceptions));

        Assert.That(mapped.Code, Is.EqualTo("tool_failed"));
    }

    [Test]
    public void Aggregate_request_cancellation_is_normalized_without_reading_inner_messages()
    {
        var hostile = new HostileMessageException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var aggregate = new AggregateException(new OperationCanceledException(), hostile);
        var normalized = CreateRequestCancellation(aggregate, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.CancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(normalized.InnerException, Is.SameAs(aggregate));
            Assert.That(hostile.GetterCalls, Is.Zero);
        });
    }

    private static void AssertCommonResult(
        CallToolResult result,
        ToolErrorResponse response,
        string expectedCode)
    {
        Assert.That(result.IsError, Is.True);
        Assert.That(response.Error.Code, Is.EqualTo(expectedCode));
        Assert.That(
            result.Content.OfType<TextContentBlock>().Single().Text,
            Is.EqualTo($"{response.Error.Code}: {response.Error.Detail}"));
    }

    private static ToolErrorResponse Deserialize(CallToolResult result) =>
        JsonSerializer.Deserialize<ToolErrorResponse>(
            result.StructuredContent!.Value.GetRawText(),
            JsonOptions) ?? throw new AssertionException("Tool error envelope deserialized to null.");

    private static ToolErrorInfo MapException(Exception exception)
    {
        var serverAssembly = Assembly.LoadFrom(Path.ChangeExtension(
            McpServerPaths.FindMcpServerExecutable(),
            ".dll"));
        var filter = serverAssembly.GetType(
            "WpfToolsMcp.McpServer.Tools.McpToolErrorFilter",
            throwOnError: true)!;
        var method = filter.GetMethod("MapException", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ToolErrorInfo)method.Invoke(null, [exception, null])!;
    }

    private static OperationCanceledException CreateRequestCancellation(
        Exception exception,
        CancellationToken cancellationToken)
    {
        var serverAssembly = Assembly.LoadFrom(Path.ChangeExtension(
            McpServerPaths.FindMcpServerExecutable(),
            ".dll"));
        var filter = serverAssembly.GetType(
            "WpfToolsMcp.McpServer.Tools.McpToolErrorFilter",
            throwOnError: true)!;
        var method = filter.GetMethod(
            "CreateRequestCancellation",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (OperationCanceledException)method.Invoke(null, [exception, cancellationToken])!;
    }

    private sealed class HostileMessageException : Exception
    {
        public int GetterCalls { get; private set; }

        public override string Message
        {
            get
            {
                GetterCalls++;
                throw new InvalidOperationException("Message getter must not be invoked.");
            }
        }
    }
}
