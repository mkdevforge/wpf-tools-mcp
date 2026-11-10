namespace WpfToolsMcp.Automation;

/// <summary>
/// Default folders for files that tools write when the caller gives no path:
/// screenshots, screenshot sequences, and traces.
/// </summary>
public static class ArtifactLocations
{
    public const string ArtifactDirectoryVariable = "WPF_TOOLS_MCP_ARTIFACT_DIR";
    public const string ScreenshotDirectoryVariable = "WPF_TOOLS_MCP_SCREENSHOT_DIR";

    private static string? s_configuredArtifactDirectory;

    /// <summary>
    /// Sets the artifact folder from the command line. It overrides
    /// <see cref="ArtifactDirectoryVariable"/>.
    /// </summary>
    public static void Configure(string? artifactDirectory) =>
        Volatile.Write(
            ref s_configuredArtifactDirectory,
            string.IsNullOrWhiteSpace(artifactDirectory) ? null : Path.GetFullPath(artifactDirectory.Trim()));

    /// <summary>The configured artifact folder, or null when none is configured.</summary>
    public static string? ArtifactDirectory
    {
        get
        {
            var configured = Volatile.Read(ref s_configuredArtifactDirectory);
            if (configured is not null)
            {
                return configured;
            }

            var fromEnvironment = Environment.GetEnvironmentVariable(ArtifactDirectoryVariable);
            return string.IsNullOrWhiteSpace(fromEnvironment) ? null : Path.GetFullPath(fromEnvironment.Trim());
        }
    }

    /// <summary>
    /// Folder for screenshots and screenshot sequences: <see cref="ScreenshotDirectoryVariable"/>,
    /// then "screenshots" in the artifact folder, then %TEMP%\wpf-tools-mcp\screenshots.
    /// </summary>
    public static string GetScreenshotDirectory()
    {
        var screenshotDirectory = Environment.GetEnvironmentVariable(ScreenshotDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(screenshotDirectory))
        {
            return Path.GetFullPath(screenshotDirectory.Trim());
        }

        var artifactDirectory = ArtifactDirectory;
        return artifactDirectory is not null
            ? Path.Combine(artifactDirectory, "screenshots")
            : Path.Combine(Path.GetTempPath(), "wpf-tools-mcp", "screenshots");
    }

    /// <summary>Folder for trace files: "traces" in the artifact folder, then %TEMP%.</summary>
    public static string GetTraceDirectory()
    {
        var artifactDirectory = ArtifactDirectory;
        return artifactDirectory is not null
            ? Path.Combine(artifactDirectory, "traces")
            : Path.GetTempPath();
    }
}
