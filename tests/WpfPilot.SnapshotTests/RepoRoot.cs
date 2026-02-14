namespace WpfPilot.SnapshotTests;

internal static class RepoRoot
{
    public static string Find()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "WpfPilot.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new DirectoryNotFoundException("Could not locate repo root containing 'WpfPilot.slnx'.");
        }

        return current.FullName;
    }
}

