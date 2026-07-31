using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public sealed class ActionableFailurePrivacyTests
{
    private const string PrivateSentinel = @"C:\Users\private\project\token=super-secret";
    private const string TestSessionId = "11111111111111111111111111111111";

    [Test]
    public async Task Launch_actionable_failure_keeps_stable_text_and_structured_cause_evidence()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe);

        var result = await mcp.CallToolResultAsync("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = Path.Combine(PrivateSentinel, "missing.exe")
        });

        var failure = JsonSerializer.Deserialize<ToolErrorResponse>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var text = result.Content.OfType<TextContentBlock>().Single().Text;

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure!.Error.Code, Is.EqualTo("process_not_found"));
            Assert.That(failure.Error.Stage, Is.EqualTo("process_discovery"));
            Assert.That(failure.Error.Cause, Is.Not.Null);
            Assert.That(failure.Error.Cause!.Type, Is.EqualTo(typeof(FileNotFoundException).FullName));
            Assert.That(failure.Error.Cause.Message, Is.EqualTo("The requested executable was not found."));
            Assert.That(failure.Error.Cause.Details, Does.Contain(PrivateSentinel));
            Assert.That(text, Does.Contain("process_not_found"));
            Assert.That(text, Does.Contain(failure.Error.Detail));
            Assert.That(text, Does.Not.Contain(PrivateSentinel));
            Assert.That(result.StructuredContent!.Value.GetRawText(), Does.Contain("token=super-secret"));
        });
    }

    [Test]
    public async Task Relative_missing_executable_is_reported_as_process_discovery_failure()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe);
        var missingExecutable = $"wpf-tools-mcp-missing-{Guid.NewGuid():N}.exe";

        var result = await mcp.CallToolResultAsync("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = missingExecutable
        });
        var failure = JsonSerializer.Deserialize<ToolErrorResponse>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(failure!.Error.Code, Is.EqualTo("process_not_found"));
            Assert.That(failure.Error.Stage, Is.EqualTo("process_discovery"));
            Assert.That(failure.Error.Retryable, Is.False);
            Assert.That(failure.Error.Cause!.Message, Does.Contain(missingExecutable));
            Assert.That(result.Content.OfType<TextContentBlock>().Single().Text, Does.Not.Contain(missingExecutable));
        });
    }

    [Test]
    public void Unknown_tool_error_mapping_keeps_stable_detail_and_bounded_cause()
    {
        var serverAssemblyPath = Path.ChangeExtension(
            McpServerPaths.FindMcpServerExecutable(),
            ".dll");
        var serverAssembly = Assembly.LoadFrom(serverAssemblyPath);
        var errorBoundary = serverAssembly.GetType(
            "WpfToolsMcp.McpServer.Tools.McpToolErrorFilter",
            throwOnError: true)!;
        var mapException = errorBoundary.GetMethod(
            "MapException",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var mapped = (ToolErrorInfo)mapException.Invoke(
            null,
            [new InvalidOperationException(PrivateSentinel), null])!;
        var json = JsonSerializer.Serialize(mapped, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Code, Is.EqualTo("tool_failed"));
            Assert.That(mapped.Detail, Is.EqualTo("The tool operation failed."));
            Assert.That(mapped.Cause, Is.EqualTo(new DiagnosticCauseInfo(typeof(InvalidOperationException).FullName!)
            {
                Message = PrivateSentinel
            }));
            Assert.That(json, Does.Contain("token=super-secret"));
        });
    }

    [Test]
    public async Task Trace_records_actionable_code_and_bounded_diagnostic_cause()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(TestSessionId, resetIfRunning: false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-actionable-trace-test-{Guid.NewGuid():N}.json");
        var failure = FailureDiagnostics.Exception(
            FailureDiagnostics.Codes.InjectionFailed,
            FailureDiagnostics.Stages.Injection,
            "The WPF backend could not be initialized in the target process.",
            retryable: false,
            recoveryActions: [FailureDiagnostics.Recovery.UseUia],
            inner: new InvalidOperationException(PrivateSentinel));

        try
        {
            using (var trace = controller.BeginToolTrace("inject_agent"))
            {
                trace!.SetError(failure);
            }

            var response = await controller.TraceStopAsync(
                traceStart.TraceId,
                outputPath,
                includeEvents: true);
            var traceError = response.Events!.Single().Error;
            var artifact = await File.ReadAllTextAsync(outputPath);

            Assert.Multiple(() =>
            {
                Assert.That(
                    traceError,
                    Does.StartWith("injection_failed: The WPF backend could not be initialized in the target process."));
                Assert.That(
                    traceError,
                    Does.Contain($"Cause: System.InvalidOperationException: {PrivateSentinel}"));
                Assert.That(traceError, Has.Length.LessThanOrEqualTo(1_000));
                Assert.That(artifact, Does.Contain("token=super-secret"));
            });
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Test]
    public async Task Trace_records_non_actionable_exception_type_and_message()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(TestSessionId, resetIfRunning: false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-private-trace-test-{Guid.NewGuid():N}.json");

        try
        {
            using (var trace = controller.BeginToolTrace("unknown_failure"))
            {
                trace!.SetError(new InvalidOperationException(PrivateSentinel));
            }

            var response = await controller.TraceStopAsync(
                traceStart.TraceId,
                outputPath,
                includeEvents: true);
            var traceError = response.Events!.Single().Error;
            var artifact = await File.ReadAllTextAsync(outputPath);

            Assert.Multiple(() =>
            {
                Assert.That(
                    traceError,
                    Is.EqualTo(
                        $"tool_failed: The tool operation failed. Cause: System.InvalidOperationException: {PrivateSentinel}"));
                Assert.That(artifact, Does.Contain("token=super-secret"));
            });
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Test]
    public async Task Trace_catches_a_failing_exception_message_getter()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(TestSessionId, resetIfRunning: false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-throwing-message-trace-test-{Guid.NewGuid():N}.json");
        var hostile = new HostileMessageException();

        try
        {
            using (var trace = controller.BeginToolTrace("throwing_message"))
            {
                trace!.SetError(hostile);
            }

            var response = await controller.TraceStopAsync(
                traceStart.TraceId,
                outputPath,
                includeEvents: true);
            var traceError = response.Events!.Single().Error;

            Assert.Multiple(() =>
            {
                Assert.That(traceError, Does.StartWith("tool_failed: The tool operation failed. Cause: "));
                Assert.That(traceError, Does.Contain(nameof(HostileMessageException)));
                Assert.That(traceError, Does.Contain("message unavailable: InvalidOperationException"));
                Assert.That(traceError, Has.Length.LessThanOrEqualTo(1_000));
                Assert.That(hostile.GetterCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Test]
    public async Task Trace_reuses_an_embedded_actionable_cause_without_an_inner_exception()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(TestSessionId, resetIfRunning: false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-embedded-cause-trace-test-{Guid.NewGuid():N}.json");
        var failure = new FailureInfo(
            "agent_connection_failed",
            "pipe_connection",
            "The WPF agent pipe could not be connected.")
        {
            Cause = new DiagnosticCauseInfo("Customer.PipeException")
            {
                Message = "named pipe diagnostic"
            }
        };

        try
        {
            using (var trace = controller.BeginToolTrace("embedded_cause"))
            {
                trace!.SetError(new ActionableFailureException(failure));
            }

            var response = await controller.TraceStopAsync(
                traceStart.TraceId,
                outputPath,
                includeEvents: true);

            Assert.That(
                response.Events!.Single().Error,
                Is.EqualTo(
                    "agent_connection_failed: The WPF agent pipe could not be connected. " +
                    "Cause: Customer.PipeException: named pipe diagnostic"));
        }
        finally
        {
            File.Delete(outputPath);
        }
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
