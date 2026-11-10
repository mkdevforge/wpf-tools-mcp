namespace WpfToolsMcp.McpServer;

internal enum ToolProfile
{
    Core,
    Diagnostics
}

internal static class ToolProfileOptions
{
    public static ToolProfile ParseProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ToolProfile.Core;
        }

        value = value.Trim();
        if (value.Equals("core", StringComparison.OrdinalIgnoreCase))
        {
            return ToolProfile.Core;
        }

        if (value.Equals("diagnostics", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("diagnostic", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return ToolProfile.Diagnostics;
        }

        throw new ArgumentException($"Unknown tool profile '{value}'. Valid values: core, diagnostics.");
    }
}
