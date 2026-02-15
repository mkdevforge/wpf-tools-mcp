using ModelContextProtocol;

namespace WpfPilot.McpServer.Tools;

internal static class McpToolErrors
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
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

            throw new McpException(message, baseException);
        }
    }
}

