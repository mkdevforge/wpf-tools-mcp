using WpfToolsMcp.AgentProtocol;

namespace WpfToolsMcp.Automation;

internal static class WpfAgentErrorClassifier
{
    public static bool IsStaleOrNotFound(Exception ex) =>
        HasCode(ex, AgentErrorCodes.WpfResolveNotFound, AgentErrorCodes.WpfHandleStale) ||
        HasLegacyPrefix(ex, "wpf_resolve:not_found:", "wpf_handle_stale:");

    public static bool IsResolveNotFound(Exception ex) =>
        HasCode(ex, AgentErrorCodes.WpfResolveNotFound) ||
        HasLegacyPrefix(ex, "wpf_resolve:not_found:");

    public static bool IsResolveAmbiguous(Exception ex) =>
        HasCode(ex, AgentErrorCodes.WpfResolveAmbiguous) ||
        HasLegacyPrefix(ex, "wpf_resolve:ambiguous:");

    public static bool IsLegacyTimeoutElementNotFound(Exception ex) =>
        GetMessage(ex).Contains("timeout: element not found", StringComparison.OrdinalIgnoreCase);

    public static string CleanLegacyPrefix(Exception ex, string prefix)
    {
        var firstLine = GetMessage(ex)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .FirstOrDefault() ?? string.Empty;

        var index = firstLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? firstLine[(index + prefix.Length)..].Trim()
            : firstLine.Trim();
    }

    private static bool HasCode(Exception ex, params string[] codes)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not IAgentErrorCodeException { Code: { } code } ||
                string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            if (codes.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLegacyPrefix(Exception ex, params string[] prefixes)
    {
        var message = GetMessage(ex);
        return prefixes.Any(prefix => message.Contains(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetMessage(Exception ex) =>
        ex.GetBaseException().Message ?? ex.Message ?? string.Empty;
}
