using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Runtime.InteropServices;
using WpfToolsMcp.Automation;
using WpfToolsMcp.McpServer;
using WpfToolsMcp.McpServer.Tools;
using WpfToolsMcp.McpServer.Subscriptions;

EnablePerMonitorV2DpiAwareness();

var profile = ToolProfileOptions.Parse(args, Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_TOOL_PROFILE"), out var hostArgs);
var builder = Host.CreateApplicationBuilder(hostArgs);
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<SubscriptionManager>();

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

if (profile == ToolProfile.Diagnostics)
{
    mcpBuilder.WithToolsFromAssembly();
}
else
{
    mcpBuilder.WithTools((IEnumerable<Type>)CoreToolRegistry.ToolTypes);
}

await builder.Build().RunAsync();

static void EnablePerMonitorV2DpiAwareness()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    try
    {
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    }
    catch
    {
    }
}

[DllImport("user32.dll", SetLastError = true)]
static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
