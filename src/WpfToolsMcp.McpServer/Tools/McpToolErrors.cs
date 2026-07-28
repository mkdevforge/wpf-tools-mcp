using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static class McpToolErrors
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action, [CallerMemberName] string toolName = "")
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateMcpException(ex, toolName);
        }
    }

    public static async Task<CallToolResult> RunResolveElementAsync(
        Func<Task<ResolveElementResponse>> action,
        [CallerMemberName] string toolName = "")
    {
        try
        {
            var response = await action().ConfigureAwait(false);
            var structuredContent = JsonSerializer.SerializeToNode(
                response,
                McpJsonUtilities.DefaultOptions);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = structuredContent?.ToJsonString(McpJsonUtilities.DefaultOptions)
                            ?? "null"
                    }
                ],
                StructuredContent = structuredContent
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (ElementResolutionAmbiguityException ex)
        {
            var tool = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName;
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"tool={tool}: {ex.Message}" }],
                StructuredContent = JsonSerializer.SerializeToNode(
                    ex.Ambiguity,
                    McpJsonUtilities.DefaultOptions)
            };
        }
        catch (Exception ex)
        {
            throw CreateMcpException(ex, toolName);
        }
    }

    private static McpException CreateMcpException(Exception ex, string toolName)
    {
        var baseException = ex.GetBaseException();
        var message = string.IsNullOrWhiteSpace(baseException.Message)
            ? baseException.GetType().Name
            : baseException.Message;
        var innerMessage = ex.InnerException is not null && !ReferenceEquals(ex.InnerException, baseException)
            ? ex.InnerException.Message
            : null;
        var code = GetKnownErrorCode(message);
        var prefix = string.IsNullOrWhiteSpace(code) ||
                     message.StartsWith(code + ":", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"{code}: ";
        var detail = string.IsNullOrWhiteSpace(innerMessage) ? "" : $" Inner: {innerMessage}";
        var tool = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName;

        return new McpException($"tool={tool}: {prefix}{message}{detail}", baseException);
    }

    private static string? GetKnownErrorCode(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var separators = new[] { ':', ' ' };
        var first = message.Split(separators, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        return first switch
        {
            "stale_element" => first,
            "timeout" => first,
            "element_offscreen" => first,
            "element_offscreen_after_scroll" => first,
            "wpf_handle_stale" => first,
            "no_hit_at_point" => first,
            "invalid_request" => first,
            "ambiguous_element" => first,
            "interaction_policy_blocked" => first,
            "screenshot_viewport_unstable" => first,
            "viewport_conditions_unstable" => first,
            "dpi_context_unavailable" => first,
            "monitor_dpi_unavailable" => first,
            _ => null
        };
    }
}
