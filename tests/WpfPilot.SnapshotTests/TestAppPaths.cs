namespace WpfPilot.SnapshotTests;

internal static class TestAppPaths
{
    public static string FindTestAppExecutable()
    {
        var repoRoot = RepoRoot.Find();
        var binRoot = Path.Combine(repoRoot, "src", "WpfPilot.TestApp", "bin");
        if (!Directory.Exists(binRoot))
        {
            throw new DirectoryNotFoundException($"Could not find test app output directory: '{binRoot}'.");
        }

        var candidates = Directory.EnumerateFiles(binRoot, "WpfPilot.TestApp.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains("net8.0-windows", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new FileNotFoundException($"Could not find WpfPilot.TestApp.exe under '{binRoot}'.");
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }
}
