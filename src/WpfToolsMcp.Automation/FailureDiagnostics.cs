using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class FailureDiagnostics
{
    private const int ShortRetryDelayMs = 1_000;
    private const int InjectionRetryDelayMs = 10_000;
    private const int MaximumDetailLength = 512;
    private const int ErrorAccessDenied = 5;
    private const int HResultAccessDenied = unchecked((int)0x80070005u);

    internal static class Stages
    {
        internal const string ProcessDiscovery = "process_discovery";
        internal const string Attachment = "attachment";
        internal const string ArchitectureDetection = "architecture_detection";
        internal const string Injection = "injection";
        internal const string PipeConnection = "pipe_connection";
        internal const string Protocol = "protocol";
        internal const string TargetShutdown = "target_shutdown";
    }

    internal static class Codes
    {
        internal const string ProcessNotFound = "process_not_found";
        internal const string StaleProcessCandidate = "stale_process_candidate";
        internal const string ProcessIdentityUnavailable = "process_identity_unavailable";
        internal const string ProcessDiscoveryFailed = "process_discovery_failed";
        internal const string ProcessStateUnavailable = "process_state_unavailable";
        internal const string AccessDenied = "access_denied";
        internal const string ElevationMismatch = "elevation_mismatch";
        internal const string AttachmentFailed = "attachment_failed";
        internal const string ArchitectureDetectionFailed = "architecture_detection_failed";
        internal const string UnsupportedArchitecture = "unsupported_architecture";
        internal const string BackendAssetsMissing = "backend_assets_missing";
        internal const string OperationTimeout = "operation_timeout";
        internal const string InjectionTimeout = "injection_timeout";
        internal const string InjectorCrashed = "injector_crashed";
        internal const string InjectionFailed = "injection_failed";
        internal const string AgentConnectionTimeout = "agent_connection_timeout";
        internal const string AgentConnectionFailed = "agent_connection_failed";
        internal const string ProtocolMismatch = "protocol_mismatch";
        internal const string ProtocolError = "protocol_error";
        internal const string BackendScopeUnavailable = "backend_scope_unavailable";
        internal const string BackendOperationFailed = "backend_operation_failed";
        internal const string AgentUnresponsive = "agent_unresponsive";
        internal const string TargetExited = "target_exited";
        internal const string ProcessReplaced = "process_replaced";
        internal const string UnexpectedFailure = "unexpected_failure";
    }

    internal static class Recovery
    {
        internal const string Retry = "retry";
        internal const string UseUia = "use_uia";
        internal const string Reattach = "reattach";
        internal const string RestartTarget = "restart_target";
        internal const string MatchElevation = "match_elevation";
        internal const string UseSupportedArchitecture = "use_supported_architecture";
        internal const string RepairInstallation = "repair_installation";
        internal const string SelectProcessInstance = "select_process_instance";
    }

    internal static ActionableFailureException CreateException(
        Exception exception,
        string stage,
        ProcessIntegrityLevelComparison? integrityComparison = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ActionableFailureException(
            Classify(exception, stage, integrityComparison),
            exception);
    }

    internal static ActionableFailureException Exception(
        string code,
        string stage,
        string detail,
        bool? retryable,
        IReadOnlyList<string>? recoveryActions = null,
        int? retryAfterMs = null,
        Exception? inner = null)
    {
        var failure = Create(
            code,
            stage,
            detail,
            retryable,
            recoveryActions,
            retryAfterMs);
        return inner is null
            ? new ActionableFailureException(failure)
            : new ActionableFailureException(failure, inner);
    }

    internal static FailureInfo Classify(Exception exception, string stage) =>
        Classify(exception, stage, integrityComparison: null);

    internal static FailureInfo Classify(
        Exception exception,
        string stage,
        ProcessIntegrityLevelComparison? integrityComparison)
    {
        ArgumentNullException.ThrowIfNull(exception);
        EnsureKnownStage(stage);

        if (exception is ActionableFailureException actionable)
        {
            return actionable.Failure;
        }

        var cause = exception.GetBaseException();
        if (cause is ActionableFailureException actionableCause)
        {
            return actionableCause.Failure;
        }

        if (IsAccessDenied(cause))
        {
            return AccessDenied(stage, integrityComparison);
        }

        if (cause is TimeoutException)
        {
            return Timeout(stage);
        }

        if (stage == Stages.ProcessDiscovery)
        {
            return ProcessDiscovery(cause, integrityComparison);
        }

        if (stage == Stages.TargetShutdown)
        {
            return TargetExited();
        }

        if (stage == Stages.Injection && cause is FileNotFoundException or DirectoryNotFoundException)
        {
            return MissingAssets();
        }

        if ((stage == Stages.ArchitectureDetection || stage == Stages.Injection) &&
            cause is NotSupportedException)
        {
            return UnsupportedArchitecture();
        }

        return stage switch
        {
            Stages.Attachment => AttachmentFailure(),
            Stages.ArchitectureDetection => ArchitectureDetectionFailure(),
            Stages.Injection => InjectionFailure(),
            Stages.PipeConnection => PipeFailure(),
            Stages.Protocol => ProtocolFailure(cause),
            _ => UnexpectedFailure(stage)
        };
    }

    internal static FailureInfo MissingAssets() =>
        Create(
            Codes.BackendAssetsMissing,
            Stages.Injection,
            "Required WPF backend files are unavailable.",
            retryable: false,
            recoveryActions: [Recovery.UseUia, Recovery.RepairInstallation]);

    internal static FailureInfo UnsupportedArchitecture() =>
        Create(
            Codes.UnsupportedArchitecture,
            Stages.ArchitectureDetection,
            "The target process architecture is not supported by the WPF backend.",
            retryable: false,
            recoveryActions: [Recovery.UseUia, Recovery.UseSupportedArchitecture]);

    internal static FailureInfo AccessDenied(
        string stage,
        ProcessIntegrityLevelComparison? integrityComparison = null)
    {
        EnsureKnownStage(stage);
        var measuredElevationMismatch = integrityComparison == ProcessIntegrityLevelComparison.TargetHigher;
        var actions = SupportsUiaFallback(stage)
            ? new[] { Recovery.UseUia, Recovery.MatchElevation }
            : new[] { Recovery.MatchElevation };

        return Create(
            measuredElevationMismatch ? Codes.ElevationMismatch : Codes.AccessDenied,
            stage,
            measuredElevationMismatch
                ? "The target process has a higher measured integrity level than the MCP host."
                : "Access to the target process was denied.",
            retryable: false,
            recoveryActions: actions);
    }

    internal static FailureInfo Timeout(string stage)
    {
        EnsureKnownStage(stage);
        return stage switch
        {
            Stages.Injection => Create(
                Codes.InjectionTimeout,
                stage,
                "The WPF injector did not complete before the timeout.",
                retryable: true,
                retryAfterMs: InjectionRetryDelayMs,
                recoveryActions: [Recovery.UseUia, Recovery.Retry]),
            Stages.PipeConnection => Create(
                Codes.AgentConnectionTimeout,
                stage,
                "The WPF agent did not accept a pipe connection before the timeout.",
                retryable: true,
                retryAfterMs: ShortRetryDelayMs,
                recoveryActions: [Recovery.UseUia, Recovery.Retry]),
            Stages.Protocol => Create(
                Codes.AgentUnresponsive,
                stage,
                "The WPF agent did not respond before the timeout.",
                retryable: true,
                retryAfterMs: ShortRetryDelayMs,
                recoveryActions: [Recovery.UseUia, Recovery.Retry, Recovery.RestartTarget]),
            _ => Create(
                Codes.OperationTimeout,
                stage,
                "The operation did not complete before the timeout.",
                retryable: true,
                retryAfterMs: ShortRetryDelayMs,
                recoveryActions: [Recovery.Retry])
        };
    }

    internal static FailureInfo InjectorExit(int exitCode)
    {
        if (exitCode == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exitCode), "A successful injector exit is not a failure.");
        }

        var crashed = unchecked((uint)exitCode) >= 0xC0000000u;
        return crashed
            ? Create(
                Codes.InjectorCrashed,
                Stages.Injection,
                "The WPF injector process terminated unexpectedly.",
                retryable: false,
                recoveryActions: [Recovery.UseUia, Recovery.RestartTarget])
            : InjectionFailure();
    }

    internal static FailureInfo ClassifyInjectorFailure(
        InjectionRunResult result,
        ProcessIntegrityLevelComparison? integrityComparison = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode is ErrorAccessDenied or
            unchecked((int)0x80070005u) or
            unchecked((int)0xC0000022u))
        {
            return AccessDenied(Stages.Injection, integrityComparison);
        }

        return InjectorExit(result.ExitCode);
    }

    internal static FailureInfo PipeFailure() =>
        Create(
            Codes.AgentConnectionFailed,
            Stages.PipeConnection,
            "The WPF agent pipe could not be connected.",
            retryable: true,
            retryAfterMs: ShortRetryDelayMs,
            recoveryActions: [Recovery.UseUia, Recovery.Retry]);

    internal static FailureInfo ProtocolMismatch() =>
        Create(
            Codes.ProtocolMismatch,
            Stages.Protocol,
            "The WPF agent uses an incompatible protocol version.",
            retryable: false,
            recoveryActions: [Recovery.UseUia, Recovery.RestartTarget]);

    internal static FailureInfo ProtocolFailure(Exception? exception = null)
    {
        if (exception is TimeoutException)
        {
            return Timeout(Stages.Protocol);
        }

        return Create(
            Codes.ProtocolError,
            Stages.Protocol,
            "The WPF agent returned an invalid protocol response.",
            retryable: true,
            retryAfterMs: ShortRetryDelayMs,
            recoveryActions: [Recovery.UseUia, Recovery.Retry, Recovery.RestartTarget]);
    }

    internal static FailureInfo BackendOperationFailure() =>
        Create(
            Codes.BackendOperationFailed,
            Stages.Protocol,
            "The WPF backend could not complete the requested operation.",
            retryable: null,
            recoveryActions: [Recovery.UseUia]);

    internal static FailureInfo BackendScopeUnavailable(string detail) =>
        Create(
            Codes.BackendScopeUnavailable,
            Stages.Protocol,
            detail,
            retryable: false,
            recoveryActions: [Recovery.UseUia]);

    internal static FailureInfo TargetExited(bool processReplaced = false) =>
        processReplaced
            ? Create(
                Codes.ProcessReplaced,
                Stages.TargetShutdown,
                "The attached process identity was replaced by a different process.",
                retryable: false,
                recoveryActions: [Recovery.Reattach])
            : Create(
                Codes.TargetExited,
                Stages.TargetShutdown,
                "The target process exited before the operation completed.",
                retryable: false,
                recoveryActions: [Recovery.RestartTarget, Recovery.Reattach]);

    internal static FailureInfo ProcessNotFound() =>
        Create(
            Codes.ProcessNotFound,
            Stages.ProcessDiscovery,
            "No live process matched the requested target.",
            retryable: false,
            recoveryActions: [Recovery.RestartTarget, Recovery.Reattach]);

    internal static FailureInfo ProcessIdentityUnavailable() =>
        Create(
            Codes.ProcessIdentityUnavailable,
            Stages.ProcessDiscovery,
            "A stable identity for the target process could not be established.",
            retryable: true,
            retryAfterMs: ShortRetryDelayMs,
            recoveryActions: [Recovery.Retry]);

    internal static FailureInfo ProcessDiscovery(
        Exception exception,
        ProcessIntegrityLevelComparison? integrityComparison = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsAccessDenied(exception))
        {
            return AccessDenied(Stages.ProcessDiscovery, integrityComparison);
        }

        if (exception is ArgumentException)
        {
            return ProcessNotFound();
        }

        return Create(
            Codes.ProcessDiscoveryFailed,
            Stages.ProcessDiscovery,
            "The target process could not be discovered.",
            retryable: true,
            retryAfterMs: ShortRetryDelayMs,
            recoveryActions: [Recovery.Retry]);
    }

    internal static FailureInfo AttachmentFailure() =>
        Create(
            Codes.AttachmentFailed,
            Stages.Attachment,
            "The target process could not be attached for UI automation.",
            retryable: true,
            retryAfterMs: ShortRetryDelayMs,
            recoveryActions: [Recovery.Retry, Recovery.Reattach]);

    internal static FailureInfo ArchitectureDetectionFailure() =>
        Create(
            Codes.ArchitectureDetectionFailed,
            Stages.ArchitectureDetection,
            "The target process architecture could not be determined.",
            retryable: false,
            recoveryActions: [Recovery.UseUia, Recovery.UseSupportedArchitecture]);

    internal static FailureInfo InjectionFailure() =>
        Create(
            Codes.InjectionFailed,
            Stages.Injection,
            "The WPF backend could not be initialized in the target process.",
            retryable: false,
            recoveryActions: [Recovery.UseUia, Recovery.RestartTarget]);

    private static FailureInfo UnexpectedFailure(string stage) =>
        Create(
            Codes.UnexpectedFailure,
            stage,
            "The operation failed unexpectedly.");

    internal static FailureInfo Create(
        string code,
        string stage,
        string detail,
        bool? retryable = null,
        IReadOnlyList<string>? recoveryActions = null,
        int? retryAfterMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        EnsureStableCode(code, nameof(code));
        EnsureKnownStage(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (retryAfterMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterMs));
        }

        IReadOnlyList<string>? copiedRecoveryActions = null;
        if (recoveryActions is not null)
        {
            var actions = recoveryActions.ToArray();
            foreach (var action in actions)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(action);
                EnsureStableCode(action, nameof(recoveryActions));
            }

            copiedRecoveryActions = Array.AsReadOnly(actions);
        }

        var boundedDetail = detail.Trim();
        if (boundedDetail.Length > MaximumDetailLength)
        {
            boundedDetail = boundedDetail[..MaximumDetailLength];
        }

        return new FailureInfo(code, stage, boundedDetail)
        {
            Retryable = retryable,
            RetryAfterMs = retryAfterMs,
            RecoveryActions = copiedRecoveryActions
        };
    }

    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException ||
        exception is Win32Exception { NativeErrorCode: ErrorAccessDenied } ||
        exception is COMException { HResult: HResultAccessDenied };

    private static bool SupportsUiaFallback(string stage) =>
        stage is Stages.ArchitectureDetection or Stages.Injection or Stages.PipeConnection or Stages.Protocol;

    private static void EnsureKnownStage(string stage)
    {
        if (stage is not (Stages.ProcessDiscovery or
            Stages.Attachment or
            Stages.ArchitectureDetection or
            Stages.Injection or
            Stages.PipeConnection or
            Stages.Protocol or
            Stages.TargetShutdown))
        {
            throw new ArgumentException($"Unknown failure stage '{stage}'.", nameof(stage));
        }
    }

    private static void EnsureStableCode(string value, string parameterName)
    {
        if (value[0] == '_' ||
            value[^1] == '_' ||
            value.Contains("__", StringComparison.Ordinal) ||
            value.Any(character =>
                character != '_' &&
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException(
                "Failure codes and recovery actions must use lower_snake_case ASCII.",
                parameterName);
        }
    }
}
