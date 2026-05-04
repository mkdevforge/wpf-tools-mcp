namespace WpfToolsMcp.McpServer.Tools;

internal static class CoreToolRegistry
{
    public static readonly Type[] ToolTypes =
    [
        typeof(CoreAppTools),
        typeof(CoreInspectionTools),
        typeof(CoreInteractionTools),
        typeof(CoreWpfDiagnosticsTools)
    ];
}
