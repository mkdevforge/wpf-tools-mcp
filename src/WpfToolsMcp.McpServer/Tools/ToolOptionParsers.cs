using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.McpServer.Tools;

internal static class ToolOptionParsers
{
    public static ClickType ParseClickType(string? clickType)
    {
        if (string.IsNullOrWhiteSpace(clickType))
        {
            return ClickType.Single;
        }

        var value = clickType.Trim();
        if (IsAny(value, "single", "left", "leftClick", "left_click"))
        {
            return ClickType.Single;
        }

        if (IsAny(value, "double", "doubleClick", "double_click"))
        {
            return ClickType.Double;
        }

        if (IsAny(value, "right", "rightClick", "right_click", "context", "contextMenu", "context_menu"))
        {
            return ClickType.Right;
        }

        throw new ArgumentException($"Unknown clickType '{clickType}'. Valid values: single, double, right.");
    }

    private static bool IsAny(string value, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
