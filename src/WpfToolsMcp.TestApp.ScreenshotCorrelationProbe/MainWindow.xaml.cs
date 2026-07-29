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

    private void ShowOwnedOverlay(object sender, RoutedEventArgs e)
    {
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

        if (!_ownedOverlay.IsVisible)
        {
            _ownedOverlay.Show();
        }

        _ownedOverlay.Activate();
    }

    private void HideOwnedOverlay(object sender, RoutedEventArgs e)
    {
        _ownedOverlay?.Close();
    }
}
