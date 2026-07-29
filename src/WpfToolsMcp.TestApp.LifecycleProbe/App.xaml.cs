using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;

namespace WpfToolsMcp.TestApp.LifecycleProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = LifecycleProbeOptions.Parse(e.Args);
        if (options.ExitImmediately)
        {
            Shutdown();
            return;
        }

        if (options.IsChildProcess)
        {
            WriteChildPid(options.ChildPidPath!);
            Thread.Sleep(Timeout.Infinite);
            return;
        }

        if (options.ChildPidPath is not null)
        {
            StartChildProcess(options.ChildPidPath);
        }

        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }

    private static void StartChildProcess(string childPidPath)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Could not resolve the lifecycle probe executable path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--child-process");
        startInfo.ArgumentList.Add("--child-pid-path");
        startInfo.ArgumentList.Add(childPidPath);

        using var child = Process.Start(startInfo) ??
                          throw new InvalidOperationException("Failed to start lifecycle probe child process.");
    }

    private static void WriteChildPid(string childPidPath)
    {
        var directory = Path.GetDirectoryName(childPidPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            childPidPath,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }
}

internal sealed record LifecycleProbeOptions(
    string MarkerPath,
    bool VetoClose,
    string? ChildPidPath,
    bool IsChildProcess,
    bool ExitImmediately)
{
    public static LifecycleProbeOptions Parse(IReadOnlyList<string> args)
    {
        string? markerPath = null;
        string? childPidPath = null;
        var vetoClose = false;
        var isChildProcess = false;
        var exitImmediately = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--marker-path" when index + 1 < args.Count:
                    markerPath = args[++index];
                    break;
                case "--veto-close":
                    vetoClose = true;
                    break;
                case "--child-pid-path" when index + 1 < args.Count:
                    childPidPath = args[++index];
                    break;
                case "--child-process":
                    isChildProcess = true;
                    break;
                case "--exit-immediately":
                    exitImmediately = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown lifecycle probe argument '{args[index]}'.");
            }
        }

        if (isChildProcess)
        {
            if (string.IsNullOrWhiteSpace(childPidPath))
            {
                throw new ArgumentException("The lifecycle probe child requires --child-pid-path <path>.");
            }

            return new LifecycleProbeOptions(
                MarkerPath: "",
                VetoClose: false,
                ChildPidPath: Path.GetFullPath(childPidPath),
                IsChildProcess: true,
                ExitImmediately: false);
        }

        if (string.IsNullOrWhiteSpace(markerPath))
        {
            throw new ArgumentException("The lifecycle probe requires --marker-path <path>.");
        }

        return new LifecycleProbeOptions(
            Path.GetFullPath(markerPath),
            vetoClose,
            string.IsNullOrWhiteSpace(childPidPath) ? null : Path.GetFullPath(childPidPath),
            IsChildProcess: false,
            ExitImmediately: exitImmediately);
    }
}
