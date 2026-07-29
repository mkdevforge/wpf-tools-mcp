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

    internal static bool IsPerWindowAutoWpfMiss(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.GetBaseException().Message ?? exception.Message ?? string.Empty;
        return message.Contains("wpf_window_not_found:", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldRecordAutoAgentFailure(Exception exception) =>
        !IsPerWindowAutoWpfMiss(exception);

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
