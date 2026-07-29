using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.ScreenshotCorrelationProbe;

public partial class MainWindow : Window
{
    private Window? _ownedOverlay;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ToggleOwnedOverlay(object sender, RoutedEventArgs e)
    {
        if (_ownedOverlay is { IsVisible: true })
        {
            _ownedOverlay.Close();
            return;
        }

        if (_ownedOverlay is null)
        {
            _ownedOverlay = new Window
            {
                Owner = this,
                Title = "WPF Tools MCP Correlation Owned Overlay",
                Width = 280,
                Height = 180,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White,
                Content = new TextBlock
                {
                    Text = "Owned overlay obscuration probe",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _ownedOverlay.Closed += (_, _) => _ownedOverlay = null;
        }

        _ownedOverlay.Show();
        _ownedOverlay.Activate();
    }
}
