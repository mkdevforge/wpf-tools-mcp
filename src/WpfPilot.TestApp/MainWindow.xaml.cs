using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;

namespace WpfPilot.TestApp;

public partial class MainWindow : Window
{
    private int _basicClickCount;

    public MainWindow()
    {
        InitializeComponent();
        PopulateListBox();
        SourceInitialized += (_, _) => DisableWindowRounding();
    }

    private void BasicButton_Click(object sender, RoutedEventArgs e)
    {
        _basicClickCount++;
        BasicClickStatus.Text = $"Clicks: {_basicClickCount}";
    }

    private void PopulateListBox()
    {
        BasicListBox.ItemsSource = Enumerable.Range(0, 100).Select(i => $"Item {i}").ToArray();
        BasicListBox.SelectedIndex = -1;
        BasicListBoxStatus.Text = "Selected: (none)";
    }

    private void BasicListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = BasicListBox.SelectedItem as string;
        BasicListBoxStatus.Text = selected is null ? "Selected: (none)" : $"Selected: {selected}";
    }

    private void BasicHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
    }

    private void DisableWindowRounding()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var preference = DWMWCP_DONOTROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
