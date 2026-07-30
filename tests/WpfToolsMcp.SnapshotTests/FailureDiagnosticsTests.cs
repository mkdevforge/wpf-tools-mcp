using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

public sealed class FailureDiagnosticsTests
{
    private const string PrivateSentinel = @"C:\Users\private\project\token=super-secret";

    [Test]
    public void Auto_agent_retry_gate_honors_transient_retry_delay()
    {
        var recordedAt = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var transient = FailureDiagnostics.Create(
            FailureDiagnostics.Codes.AgentConnectionTimeout,
            FailureDiagnostics.Stages.PipeConnection,
            "The WPF agent did not accept a pipe connection before the timeout.",
            retryable: true,
            retryAfterMs: 1_000,
            recoveryActions: [FailureDiagnostics.Recovery.UseUia, FailureDiagnostics.Recovery.Retry]);
        var permanent = FailureDiagnostics.MissingAssets();

        Assert.Multiple(() =>
        {
            Assert.That(
                AutomationController.ShouldRetryAutoAgentConnection(
                    transient,
                    recordedAt,
                    recordedAt.AddMilliseconds(999)),
                Is.False);
            Assert.That(
                AutomationController.ShouldRetryAutoAgentConnection(
                    transient,
                    recordedAt,
                    recordedAt.AddMilliseconds(1_000)),
                Is.True);
            Assert.That(
                AutomationController.ShouldRetryAutoAgentConnection(
                    permanent,
                    recordedAt,
                    recordedAt.AddDays(1)),
                Is.False);
        });
    }

    [Test]
    public void Missing_assets_are_classified_without_exposing_the_path()
    {
        var failure = FailureDiagnostics.Classify(
            new DirectoryNotFoundException(PrivateSentinel),
            FailureDiagnostics.Stages.Injection);

        AssertFailure(
            failure,
            FailureDiagnostics.Codes.BackendAssetsMissing,
            FailureDiagnostics.Stages.Injection,
            retryable: false,
            FailureDiagnostics.Recovery.UseUia,
            FailureDiagnostics.Recovery.RepairInstallation);
        Assert.That(JsonSerializer.Serialize(failure), Does.Not.Contain(PrivateSentinel));
    }

    [Test]
    public void Unsupported_architecture_is_explicit_and_not_blindly_retryable()
    {
        var failure = FailureDiagnostics.Classify(
            new PlatformNotSupportedException(PrivateSentinel),
            FailureDiagnostics.Stages.ArchitectureDetection);

        AssertFailure(
            failure,
            FailureDiagnostics.Codes.UnsupportedArchitecture,
            FailureDiagnostics.Stages.ArchitectureDetection,
            retryable: false,
            FailureDiagnostics.Recovery.UseUia,
            FailureDiagnostics.Recovery.UseSupportedArchitecture);
    }

