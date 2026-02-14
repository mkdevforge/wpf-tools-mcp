namespace WpfPilot.SnapshotTests;

internal static class TestAppPaths
{
    public static string FindExecutable(string projectFolderName, string exeName)
    {
        var repoRoot = RepoRoot.Find();
        var binRoot = Path.Combine(repoRoot, "src", projectFolderName, "bin");
        if (!Directory.Exists(binRoot))
        {
            throw new DirectoryNotFoundException($"Could not find test app output directory: '{binRoot}'.");
        }

        var candidates = Directory.EnumerateFiles(binRoot, $"{exeName}.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains("net8.0-windows", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new FileNotFoundException($"Could not find {exeName}.exe under '{binRoot}'.");
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    public static string FindTestAppExecutable() => FindExecutable("WpfPilot.TestApp", "WpfPilot.TestApp");

    public static string FindTabsTestAppExecutable() => FindExecutable("WpfPilot.TestApp.Tabs", "WpfPilot.TestApp.Tabs");

    public static string FindTreeViewTestAppExecutable() => FindExecutable("WpfPilot.TestApp.TreeView", "WpfPilot.TestApp.TreeView");
}
