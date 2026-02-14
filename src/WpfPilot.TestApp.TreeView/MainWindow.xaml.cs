using System.Windows;
using System.Windows.Controls;

namespace WpfPilot.TestApp.TreeView;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateSelectedStatus(null);
    }

    private void MainTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        UpdateSelectedStatus(e.NewValue);

    private void UpdateSelectedStatus(object? selected)
    {
        var header = selected switch
        {
            TreeViewItem item => item.Header?.ToString(),
            _ => selected?.ToString()
        };

        TreeSelectedStatus.Text = string.IsNullOrWhiteSpace(header) ? "Selected: (none)" : $"Selected: {header}";
    }
}

