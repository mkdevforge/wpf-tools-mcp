using System.Windows;
using System.Windows.Controls;

namespace WpfPilot.TestApp.Minimal;

public partial class MainWindow : Window
{
    private int _primaryClicks;

    public MainWindow()
    {
        InitializeComponent();
        PopulateList();
        UpdateClickStatus();
        UpdateSelectionStatus();
    }

    private void PopulateList()
    {
        ItemList.ItemsSource = Enumerable.Range(0, 31).Select(i => $"Item {i}").ToArray();
        ItemList.SelectedIndex = -1;
    }

    private void PrimaryOkButton_Click(object sender, RoutedEventArgs e)
    {
        _primaryClicks++;
        UpdateClickStatus();
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionStatus();

    private void UpdateClickStatus() => ClickStatus.Text = $"Clicks: {_primaryClicks}";

    private void UpdateSelectionStatus()
    {
        var selected = ItemList.SelectedItem as string;
        SelectionStatus.Text = selected is null ? "Selected: (none)" : $"Selected: {selected}";
    }
}

