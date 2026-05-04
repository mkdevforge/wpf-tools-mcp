namespace WpfToolsMcp.SnapshotTests;

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

    public static string FindTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp", "WpfToolsMcp.TestApp");

    public static string FindTabsTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp.Tabs", "WpfToolsMcp.TestApp.Tabs");

    public static string FindTreeViewTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp.TreeView", "WpfToolsMcp.TestApp.TreeView");

    public static string FindMinimalTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp.Minimal", "WpfToolsMcp.TestApp.Minimal");

    public static string FindDataGridTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp.DataGrid", "WpfToolsMcp.TestApp.DataGrid");

    public static string FindDialogsTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp.Dialogs", "WpfToolsMcp.TestApp.Dialogs");

    public static string FindDynamicContentTestAppExecutable() =>
        FindExecutable("WpfToolsMcp.TestApp.DynamicContent", "WpfToolsMcp.TestApp.DynamicContent");

    public static string FindDeeplyNestedTestAppExecutable() =>
        FindExecutable("WpfToolsMcp.TestApp.DeeplyNested", "WpfToolsMcp.TestApp.DeeplyNested");

    public static string FindCustomControlsTestAppExecutable() =>
        FindExecutable("WpfToolsMcp.TestApp.CustomControls", "WpfToolsMcp.TestApp.CustomControls");

    public static string FindScrollTestAppExecutable() => FindExecutable("WpfToolsMcp.TestApp.Scroll", "WpfToolsMcp.TestApp.Scroll");

    public static string FindBrokenAutomationTestAppExecutable() =>
        FindExecutable("WpfToolsMcp.TestApp.BrokenAutomation", "WpfToolsMcp.TestApp.BrokenAutomation");

    public static string FindBindingErrorsTestAppExecutable() =>
        FindExecutable("WpfToolsMcp.TestApp.BindingErrors", "WpfToolsMcp.TestApp.BindingErrors");
}
