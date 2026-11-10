using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.Runtime.InteropServices;
using System.Text.Json;
using WpfToolsMcp.Automation;
using WpfToolsMcp.McpServer;
using WpfToolsMcp.McpServer.Tools;
using WpfToolsMcp.McpServer.Subscriptions;

EnablePerMonitorV2DpiAwareness();

ServerOptions options;
try
{
    options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
    ClientPathMap.Current = new ClientPathMap(options.PathMappings);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"wpf-tools-mcp: {ex.Message}");
    return 2;
}

ArtifactLocations.Configure(options.ArtifactDirectory);

if (options.Transport == ServerTransport.Http)
{
    await RunHttpAsync(options);
}
else
{
    var builder = Host.CreateApplicationBuilder(options.HostArgs);
    ConfigureLogging(builder.Logging);
    AddMcpServer(builder.Services, options.Profile).WithStdioServerTransport();
    await builder.Build().RunAsync();
}

return 0;

static async Task RunHttpAsync(ServerOptions options)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = options.HostArgs });
    ConfigureLogging(builder.Logging);
    builder.WebHost.UseUrls(options.HttpUrl.GetLeftPart(UriPartial.Authority));
    AddMcpServer(builder.Services, options.Profile).WithHttpTransport();

    var app = builder.Build();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WpfToolsMcp.Http");
    var guard = new HttpAccessGuard(options.HttpToken, logger);
    app.Use(guard.InvokeAsync);
    app.MapMcp(options.HttpUrl.AbsolutePath);

    logger.LogInformation(
        "Listening for MCP clients on {Url} ({Access}).",
        options.HttpUrl,
        options.HttpToken is null ? "loopback only, no token" : "bearer token required");
    foreach (var mapping in ClientPathMap.Current.Mappings)
    {
        logger.LogInformation("Path mapping: server {ServerPath} = client {ClientPath}.", mapping.ServerPath, mapping.ClientPath);
    }

    await app.RunAsync();
}

static void ConfigureLogging(ILoggingBuilder logging) =>
    logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

static IMcpServerBuilder AddMcpServer(IServiceCollection services, ToolProfile profile)
{
    services.AddSingleton<SessionManager>();
    services.AddSingleton<SubscriptionManager>();

    var mcpBuilder = services
        .AddMcpServer()
        .WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(McpToolOutputSchema.CreateListToolsFilter());
            filters.AddListToolsFilter(WaitToolSchema.CreateListToolsFilter());
            filters.AddListToolsFilter(DiagnosticSnapshotToolSchema.CreateListToolsFilter());
            filters.AddListToolsFilter(RealizeItemToolSchema.CreateListToolsFilter());
            filters.AddCallToolFilter(McpToolErrorFilter.CreateCallToolFilter());
        });
    var toolSerializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
    {
        AllowOutOfOrderMetadataProperties = true
    };

    if (profile == ToolProfile.Diagnostics)
    {
        mcpBuilder.WithToolsFromAssembly(serializerOptions: toolSerializerOptions);
    }
    else
    {
        mcpBuilder.WithTools((IEnumerable<Type>)CoreToolRegistry.ToolTypes, toolSerializerOptions);
    }

    return mcpBuilder;
}

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
