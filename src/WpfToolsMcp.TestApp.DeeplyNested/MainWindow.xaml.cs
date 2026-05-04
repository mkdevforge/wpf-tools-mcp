using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.DeeplyNested;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootHost.Content = BuildNested(depth: 18);
    }

    private static UIElement BuildNested(int depth)
    {
        var current = BuildTarget();
        for (var i = depth; i >= 1; i--)
        {
            var wrapper = new LevelBorder
            {
                Child = current,
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
            };

            AutomationProperties.SetAutomationId(wrapper, $"Nested_Level_{i:00}");
            current = wrapper;
        }

        return current;
    }

    private static UIElement BuildTarget()
    {
        var button = new Button
        {
            Content = "Deep target",
            Width = 160,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        AutomationProperties.SetAutomationId(button, "Nested_TargetButton");
        return button;
    }
}
