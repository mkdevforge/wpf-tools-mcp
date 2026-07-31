using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace WpfToolsMcp.TestApp.VirtualizedItems;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        for (var index = 0; index < 300; index++)
        {
            var automationName = index is 120 or 220
                ? "Duplicate target"
                : $"Virtual item {index:D3}";
            Items.Add(new VirtualItem(index, automationName));
        }
    }

    public ObservableCollection<VirtualItem> Items { get; } = [];

    private void VirtualizedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectionStatus.Text = VirtualizedList.SelectedItem is VirtualItem item
            ? $"Selected: {item.AutomationName}"
            : "Selected: (none)";
    }

    public sealed class VirtualItem(int index, string automationName)
    {
        public int Index { get; } = index;

        public string AutomationName { get; } = automationName;

        public string DisplayText => $"{Index:D3}  {AutomationName}";

        public override string ToString() => AutomationName;
    }
}
