using System.IO;
using System.Windows;

namespace WpfToolsMcp.TestApp.ObservationProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = ObservationProbeOptions.Parse(e.Args);
        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }
}

internal sealed record ObservationProbeOptions(string MarkerPath, bool VetoClose)
{
    public static ObservationProbeOptions Parse(IReadOnlyList<string> args)
    {
        string? markerPath = null;
        var vetoClose = false;

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
                default:
                    throw new ArgumentException($"Unknown observation probe argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(markerPath))
        {
            throw new ArgumentException("The observation probe requires --marker-path <path>.");
        }

        return new ObservationProbeOptions(Path.GetFullPath(markerPath), vetoClose);
    }
}
