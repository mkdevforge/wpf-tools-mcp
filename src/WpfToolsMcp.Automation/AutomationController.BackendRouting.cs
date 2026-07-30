using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

public sealed partial class AutomationController
{
    internal enum AutoBackendRoute
    {
        Wpf,
        Uia,
        ProbeWpfThenUia
    }

    internal static AutoBackendRoute ClassifyAutoBackendRoute(FrameworkType frameworkType) =>
        frameworkType switch
        {
            FrameworkType.Wpf => AutoBackendRoute.Wpf,
            FrameworkType.Win32 or
            FrameworkType.WinForms or
            FrameworkType.Xaml or
            FrameworkType.Qt => AutoBackendRoute.Uia,
            FrameworkType.None or FrameworkType.Unknown => AutoBackendRoute.ProbeWpfThenUia,
            _ => AutoBackendRoute.ProbeWpfThenUia
        };

    internal static InspectionBackend SelectAutoBackend(
        AutoBackendRoute route,
        bool wpfBackendAvailable) =>
        route != AutoBackendRoute.Uia && wpfBackendAvailable
            ? InspectionBackend.Wpf
            : InspectionBackend.Uia;

    internal static TakeScreenshotResponse WithScreenshotRoutingMetadata(
        TakeScreenshotResponse response,
        bool hasElementTarget,
        InspectionBackend backendUsed,
        BackendFallbackInfo? fallback) =>
        response with
        {
            BackendUsed = hasElementTarget ? backendUsed : null,
            Fallback = hasElementTarget ? fallback : null
        };

    internal static bool IsPerWindowAutoWpfMiss(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = GetInternalFailureMessage(exception);
        return message.Contains("wpf_window_not_found:", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAutoWpfScopeMiss(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsPerWindowAutoWpfMiss(exception) ||
               exception is InvalidOperationException invalidOperation &&
               (IsAutoWpfLocatorMiss(invalidOperation) || IsAutoWpfLocatorAmbiguous(invalidOperation));
    }

    internal static bool ShouldRecordAutoAgentFailure(
        Exception exception,
        bool agentConnectionHealthy) =>
        !agentConnectionHealthy && !IsAutoWpfScopeMiss(exception);

    internal static string GetInternalFailureMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (current is AgentRemoteException remote &&
                !string.IsNullOrWhiteSpace(remote.RemoteMessage))
            {
                return remote.RemoteMessage;
            }

            current = current is ActionableFailureException { DiagnosticCause: { } diagnosticCause }
                ? diagnosticCause
                : current.InnerException;
        }

        return exception.GetBaseException().Message ?? exception.Message ?? string.Empty;
    }

    private static BackendFallbackInfo CreateWpfToUiaFallback(
        bool attempted,
        FailureInfo? failure = null) =>
        new(
            FromBackend: "wpf",
            ToBackend: "uia",
            Attempted: attempted,
            Available: true,
            Used: true)
        {
            Failure = failure
        };

    private static FailureInfo CreateWpfScopeFailure(string detail) =>
        FailureDiagnostics.BackendScopeUnavailable(detail);

    private FailureInfo ClassifyAutoWpfFallbackFailure(Exception exception)
    {
        var connectionHealthy = IsAgentConnected;
        var failure = IsAutoWpfScopeMiss(exception)
            ? CreateWpfScopeFailure("The requested scope is unavailable through the WPF backend.")
            : exception is AgentRemoteException
                ? FailureDiagnostics.BackendOperationFailure()
                : FailureDiagnostics.Classify(exception, FailureDiagnostics.Stages.Protocol);
        failure = PreferTargetStateFailure(failure);
        if (ShouldRecordAutoAgentFailure(exception, connectionHealthy))
        {
            SetAutoAgentFailure(failure);
        }

        return failure;
    }

    private static AutoBackendRoute GetAutoBackendRoute(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            return ClassifyAutoBackendRoute(window.FrameworkType);
        }
        catch
        {
            // An unavailable UIA framework property must retain the existing WPF-first fallback behavior.
            return AutoBackendRoute.ProbeWpfThenUia;
        }
    }
}
