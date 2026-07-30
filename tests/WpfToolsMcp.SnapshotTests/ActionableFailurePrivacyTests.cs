using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

public sealed class ActionableFailurePrivacyTests
{
    private const string PrivateSentinel = @"C:\Users\private\project\token=super-secret";

    [Test]
    public async Task Launch_actionable_failure_returns_structured_sanitized_failure()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        await using var mcp = await McpTestContext.StartAsync(serverExe);

        var result = await mcp.CallToolResultAsync("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = Path.Combine(PrivateSentinel, "missing.exe")
        });

        var failure = JsonSerializer.Deserialize<FailureInfo>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var text = result.Content.OfType<TextContentBlock>().Single().Text;

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure!.Code, Is.EqualTo("process_not_found"));
            Assert.That(failure.Stage, Is.EqualTo("process_discovery"));
            Assert.That(text, Does.Contain("process_not_found"));
            Assert.That(text, Does.Contain(failure.Detail));
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
        var failure = JsonSerializer.Deserialize<FailureInfo>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(failure!.Code, Is.EqualTo("process_not_found"));
            Assert.That(failure.Stage, Is.EqualTo("process_discovery"));
            Assert.That(failure.Retryable, Is.False);
            Assert.That(result.Content.OfType<TextContentBlock>().Single().Text, Does.Not.Contain(missingExecutable));
        });
    }

    [Test]
    public async Task Generic_mcp_error_mapping_ignores_raw_messages_around_actionable_failures()
    {
        var serverAssemblyPath = Path.ChangeExtension(
            McpServerPaths.FindMcpServerExecutable(),
            ".dll");
        var serverAssembly = Assembly.LoadFrom(serverAssemblyPath);
        var errorBoundary = serverAssembly.GetType(
            "WpfToolsMcp.McpServer.Tools.McpToolErrors",
            throwOnError: true)!;
        var runAsync = errorBoundary
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "RunAsync" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(string));
        var actionable = FailureDiagnostics.Exception(
            FailureDiagnostics.Codes.InjectionFailed,
            FailureDiagnostics.Stages.Injection,
            "The WPF backend could not be initialized in the target process.",
            retryable: false,
            recoveryActions: [FailureDiagnostics.Recovery.UseUia],
            inner: new InvalidOperationException(PrivateSentinel));
        var wrapped = new InvalidOperationException(
            "outer failure",
            new InvalidOperationException(PrivateSentinel, actionable));
        Func<Task<string>> action = () => Task.FromException<string>(wrapped);
        var task = (Task<string>)runAsync.Invoke(null, [action, "test_tool"])!;

        Exception? mapped = null;
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            mapped = ex;
        }

        Assert.Multiple(() =>
        {
            Assert.That(mapped, Is.Not.Null);
            Assert.That(
                mapped!.Message,
                Is.EqualTo(
                    "tool=test_tool: injection_failed: " +
                    "The WPF backend could not be initialized in the target process."));
            Assert.That(mapped.ToString(), Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public async Task Trace_records_only_actionable_code_and_safe_detail()
    {
        using var controller = new AutomationController();
        var traceStart = await controller.TraceStartAsync(resetIfRunning: false);
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
}
