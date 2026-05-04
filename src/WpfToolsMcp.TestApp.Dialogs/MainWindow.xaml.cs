using System.Windows;

namespace WpfToolsMcp.TestApp.Dialogs;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OpenDialog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog
        {
            Owner = this
        };

        var ok = dialog.ShowDialog() == true;
        var status = ok ? "Dialog: OK" : "Dialog: Cancel";
        DialogsStatus.Text = status;
    }
}
