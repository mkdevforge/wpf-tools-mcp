namespace WpfToolsMcp.TestApp.BindingErrors;

public partial class MainWindow
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void ApplyValidationErrors(object sender, System.Windows.RoutedEventArgs e)
    {
        RuleTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, string.Empty);
        ConversionTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "not-an-integer");
        ExceptionTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, MainViewModel.ThrowingValue);
        DataErrorTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, MainViewModel.InvalidValue);
        NotifyErrorTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, MainViewModel.InvalidValue);
        UpdateValidationSources();
    }

    private void ClearValidationErrors(object sender, System.Windows.RoutedEventArgs e)
    {
        RuleTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "valid");
        ConversionTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "42");
        ExceptionTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "valid");
        DataErrorTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "valid");
        NotifyErrorTextBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "valid");
        UpdateValidationSources();
    }

    private void UpdateValidationSources()
    {
        UpdateValidationSource(RuleTextBox);
        UpdateValidationSource(ConversionTextBox);
        UpdateValidationSource(ExceptionTextBox);
        UpdateValidationSource(DataErrorTextBox);
        UpdateValidationSource(NotifyErrorTextBox);
    }

    private static void UpdateValidationSource(System.Windows.Controls.TextBox textBox) =>
        (textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)
         ?? throw new InvalidOperationException($"Validation fixture binding was removed from {textBox.Name}."))
        .UpdateSource();
}
