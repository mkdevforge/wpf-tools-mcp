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

    public static string FindMinimalTestAppExecutable() => FindExecutable("WpfPilot.TestApp.Minimal", "WpfPilot.TestApp.Minimal");

    public static string FindDataGridTestAppExecutable() => FindExecutable("WpfPilot.TestApp.DataGrid", "WpfPilot.TestApp.DataGrid");

    public static string FindDialogsTestAppExecutable() => FindExecutable("WpfPilot.TestApp.Dialogs", "WpfPilot.TestApp.Dialogs");

    public static string FindDynamicContentTestAppExecutable() =>
        FindExecutable("WpfPilot.TestApp.DynamicContent", "WpfPilot.TestApp.DynamicContent");

    public static string FindDeeplyNestedTestAppExecutable() =>
        FindExecutable("WpfPilot.TestApp.DeeplyNested", "WpfPilot.TestApp.DeeplyNested");

    public static string FindCustomControlsTestAppExecutable() =>
        FindExecutable("WpfPilot.TestApp.CustomControls", "WpfPilot.TestApp.CustomControls");

    public static string FindScrollTestAppExecutable() => FindExecutable("WpfPilot.TestApp.Scroll", "WpfPilot.TestApp.Scroll");

    public static string FindBrokenAutomationTestAppExecutable() =>
        FindExecutable("WpfPilot.TestApp.BrokenAutomation", "WpfPilot.TestApp.BrokenAutomation");

    public static string FindBindingErrorsTestAppExecutable() =>
        FindExecutable("WpfPilot.TestApp.BindingErrors", "WpfPilot.TestApp.BindingErrors");
}
