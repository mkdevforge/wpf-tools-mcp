using System.Windows;
using System.Windows.Controls;

namespace WpfPilot.TestApp.Tabs;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateSelectedStatus();
    }

    private void TabsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedStatus();

    private void UpdateSelectedStatus()
    {
        var selected = TabsTabControl.SelectedItem as TabItem;
        var header = selected?.Header?.ToString();
        TabsSelectedStatus.Text = string.IsNullOrWhiteSpace(header) ? "Selected: (none)" : $"Selected: {header}";
    }
}

