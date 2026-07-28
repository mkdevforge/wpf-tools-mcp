using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;

namespace WpfToolsMcp.TestApp.LifecycleProbe;

public partial class MainWindow : Window
{
    private readonly LifecycleProbeOptions _options;
    private int _useCount;

    internal MainWindow(LifecycleProbeOptions options)
    {
        _options = options;
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LifecycleStatus.Text = $"Ready: {Environment.ProcessId}";
        AppendMarker("started");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        AppendMarker("closing");
        if (_options.VetoClose)
        {
            e.Cancel = true;
            AppendMarker("close-vetoed");
        }
    }

    private void OnClosed(object? sender, EventArgs e) => AppendMarker("closed");

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        _useCount++;
        LifecycleStatus.Text = $"Used: {_useCount}";
        AppendMarker("used");
    }

    private void AppendMarker(string marker)
    {
        var directory = Path.GetDirectoryName(_options.MarkerPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(_options.MarkerPath, marker + Environment.NewLine, Encoding.UTF8);
    }
}
