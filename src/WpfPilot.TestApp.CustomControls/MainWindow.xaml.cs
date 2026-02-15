using System.Windows;

namespace WpfPilot.TestApp.CustomControls;

public partial class MainWindow : Window
{
    private int _clicks;

    public MainWindow()
    {
        InitializeComponent();
        UpdateStatus();
    }

    private void ClickableCard_Clicked(object sender, RoutedEventArgs e)
    {
        _clicks++;
        UpdateStatus();
    }

    private void UpdateStatus() => StatusText.Text = $"Clicks: {_clicks}";
}

