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
    public async Task Launch_actionable_failure_returns_structured_sanitized_failure()
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
            Assert.That(text, Does.Contain("process_not_found"));
            Assert.That(text, Does.Contain(failure.Error.Detail));
            Assert.That(text, Does.Not.Contain(PrivateSentinel));
            Assert.That(result.StructuredContent!.Value.GetRawText(), Does.Not.Contain(PrivateSentinel));
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
            Assert.That(result.Content.OfType<TextContentBlock>().Single().Text, Does.Not.Contain(missingExecutable));
        });
    }

    [Test]
    public void Unknown_tool_error_mapping_ignores_raw_exception_messages()
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
            Assert.That(json, Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public async Task Trace_records_only_actionable_code_and_safe_detail()
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
                    Is.EqualTo("injection_failed: The WPF backend could not be initialized in the target process."));
                Assert.That(traceError, Does.Not.Contain(PrivateSentinel));
                Assert.That(artifact, Does.Not.Contain(PrivateSentinel));
            });
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Test]
    public async Task Trace_replaces_non_actionable_errors_without_reading_exception_messages()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(TestSessionId, resetIfRunning: false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"wpf-tools-mcp-private-trace-test-{Guid.NewGuid():N}.json");
        var hostile = new HostileMessageException();

        try
        {
            using (var trace = controller.BeginToolTrace("unknown_failure"))
            {
                trace!.SetError(new AggregateException(
                    new InvalidOperationException(PrivateSentinel),
                    hostile));
            }

            var response = await controller.TraceStopAsync(
                traceStart.TraceId,
                outputPath,
                includeEvents: true);
            var traceError = response.Events!.Single().Error;
            var artifact = await File.ReadAllTextAsync(outputPath);

            Assert.Multiple(() =>
            {
                Assert.That(traceError, Is.EqualTo("tool_failed: The tool operation failed."));
                Assert.That(traceError, Does.Not.Contain(PrivateSentinel));
                Assert.That(artifact, Does.Not.Contain(PrivateSentinel));
                Assert.That(hostile.GetterCalls, Is.Zero);
            });
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
