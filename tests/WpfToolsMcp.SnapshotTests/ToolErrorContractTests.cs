using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;
using WpfToolsMcp.McpServer.Tools;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
public sealed class ToolErrorContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DiagnosticSentinel = @"C:\Users\example\work\diagnostic=visible";

    [Test]
    public async Task Error_filter_preserves_prebuilt_tool_error_results_unchanged()
    {
        var expected = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "existing error content" }]
        };
        var filtered = McpToolErrorFilter.CreateCallToolFilter()(
            (_, _) => ValueTask.FromResult(expected));
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(
            typeof(RequestContext<CallToolRequestParams>));

        var actual = await filtered(context, CancellationToken.None);

        Assert.That(actual, Is.SameAs(expected));
    }

    [Test]
    public async Task Core_and_diagnostics_failures_use_the_common_envelope()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var core = await McpTestContext.StartAsync(serverExe, "core");
        await using var diagnostics = await McpTestContext.StartAsync(serverExe, "diagnostics");

        var actionableResult = await core.CallToolResultAsync("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = Path.Combine(DiagnosticSentinel, "missing.exe")
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
            Assert.That(actionable.Error.Cause!.Type, Is.EqualTo(typeof(FileNotFoundException).FullName));
            Assert.That(actionable.Error.Cause.Details, Does.Contain(DiagnosticSentinel));
            Assert.That(
                actionableResult.StructuredContent!.Value.GetRawText(),
                Does.Contain(DiagnosticSentinel.Replace("\\", "\\\\", StringComparison.Ordinal)));
            AssertCommonResult(argumentResult, invalidArguments, "invalid_request");
            Assert.That(invalidArguments.Error.RecoveryActions, Is.EqualTo(new[] { "correct_arguments" }));
            AssertCommonResult(bindingResult, invalidBinding, "invalid_request");
            Assert.That(invalidBinding.Error.RecoveryActions, Is.EqualTo(new[] { "correct_arguments" }));
        });
    }

    [Test]
    public async Task Unvalidated_request_identities_are_not_claimed_as_validated_context()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe, "diagnostics");

        var result = await mcp.CallToolResultAsync("get_active_window", new Dictionary<string, object?>
        {
            ["sessionId"] = DiagnosticSentinel
        });
        var envelope = Deserialize(result);
        var wire = result.StructuredContent!.Value.GetRawText();

        Assert.Multiple(() =>
        {
            AssertCommonResult(result, envelope, "stale_session");
            Assert.That(envelope.Error.Context, Is.Null);
            Assert.That(envelope.Error.Cause!.Message, Does.Contain(DiagnosticSentinel));
            Assert.That(
                wire,
                Does.Contain(DiagnosticSentinel.Replace("\\", "\\\\", StringComparison.Ordinal)));
            Assert.That(result.Content.OfType<TextContentBlock>().Single().Text, Does.Contain(DiagnosticSentinel));
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
    public void Typed_ambiguities_project_bounded_observed_candidate_context()
    {
        var processFailure = new ProcessSelectionAmbiguityException(new ProcessSelectionAmbiguity(
            Code: "ambiguous_process",
            RequestedProcessName: DiagnosticSentinel,
            DiscoveredCandidates: 1,
            ReturnedCandidates: 1,
            Truncated: true,
            TruncatedReason: "maxResults",
            Candidates:
            [
                new ProcessCandidateInfo(
                    Index: 0,
                    ProcessInstanceId: "123:456",
                    Pid: 123,
                    ProcessName: DiagnosticSentinel,
                    StartTimeUtc: "2026-01-01T00:00:00Z",
                    MainWindowHandle: 789,
                    MainWindowTitle: DiagnosticSentinel,
                    ExecutablePath: DiagnosticSentinel)
            ],
            Recovery: DiagnosticSentinel));
        var elementFailure = new ElementResolutionAmbiguityException(new ResolveElementAmbiguity(
            Code: "ambiguous_element",
            BackendUsed: InspectionBackend.Uia,
            WindowHandleUsed: 789,
            ReturnedCandidates: 1,
            DiscoveredCandidates: 1,
            Truncated: true,
            Candidates:
            [
                new ResolveElementCandidate(
                    0,
                    new ElementRef(
                        Type: DiagnosticSentinel,
                        AutomationId: DiagnosticSentinel,
                        Name: DiagnosticSentinel,
                        XPath: DiagnosticSentinel,
                        Bounds: new Rect(1, 2, 3, 4),
                        ElementId: "uia_abcdefghijklmnop"))
            ],
            TruncatedReason: "legacyAgent"));

        var process = MapException(processFailure);
        var element = MapException(elementFailure);
        var invalidReason = MapException(new ProcessSelectionAmbiguityException(
            processFailure.Ambiguity with { TruncatedReason = DiagnosticSentinel }));
        var oversizedReason = MapException(new ProcessSelectionAmbiguityException(
            processFailure.Ambiguity with { TruncatedReason = new string('a', 65) }));
        var processJson = JsonSerializer.Serialize(process, JsonOptions);
        var elementJson = JsonSerializer.Serialize(element, JsonOptions);
        var invalidReasonJson = JsonSerializer.Serialize(invalidReason, JsonOptions);

        Assert.Multiple(() =>
        {
            Assert.That(process.Code, Is.EqualTo("ambiguous_process"));
            Assert.That(process.Context!.ReturnedCandidates, Is.EqualTo(process.Context.Candidates!.Count));
            Assert.That(process.Context.TruncatedReason, Is.EqualTo("maxResults"));
            Assert.That(process.Context!.Candidates!.Single(), Is.EqualTo(
                new ToolErrorCandidate(ToolErrorCandidateKind.Process, 0)
                {
                    ProcessInstanceId = "123:456",
                    Pid = 123,
                    WindowHandle = 789,
                    ProcessName = DiagnosticSentinel,
                    StartTimeUtc = "2026-01-01T00:00:00Z",
                    MainWindowTitle = DiagnosticSentinel,
                    ExecutablePath = DiagnosticSentinel
                }));
            Assert.That(element.Code, Is.EqualTo("ambiguous_element"));
            Assert.That(element.Context!.ReturnedCandidates, Is.EqualTo(element.Context.Candidates!.Count));
            Assert.That(element.Context.TruncatedReason, Is.EqualTo("legacyAgent"));
            Assert.That(element.Context!.Candidates!.Single(), Is.EqualTo(
                new ToolErrorCandidate(ToolErrorCandidateKind.Element, 0)
                {
                    ElementId = "uia_abcdefghijklmnop",
                    ElementType = DiagnosticSentinel,
                    AutomationId = DiagnosticSentinel,
                    Name = DiagnosticSentinel,
                    XPath = DiagnosticSentinel,
                    Bounds = new Rect(1, 2, 3, 4)
                }));
            Assert.That(invalidReason.Context!.TruncatedReason, Is.Null);
            Assert.That(oversizedReason.Context!.TruncatedReason, Is.Null);
            Assert.That(processJson, Does.Contain(DiagnosticSentinel.Replace("\\", "\\\\", StringComparison.Ordinal)));
            Assert.That(elementJson, Does.Contain(DiagnosticSentinel.Replace("\\", "\\\\", StringComparison.Ordinal)));
            Assert.That(invalidReasonJson, Does.Contain(DiagnosticSentinel.Replace("\\", "\\\\", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void Oversized_process_candidate_executable_path_is_omitted_instead_of_sliced()
    {
        var observedPath = DiagnosticSentinel + new string('x', 600);
        var failure = new ProcessSelectionAmbiguityException(new ProcessSelectionAmbiguity(
            Code: "ambiguous_process",
            RequestedProcessName: "test",
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
                    ProcessName: "test",
                    StartTimeUtc: "2026-01-01T00:00:00Z",
                    MainWindowHandle: 789,
                    MainWindowTitle: "test",
                    ExecutablePath: observedPath)
            ],
            Recovery: "select"));

        var error = MapException(failure);
        var candidate = error.Context!.Candidates!.Single();

        Assert.Multiple(() =>
        {
            Assert.That(candidate.ExecutablePath, Is.Null);
            Assert.That(candidate.ExecutablePathUnavailableReason, Does.StartWith("executablePathOmitted:"));
            Assert.That(candidate.ExecutablePathUnavailableReason, Does.Contain($"actualLength={observedPath.Length}"));
        });
    }

    [Test]
    public void Oversized_element_candidate_xpath_is_omitted_instead_of_sliced()
    {
        var observedXPath = "/Window/" + new string('x', 1_100);
        var failure = new ElementResolutionAmbiguityException(new ResolveElementAmbiguity(
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
                        Type: "Button",
                        AutomationId: "Save",
                        Name: "Save",
                        XPath: observedXPath,
                        ElementId: "uia_abcdefghijklmnop"))
            ]));

        var candidate = MapException(failure).Context!.Candidates!.Single();

        Assert.Multiple(() =>
        {
            Assert.That(candidate.XPath, Is.Null);
            Assert.That(candidate.XPathOmitted, Is.True);
        });
    }

    [Test]
    public void Process_candidate_executable_path_unavailable_reason_is_explicit_and_bounded()
    {
        var observedReason = "mainModuleReadFailed:" + new string('r', 300);
        var failure = new ProcessSelectionAmbiguityException(new ProcessSelectionAmbiguity(
            Code: "ambiguous_process",
            RequestedProcessName: "test",
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
                    ProcessName: "test",
                    StartTimeUtc: "2026-01-01T00:00:00Z",
                    MainWindowHandle: 789,
                    MainWindowTitle: "test",
                    ExecutablePathUnavailableReason: observedReason)
            ],
            Recovery: "select"));

        var candidate = MapException(failure).Context!.Candidates!.Single();

        Assert.Multiple(() =>
        {
            Assert.That(candidate.ExecutablePath, Is.Null);
            Assert.That(candidate.ExecutablePathUnavailableReason, Has.Length.EqualTo(256));
            Assert.That(candidate.ExecutablePathUnavailableReason, Is.EqualTo(observedReason[..256]));
        });
    }

    [Test]
    public void Typed_ambiguity_filter_reports_its_projected_candidate_cap()
    {
        var processCandidates = Enumerable.Range(0, 30)
            .Select(index => new ProcessCandidateInfo(
                Index: index,
                ProcessInstanceId: $"{index + 1}:{index + 101}",
                Pid: index + 1,
                ProcessName: "test",
                StartTimeUtc: "2026-01-01T00:00:00Z",
                MainWindowHandle: index + 1,
                MainWindowTitle: "test"))
            .ToArray();
        var elementCandidates = Enumerable.Range(0, 30)
            .Select(index => new ResolveElementCandidate(
                index,
                new ElementRef(
                    Type: "Button",
                    AutomationId: null,
                    Name: null,
                    XPath: $"/Window/Button[{index + 1}]",
                    ElementId: $"uia_{index + 1:D16}")))
            .ToArray();

        var process = MapException(new ProcessSelectionAmbiguityException(new ProcessSelectionAmbiguity(
            Code: "ambiguous_process",
            RequestedProcessName: "test",
            DiscoveredCandidates: 40,
            ReturnedCandidates: 30,
            Truncated: false,
            TruncatedReason: null,
            Candidates: processCandidates,
            Recovery: "select")));
        var element = MapException(new ElementResolutionAmbiguityException(new ResolveElementAmbiguity(
            Code: "ambiguous_element",
            BackendUsed: InspectionBackend.Wpf,
            WindowHandleUsed: 789,
            ReturnedCandidates: 30,
            DiscoveredCandidates: 40,
            Truncated: false,
            Candidates: elementCandidates)));
        var upstreamTruncation = MapException(new ProcessSelectionAmbiguityException(new ProcessSelectionAmbiguity(
            Code: "ambiguous_process",
            RequestedProcessName: "test",
            DiscoveredCandidates: 40,
            ReturnedCandidates: 30,
            Truncated: true,
            TruncatedReason: "maxResults",
            Candidates: processCandidates,
            Recovery: "select")));

        Assert.Multiple(() =>
        {
            Assert.That(process.Context!.Candidates, Has.Count.EqualTo(25));
            Assert.That(process.Context.ReturnedCandidates, Is.EqualTo(process.Context.Candidates!.Count));
            Assert.That(process.Context.DiscoveredCandidates, Is.EqualTo(40));
            Assert.That(process.Context.Truncated, Is.True);
            Assert.That(process.Context.TruncatedReason, Is.EqualTo("maxCandidates"));
            Assert.That(element.Context!.Candidates, Has.Count.EqualTo(25));
            Assert.That(element.Context.ReturnedCandidates, Is.EqualTo(element.Context.Candidates!.Count));
            Assert.That(element.Context.DiscoveredCandidates, Is.EqualTo(40));
            Assert.That(element.Context.Truncated, Is.True);
            Assert.That(element.Context.TruncatedReason, Is.EqualTo("maxCandidates"));
            Assert.That(upstreamTruncation.Context!.Candidates, Has.Count.EqualTo(25));
            Assert.That(upstreamTruncation.Context.ReturnedCandidates, Is.EqualTo(25));
            Assert.That(upstreamTruncation.Context.TruncatedReason, Is.EqualTo("maxResults"));
        });
    }

    [Test]
    public void Unknown_and_known_code_mappings_keep_stable_details_and_bounded_cause_evidence()
    {
        var unknown = MapException(new InvalidOperationException(DiagnosticSentinel));
        var stale = MapException(new InvalidOperationException($"wpf_handle_stale: {DiagnosticSentinel}"));
        var capability = MapException(new InvalidOperationException($"agent_capability_unavailable: {DiagnosticSentinel}"));
        var outsideSession = MapException(new InvalidOperationException($"window_outside_session: {DiagnosticSentinel}"));
        var occluded = MapException(new InvalidOperationException($"mouse_target_occluded: {DiagnosticSentinel}"));
        var performanceOwner = MapException(new InvalidOperationException("performance_run_not_owned"));
        var subscription = MapException(new InvalidOperationException($"subscription_not_found: {DiagnosticSentinel}"));
        var missingWpfElement = MapException(new InvalidOperationException($"wpf_resolve:not_found: {DiagnosticSentinel}"));
        var oversized = MapException(new InvalidOperationException(new string('m', 1_200)));
        var disabled = MapException(new InvalidOperationException("element_disabled: The target is disabled."));
        var subscriptionLimit = MapException(new InvalidOperationException("subscription_limit_exceeded: Too many active subscriptions."));
        var targetOwnedPrefix = MapException(new InvalidOperationException("application_specific_error: target-defined prose"));

        Assert.Multiple(() =>
        {
            Assert.That(unknown.Code, Is.EqualTo("tool_failed"));
            Assert.That(unknown.Detail, Is.EqualTo("The tool operation failed."));
            Assert.That(unknown.Cause, Is.EqualTo(new DiagnosticCauseInfo(typeof(InvalidOperationException).FullName!)
            {
                Message = DiagnosticSentinel
            }));
            Assert.That(stale.Code, Is.EqualTo("stale_element"));
            Assert.That(stale.Detail, Is.EqualTo("The element handle is no longer valid."));
            Assert.That(stale.Cause!.Message, Is.EqualTo($"wpf_handle_stale: {DiagnosticSentinel}"));
            Assert.That(capability.Code, Is.EqualTo("agent_capability_unavailable"));
            Assert.That(capability.RecoveryActions, Is.EqualTo(new[] { "restart_and_reattach" }));
            Assert.That(outsideSession.Code, Is.EqualTo("window_outside_session"));
            Assert.That(occluded.Code, Is.EqualTo("mouse_target_occluded"));
            Assert.That(performanceOwner.Code, Is.EqualTo("performance_run_not_owned"));
            Assert.That(subscription.Code, Is.EqualTo("subscription_not_found"));
            Assert.That(missingWpfElement.Code, Is.EqualTo("element_not_found"));
            Assert.That(disabled.Code, Is.EqualTo("element_disabled"));
            Assert.That(disabled.Detail, Is.EqualTo("The target is disabled."));
            Assert.That(subscriptionLimit.Code, Is.EqualTo("subscription_limit_exceeded"));
            Assert.That(targetOwnedPrefix.Code, Is.EqualTo("tool_failed"));
            Assert.That(oversized.Cause!.Message, Has.Length.EqualTo(1_024));
            Assert.That(
                JsonSerializer.Serialize(
                    new[]
                    {
                        unknown,
                        stale,
                        capability,
                        outsideSession,
                        occluded,
                        performanceOwner,
                        subscription,
                        missingWpfElement,
                        disabled,
                        subscriptionLimit,
                        targetOwnedPrefix,
                        oversized
                    },
                    JsonOptions),
                Does.Contain(DiagnosticSentinel.Replace("\\", "\\\\", StringComparison.Ordinal)));
        });
    }

    [TestCase("element_disabled")]
    [TestCase("foreground_activation_failed")]
    [TestCase("focused_element_unavailable")]
    [TestCase("wait_backend_unsupported")]
    [TestCase("subscription_limit_exceeded")]
    [TestCase("window_state_change_failed")]
    [TestCase("observe_state_unsupported")]
    [TestCase("uia_action_failed")]
    public void Stable_tool_owned_codes_are_not_collapsed_to_tool_failed(string code)
    {
        var error = MapException(new InvalidOperationException($"{code}: observed detail"));

        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo(code));
            Assert.That(error.Detail, Is.EqualTo("observed detail"));
        });
    }

    [Test]
    public void Remote_target_prose_requires_agent_owned_code_provenance()
    {
        var targetCollision = MapException(new AgentRemoteException(
            "wpf/get_computed_properties",
            "element_disabled: application-defined failure",
            "application stack"));
        var typedAgentFailure = MapException(new AgentRemoteException(
            "wpf/focus_element",
            "element_disabled: focus target is disabled",
            "agent stack",
            "element_disabled"));
        var newlyTypedAgentFailure = MapException(new AgentRemoteException(
            "wpf/future_operation",
            "future_agent_failure: local diagnostic detail",
            "agent stack",
            "future_agent_failure"));

        Assert.Multiple(() =>
        {
            Assert.That(targetCollision.Code, Is.EqualTo("tool_failed"));
            Assert.That(targetCollision.Cause!.Message, Is.EqualTo("element_disabled: application-defined failure"));
            Assert.That(targetCollision.Cause.Details, Is.EqualTo("application stack"));
            Assert.That(typedAgentFailure.Code, Is.EqualTo("element_disabled"));
            Assert.That(typedAgentFailure.Detail, Is.EqualTo("focus target is disabled"));
            Assert.That(newlyTypedAgentFailure.Code, Is.EqualTo("future_agent_failure"));
            Assert.That(newlyTypedAgentFailure.Detail, Is.EqualTo("local diagnostic detail"));
        });
    }

    [Test]
    public void Agent_tool_error_marker_preserves_exception_type_and_records_owned_code()
    {
        var exception = AgentToolError.InvalidOperation(
            "observe_state_not_found",
            "observe_state_not_found");

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(AgentToolError.GetCode(exception), Is.EqualTo("observe_state_not_found"));
        });
    }

    [Test]
    public void Error_mapping_reads_overridden_exception_messages_best_effort()
    {
        var directThrowing = new ThrowingMessageException();
        var aggregateThrowing = new ThrowingMessageException();

        var direct = MapException(directThrowing);
        var aggregate = MapException(new AggregateException(aggregateThrowing));

        Assert.Multiple(() =>
        {
            Assert.That(direct.Code, Is.EqualTo("tool_failed"));
            Assert.That(aggregate.Code, Is.EqualTo("tool_failed"));
            Assert.That(direct.Cause!.Type, Is.EqualTo(typeof(ThrowingMessageException).FullName));
            Assert.That(direct.Cause.Message, Is.Null);
            Assert.That(direct.Cause.MessageUnavailableReason, Does.StartWith("getter_threw: System.InvalidOperationException:"));
            Assert.That(directThrowing.GetterCalls, Is.EqualTo(1));
            Assert.That(aggregateThrowing.GetterCalls, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public void Actionable_and_remote_failures_expose_their_real_bounded_diagnostic_cause()
    {
        var actionable = MapException(new ActionableFailureException(
            new FailureInfo("injection_failed", "injection", "Injection failed."),
            new InvalidOperationException("injector exit code 7")));
        var remote = MapException(new AgentRemoteException(
            "wpf/inspect",
            new string('m', 1_200),
            new string('d', 5_000)));
        var embeddedCause = MapException(new ActionableFailureException(
            new FailureInfo("backend_operation_failed", "protocol", "Backend failed.")
            {
                Cause = new DiagnosticCauseInfo("Customer.BackendException")
                {
                    Message = new string('e', 1_200),
                    Details = "stored backend evidence"
                }
            }));
        var legacyRemote = new AgentRemoteException(
            "wpf/resolve_element",
            "wpf_resolve:ambiguous: Locator is ambiguous (found 2).",
            "remote target stack");
        var legacyAmbiguity = MapException(
            AutomationController.CreateLegacyWpfAmbiguityException(legacyRemote, 42));
        var performance = MapException(new InvalidOperationException(
            "performance_run_not_owned",
            new AgentRemoteException(
                "wpf/performance_stop",
                "performance_run_not_owned: activeRunId=observed-run",
                "remote performance details")));
        var surrogateBoundary = MapException(new InvalidOperationException(
            new string('s', 1_023) + char.ConvertFromUtf32(0x1F600)));

        Assert.Multiple(() =>
        {
            Assert.That(actionable.Code, Is.EqualTo("injection_failed"));
            Assert.That(actionable.Stage, Is.EqualTo("injection"));
            Assert.That(actionable.Detail, Is.EqualTo("Injection failed."));
            Assert.That(actionable.Cause, Is.EqualTo(new DiagnosticCauseInfo(typeof(InvalidOperationException).FullName!)
            {
                Message = "injector exit code 7"
            }));
            Assert.That(remote.Code, Is.EqualTo("tool_failed"));
            Assert.That(remote.Cause!.Type, Is.EqualTo(typeof(AgentRemoteException).FullName));
            Assert.That(remote.Cause.Message, Has.Length.EqualTo(1_024));
            Assert.That(remote.Cause.Details, Has.Length.EqualTo(4_096));
            Assert.That(embeddedCause.Cause!.Type, Is.EqualTo("Customer.BackendException"));
            Assert.That(embeddedCause.Cause.Message, Has.Length.EqualTo(1_024));
            Assert.That(embeddedCause.Cause.Details, Is.EqualTo("stored backend evidence"));
            Assert.That(legacyAmbiguity.Code, Is.EqualTo("ambiguous_element"));
            Assert.That(legacyAmbiguity.Cause!.Type, Is.EqualTo(typeof(AgentRemoteException).FullName));
            Assert.That(legacyAmbiguity.Cause.Message, Does.StartWith("wpf_resolve:ambiguous"));
            Assert.That(legacyAmbiguity.Cause.Details, Is.EqualTo("remote target stack"));
            Assert.That(performance.Code, Is.EqualTo("performance_run_not_owned"));
            Assert.That(performance.Cause!.Type, Is.EqualTo(typeof(AgentRemoteException).FullName));
            Assert.That(performance.Cause.Message, Does.Contain("activeRunId=observed-run"));
            Assert.That(performance.Cause.Details, Is.EqualTo("remote performance details"));
            Assert.That(surrogateBoundary.Cause!.Message, Has.Length.EqualTo(1_023));
            Assert.That(surrogateBoundary.Cause.Message, Does.EndWith("s"));
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
        var throwingMessage = new ThrowingMessageException();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var aggregate = new AggregateException(new OperationCanceledException(), throwingMessage);
        var normalized = CreateRequestCancellation(aggregate, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.CancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(normalized.InnerException, Is.SameAs(aggregate));
            Assert.That(throwingMessage.GetterCalls, Is.Zero);
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
            Does.StartWith($"{response.Error.Code}: {response.Error.Detail}"));
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

    private sealed class ThrowingMessageException : Exception
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
