namespace WpfToolsMcp.SnapshotTests;

internal static class McpServerPaths
{
    public static string FindMcpServerExecutable()
    {
        var repoRoot = RepoRoot.Find();
        var binRoot = Path.Combine(repoRoot, "src", "WpfToolsMcp.McpServer", "bin");
        if (!Directory.Exists(binRoot))
        {
            throw new DirectoryNotFoundException($"Could not find MCP server output directory: '{binRoot}'.");
        }

        var candidates = Directory.EnumerateFiles(binRoot, "WpfToolsMcp.McpServer.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains("net8.0-windows", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new FileNotFoundException($"Could not find WpfToolsMcp.McpServer.exe under '{binRoot}'.");
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }
}

