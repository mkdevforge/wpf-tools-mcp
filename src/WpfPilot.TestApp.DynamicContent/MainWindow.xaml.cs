using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WpfPilot.TestApp.DynamicContent;

public partial class MainWindow : Window
{
    private int _clicks;
    private Button? _dynamicButton;

    public MainWindow()
    {
        InitializeComponent();
        UpdateStatus();
    }

    private void AddDynamicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dynamicButton is not null)
        {
            return;
        }

        _dynamicButton = new Button
        {
            Content = "Dynamic button",
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
        };

        AutomationProperties.SetAutomationId(_dynamicButton, "Dynamic_NewButton");
        _dynamicButton.Click += DynamicButton_Click;

        DynamicHost.Children.Add(_dynamicButton);
    }

    private void DynamicButton_Click(object sender, RoutedEventArgs e)
    {
        _clicks++;
        UpdateStatus();
    }

    private void UpdateStatus() => DynamicStatus.Text = $"Clicks: {_clicks}";
}

