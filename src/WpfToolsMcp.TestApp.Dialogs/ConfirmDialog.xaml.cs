using System.Windows;

namespace WpfToolsMcp.TestApp.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

