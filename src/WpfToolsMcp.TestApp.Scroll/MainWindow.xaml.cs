using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.Scroll;

public partial class MainWindow : Window
{
    private int _clicks;

    public MainWindow()
    {
        InitializeComponent();
        PopulateContent();
        UpdateStatus();
    }

    private void PopulateContent()
    {
        ScrollContentPanel.Children.Clear();

        for (var i = 0; i < 200; i++)
        {
            var text = new TextBlock
            {
                Text = $"Row {i}",
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = Brushes.Gray
            };

            ScrollContentPanel.Children.Add(text);
        }

        var targetButton = new Button
        {
            Content = "Click me (target)",
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        };

        AutomationProperties.SetAutomationId(targetButton, "Scroll_TargetButton");
        targetButton.Click += TargetButton_Click;

        ScrollContentPanel.Children.Add(targetButton);

        for (var i = 200; i < 240; i++)
        {
            var text = new TextBlock
            {
                Text = $"Row {i}",
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = Brushes.Gray
            };

            ScrollContentPanel.Children.Add(text);
        }

        var badBoundsButton = new BadBoundsButton
        {
            Content = "Click me (bad bounds target)",
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        };

        AutomationProperties.SetAutomationId(badBoundsButton, "Scroll_BadBoundsTargetButton");
        badBoundsButton.Click += TargetButton_Click;

        ScrollContentPanel.Children.Add(badBoundsButton);

        for (var i = 240; i < 260; i++)
        {
            var text = new TextBlock
            {
                Text = $"Row {i}",
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = Brushes.Gray
            };

            ScrollContentPanel.Children.Add(text);
        }
    }

    private void TargetButton_Click(object sender, RoutedEventArgs e)
    {
        _clicks++;
        UpdateStatus();
    }

    private void UpdateStatus() => StatusText.Text = $"Clicks: {_clicks}";
}
