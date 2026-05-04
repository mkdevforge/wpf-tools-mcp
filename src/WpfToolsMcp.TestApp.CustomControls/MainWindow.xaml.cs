using System.Windows;

namespace WpfToolsMcp.TestApp.CustomControls;

public partial class MainWindow : Window
{
    private int _clicks;
    private int _templatedButtonInvokes;
    private int _templatedToggleInvokes;
    private int _templatedRadioInvokes;

    public MainWindow()
    {
        InitializeComponent();
        UpdateStatus();
        UpdateTemplatedStatus();
    }

    private void ClickableCard_Clicked(object sender, RoutedEventArgs e)
    {
        _clicks++;
        UpdateStatus();
    }

    private void UpdateStatus() => StatusText.Text = $"Clicks: {_clicks}";

    private void TemplatedInvokeButton_Click(object sender, RoutedEventArgs e)
    {
        _templatedButtonInvokes++;
        UpdateTemplatedStatus();
    }

    private void TemplatedInvokeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _templatedToggleInvokes++;
        UpdateTemplatedStatus();
    }

    private void TemplatedInvokeRadioButton_Click(object sender, RoutedEventArgs e)
    {
        _templatedRadioInvokes++;
        UpdateTemplatedStatus();
    }

    private void UpdateTemplatedStatus() =>
        TemplatedStatusText.Text =
            $"Templated invokes: button={_templatedButtonInvokes}, toggle={_templatedToggleInvokes}, radio={_templatedRadioInvokes}";
}
