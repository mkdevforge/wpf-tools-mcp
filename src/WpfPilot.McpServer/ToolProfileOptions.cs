namespace WpfPilot.McpServer;

internal enum ToolProfile
{
    Core,
    Diagnostics
}

internal static class ToolProfileOptions
{
    public static ToolProfile Parse(string[] args, string? environmentValue, out string[] hostArgs)
    {
        var remaining = new List<string>(args.Length);
        string? profileValue = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--tool-profile=", StringComparison.OrdinalIgnoreCase))
            {
                profileValue = arg["--tool-profile=".Length..];
                continue;
            }

            if (arg.Equals("--tool-profile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--tool-profile requires a value: core or diagnostics.");
                }

                profileValue = args[++i];
                continue;
            }

            remaining.Add(arg);
        }

        hostArgs = remaining.ToArray();
        return ParseProfile(profileValue ?? environmentValue);
    }

    private static ToolProfile ParseProfile(string? value)
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
