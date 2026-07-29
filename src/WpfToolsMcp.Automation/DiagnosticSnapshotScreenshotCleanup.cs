namespace WpfToolsMcp.Automation;

internal static class DiagnosticSnapshotScreenshotCleanup
{
    public static bool Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
