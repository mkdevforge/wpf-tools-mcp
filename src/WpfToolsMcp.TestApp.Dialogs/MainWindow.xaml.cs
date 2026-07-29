using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WpfToolsMcp.TestApp.Dialogs;

public partial class MainWindow : Window
{
    private const string NativeDialogFileArgument = "--native-dialog-file";
    private const string NativeDialogTitle = "WPF Tools MCP Native Open Dialog";

    private readonly string? _nativeDialogFilePath;

    public MainWindow()
    {
        InitializeComponent();
        _nativeDialogFilePath = ReadNativeDialogFilePath();
    }

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

    private void OpenNativeFileDialog_Click(object sender, RoutedEventArgs e)
    {
        // Return from the semantic Invoke call before entering the modal loop.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowNativeFileDialog));
    }

    private void OpenNativeFileDialogDelayed_Click(object sender, RoutedEventArgs e)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ShowNativeFileDialog();
        };
        timer.Start();
    }

    private void ShowNativeFileDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = NativeDialogTitle,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        if (_nativeDialogFilePath is not null)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_nativeDialogFilePath);
            dialog.FileName = Path.GetFileName(_nativeDialogFilePath);
        }

        var opened = dialog.ShowDialog(this) == true;
        NativeDialogStatus.Text = opened
            ? $"Native dialog: Opened {Path.GetFileName(dialog.FileName)}"
            : "Native dialog: Cancel";
    }

    private static string? ReadNativeDialogFilePath()
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 1; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], NativeDialogFileArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = args[index + 1];
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }

        return null;
    }
}