    [Test]
    public void Access_denied_only_claims_elevation_mismatch_when_target_is_measured_higher()
    {
        var unmeasured = FailureDiagnostics.Classify(
            new Win32Exception(5, PrivateSentinel),
            FailureDiagnostics.Stages.Injection);
        var sameIntegrity = FailureDiagnostics.Classify(
            new UnauthorizedAccessException(PrivateSentinel),
            FailureDiagnostics.Stages.Injection,
            ProcessIntegrityLevelComparison.Same);
        var targetHigher = FailureDiagnostics.Classify(
            new UnauthorizedAccessException(PrivateSentinel),
            FailureDiagnostics.Stages.Attachment,
            ProcessIntegrityLevelComparison.TargetHigher);

        Assert.Multiple(() =>
        {
            Assert.That(unmeasured.Code, Is.EqualTo(FailureDiagnostics.Codes.AccessDenied));
            Assert.That(sameIntegrity.Code, Is.EqualTo(FailureDiagnostics.Codes.AccessDenied));
            Assert.That(targetHigher.Code, Is.EqualTo(FailureDiagnostics.Codes.ElevationMismatch));
            Assert.That(targetHigher.Stage, Is.EqualTo(FailureDiagnostics.Stages.Attachment));
            Assert.That(targetHigher.Detail, Does.Contain("measured integrity level"));
            Assert.That(targetHigher.Detail, Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public void Com_access_denied_hresult_uses_measured_integrity_without_exposing_the_message()
    {
        var unmeasured = FailureDiagnostics.Classify(
            new COMException(PrivateSentinel, unchecked((int)0x80070005u)),
            FailureDiagnostics.Stages.Attachment);
        var targetHigher = FailureDiagnostics.Classify(
            new COMException(PrivateSentinel, unchecked((int)0x80070005u)),
            FailureDiagnostics.Stages.Attachment,
            ProcessIntegrityLevelComparison.TargetHigher);

        Assert.Multiple(() =>
        {
            Assert.That(unmeasured.Code, Is.EqualTo(FailureDiagnostics.Codes.AccessDenied));
            Assert.That(targetHigher.Code, Is.EqualTo(FailureDiagnostics.Codes.ElevationMismatch));
            Assert.That(unmeasured.Stage, Is.EqualTo(FailureDiagnostics.Stages.Attachment));
            Assert.That(targetHigher.Stage, Is.EqualTo(FailureDiagnostics.Stages.Attachment));
            Assert.That(JsonSerializer.Serialize(unmeasured), Does.Not.Contain(PrivateSentinel));
            Assert.That(JsonSerializer.Serialize(targetHigher), Does.Not.Contain(PrivateSentinel));
        });
    }

    [TestCase("injection", "injection_timeout", 10_000)]
    [TestCase("pipe_connection", "agent_connection_timeout", 1_000)]
    [TestCase("protocol", "agent_unresponsive", 1_000)]
    public void Timeout_classification_preserves_the_failure_stage(
        string stage,
        string expectedCode,
        int expectedRetryAfterMs)
    {
        var failure = FailureDiagnostics.Classify(new TimeoutException(PrivateSentinel), stage);

        Assert.Multiple(() =>
        {
            Assert.That(failure.Code, Is.EqualTo(expectedCode));
            Assert.That(failure.Stage, Is.EqualTo(stage));
            Assert.That(failure.Retryable, Is.True);
            Assert.That(failure.RetryAfterMs, Is.EqualTo(expectedRetryAfterMs));
            Assert.That(failure.Detail, Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public void Injector_exit_distinguishes_crashes_from_reported_failures_without_output_leaks()
    {
        var crash = FailureDiagnostics.ClassifyInjectorFailure(
            new InjectionRunResult(unchecked((int)0xE0434352u), PrivateSentinel, PrivateSentinel));
        var reportedFailure = FailureDiagnostics.ClassifyInjectorFailure(
            new InjectionRunResult(7, PrivateSentinel, PrivateSentinel));

        Assert.Multiple(() =>
        {
            Assert.That(crash.Code, Is.EqualTo(FailureDiagnostics.Codes.InjectorCrashed));
            Assert.That(crash.Retryable, Is.False);
            Assert.That(reportedFailure.Code, Is.EqualTo(FailureDiagnostics.Codes.InjectionFailed));
            Assert.That(reportedFailure.Retryable, Is.False);
            Assert.That(JsonSerializer.Serialize(crash), Does.Not.Contain(PrivateSentinel));
            Assert.That(JsonSerializer.Serialize(reportedFailure), Does.Not.Contain(PrivateSentinel));
        });
    }

    [TestCase(5)]
    [TestCase(unchecked((int)0x80070005u))]
    [TestCase(unchecked((int)0xC0000022u))]
    public void Injector_access_denied_exit_uses_measured_integrity_without_parsing_output(int exitCode)
    {
        var result = new InjectionRunResult(exitCode, PrivateSentinel, PrivateSentinel);
        var unmeasured = FailureDiagnostics.ClassifyInjectorFailure(result);
        var targetHigher = FailureDiagnostics.ClassifyInjectorFailure(
            result,
            ProcessIntegrityLevelComparison.TargetHigher);

        Assert.Multiple(() =>
        {
            Assert.That(unmeasured.Code, Is.EqualTo(FailureDiagnostics.Codes.AccessDenied));
            Assert.That(targetHigher.Code, Is.EqualTo(FailureDiagnostics.Codes.ElevationMismatch));
            Assert.That(unmeasured.Stage, Is.EqualTo(FailureDiagnostics.Stages.Injection));
            Assert.That(JsonSerializer.Serialize(targetHigher), Does.Not.Contain(PrivateSentinel));
        });
    }

    [Test]
    public void Pipe_protocol_shutdown_and_discovery_categories_are_distinct()
    {
        var pipe = FailureDiagnostics.Classify(
            new IOException(PrivateSentinel),
            FailureDiagnostics.Stages.PipeConnection);
        var protocol = FailureDiagnostics.Classify(
            new JsonException(PrivateSentinel),
            FailureDiagnostics.Stages.Protocol);
        var exited = FailureDiagnostics.TargetExited();
        var replaced = FailureDiagnostics.TargetExited(processReplaced: true);
        var missing = FailureDiagnostics.ProcessDiscovery(new ArgumentException(PrivateSentinel));
        var discovery = FailureDiagnostics.ProcessDiscovery(new InvalidOperationException(PrivateSentinel));

        Assert.Multiple(() =>
        {
            Assert.That(pipe.Code, Is.EqualTo(FailureDiagnostics.Codes.AgentConnectionFailed));
            Assert.That(protocol.Code, Is.EqualTo(FailureDiagnostics.Codes.ProtocolError));
            Assert.That(exited.Code, Is.EqualTo(FailureDiagnostics.Codes.TargetExited));
            Assert.That(replaced.Code, Is.EqualTo(FailureDiagnostics.Codes.ProcessReplaced));
            Assert.That(missing.Code, Is.EqualTo(FailureDiagnostics.Codes.ProcessNotFound));
            Assert.That(discovery.Code, Is.EqualTo(FailureDiagnostics.Codes.ProcessDiscoveryFailed));
        });
    }

    [Test]
    public void Backend_scope_miss_is_not_reported_as_an_attachment_failure()
    {
        var failure = FailureDiagnostics.BackendScopeUnavailable(
            "The requested scope is unavailable through the WPF backend.");

        AssertFailure(
            failure,
            FailureDiagnostics.Codes.BackendScopeUnavailable,
            FailureDiagnostics.Stages.Protocol,
            retryable: false,
            FailureDiagnostics.Recovery.UseUia);
    }

    [Test]
    public void Integration_factory_bounds_detail_and_keeps_the_raw_cause_out_of_exception_text()
    {
        var oversizedDetail = new string('a', 700);
        var exception = FailureDiagnostics.Exception(
            FailureDiagnostics.Codes.InjectionFailed,
            FailureDiagnostics.Stages.Injection,
            oversizedDetail,
            retryable: null,
            recoveryActions: [FailureDiagnostics.Recovery.UseUia],
            inner: new InvalidOperationException(PrivateSentinel));

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.InstanceOf<InvalidOperationException>());
            Assert.That(exception.Failure.Detail, Has.Length.EqualTo(512));
            Assert.That(exception.Message, Does.Not.Contain(PrivateSentinel));
            Assert.That(exception.GetBaseException(), Is.SameAs(exception));
            Assert.That(exception.Failure.RecoveryActions, Is.EqualTo(new[] { "use_uia" }));
        });
    }

    [Test]
    public void Public_codes_stages_and_actions_are_lower_snake_case()
    {
        var failure = FailureDiagnostics.Create(
            "agent_connection_failed",
            "pipe_connection",
            "The WPF agent pipe could not be connected.",
            retryable: true,
            recoveryActions: ["use_uia", "retry"],
            retryAfterMs: 1_000);

        Assert.Multiple(() =>
        {
            Assert.That(failure.Code, Does.Match("^[a-z0-9]+(?:_[a-z0-9]+)*$"));
            Assert.That(failure.Stage, Does.Match("^[a-z0-9]+(?:_[a-z0-9]+)*$"));
            Assert.That(failure.RecoveryActions, Is.Not.Null);
            Assert.That(failure.RecoveryActions!.All(IsLowerSnakeCase), Is.True);
        });
    }

    private static void AssertFailure(
        WpfToolsMcp.Contracts.FailureInfo failure,
        string code,
        string stage,
        bool? retryable,
        params string[] recoveryActions)
    {
        Assert.Multiple(() =>
        {
            Assert.That(failure.Code, Is.EqualTo(code));
            Assert.That(failure.Stage, Is.EqualTo(stage));
            Assert.That(failure.Retryable, Is.EqualTo(retryable));
            Assert.That(failure.RecoveryActions, Is.EqualTo(recoveryActions));
            Assert.That(failure.Detail, Does.Not.Contain(PrivateSentinel));
        });
    }

    private static bool IsLowerSnakeCase(string value) =>
        value.Length > 0 &&
        value.All(character =>
            character == '_' ||
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character));
}
