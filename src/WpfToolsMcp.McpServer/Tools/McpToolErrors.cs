using System.Runtime.CompilerServices;
using ModelContextProtocol;
using WpfToolsMcp.AgentProtocol;

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
            var baseException = ex.GetBaseException();
            var message = string.IsNullOrWhiteSpace(baseException.Message)
                ? baseException.GetType().Name
                : baseException.Message;
            var innerMessage = ex.InnerException is not null && !ReferenceEquals(ex.InnerException, baseException)
                ? ex.InnerException.Message
                : null;
            var code = GetTypedErrorCode(ex) ?? GetLegacyKnownErrorCode(message);
            var prefix = string.IsNullOrWhiteSpace(code) ||
                         message.StartsWith(code + ":", StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"{code}: ";
            var detail = string.IsNullOrWhiteSpace(innerMessage) ? "" : $" Inner: {innerMessage}";
            var tool = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName;

            throw new McpException($"tool={tool}: {prefix}{message}{detail}", baseException);
        }
    }

    private static string? GetTypedErrorCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IAgentErrorCodeException { Code: { } code } &&
                !string.IsNullOrWhiteSpace(code))
            {
                return code;
            }
        }

        return null;
    }

    private static string? GetLegacyKnownErrorCode(string message)
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
            _ => null
        };
    }
}
