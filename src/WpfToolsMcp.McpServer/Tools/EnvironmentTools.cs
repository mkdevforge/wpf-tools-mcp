using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

[McpServerToolType]
public static class EnvironmentTools
{
    [McpServerTool(Name = "get_server_environment", UseStructuredContent = true), Description("Report the machine and session the server runs in: whether physical input and screen capture can work (interactiveDesktop), displays and adapters, OS, firmware system identity, and WPF render tier. Call first when the server runs on another machine or VM.")]
    public static Task<GetServerEnvironmentResponse> GetServerEnvironment(
        CancellationToken cancellationToken = default) =>
        McpToolErrors.RunAsync(() => ServerEnvironmentDiagnostics.CollectAsync(cancellationToken));
}
