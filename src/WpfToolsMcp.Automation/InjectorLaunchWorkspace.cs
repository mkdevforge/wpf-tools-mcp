using System.Diagnostics;

namespace WpfToolsMcp.Automation;

internal sealed class InjectorLaunchWorkspace : IDisposable
{
    private const string WorkspaceDirectoryName = "wpf-tools-mcp";
    private const string InjectorDirectoryName = "injector";
    private int _disposed;

    private InjectorLaunchWorkspace(
        string rootPath,
        string userProfilePath,
        string roamingAppDataPath,
        string localAppDataPath,
        string tempPath)
    {
        RootPath = rootPath;
        UserProfilePath = userProfilePath;
        RoamingAppDataPath = roamingAppDataPath;
        LocalAppDataPath = localAppDataPath;
        TempPath = tempPath;
    }

    public string RootPath { get; }

    public string UserProfilePath { get; }

    public string RoamingAppDataPath { get; }

    public string LocalAppDataPath { get; }

    public string TempPath { get; }

    public static InjectorLaunchWorkspace Create(string? tempRoot = null)
    {
        var baseTempPath = string.IsNullOrWhiteSpace(tempRoot)
            ? Path.GetTempPath()
            : Path.GetFullPath(tempRoot);
        var rootPath = Path.Combine(
            baseTempPath,
            WorkspaceDirectoryName,
            InjectorDirectoryName,
            Guid.NewGuid().ToString("N"));
        var userProfilePath = Path.Combine(rootPath, "profile");
        var roamingAppDataPath = Path.Combine(userProfilePath, "AppData", "Roaming");
        var localAppDataPath = Path.Combine(userProfilePath, "AppData", "Local");
        var tempPath = Path.Combine(rootPath, "temp");

        try
        {
            Directory.CreateDirectory(userProfilePath);
            Directory.CreateDirectory(roamingAppDataPath);
            Directory.CreateDirectory(localAppDataPath);
            Directory.CreateDirectory(tempPath);
            Directory.CreateDirectory(Path.Combine(roamingAppDataPath, "Snoop"));
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(rootPath);
            throw new IOException(
                $"Failed to create an isolated injector workspace beneath '{baseTempPath}'.",
                ex);
        }

        return new InjectorLaunchWorkspace(
            rootPath,
            userProfilePath,
            roamingAppDataPath,
            localAppDataPath,
            tempPath);
    }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException(
                "The isolated injector workspace requires UseShellExecute=false.");
        }

        startInfo.Environment["USERPROFILE"] = UserProfilePath;
        startInfo.Environment["APPDATA"] = RoamingAppDataPath;
        startInfo.Environment["LOCALAPPDATA"] = LocalAppDataPath;
        startInfo.Environment["TEMP"] = TempPath;
        startInfo.Environment["TMP"] = TempPath;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TryDeleteDirectory(RootPath);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The launcher is already contained; temporary workspace cleanup is best effort.
        }
    }
}
