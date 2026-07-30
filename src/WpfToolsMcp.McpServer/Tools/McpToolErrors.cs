using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static class McpToolErrors
{
    public static Task<T> RunAsync<T>(Func<Task<T>> action) => action();

    public static Task<CallToolResult> RunResolveElementAsync(
        Func<Task<ResolveElementResponse>> action) =>
        RunStructuredSuccessAsync(action);

    public static Task<CallToolResult> RunAttachToAppAsync(
        Func<Task<AttachToAppResponse>> action) =>
        RunStructuredSuccessAsync(action);

    public static Task<CallToolResult> RunLaunchAppAsync(
        Func<Task<LaunchAppResponse>> action) =>
        RunStructuredSuccessAsync(action);

    private static async Task<CallToolResult> RunStructuredSuccessAsync<TResponse>(
        Func<Task<TResponse>> action)
    {
        var response = await action().ConfigureAwait(false);
        var structuredContent = JsonSerializer.SerializeToElement(
            response,
            McpJsonUtilities.DefaultOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structuredContent.GetRawText() }],
            StructuredContent = structuredContent
        };
    }
}
