using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WpfToolsMcp.TestApp.BindingErrors;

public sealed class NonEmptyValidationRule : ValidationRule
{
    public override ValidationResult Validate(object? value, CultureInfo cultureInfo) =>
        string.IsNullOrWhiteSpace(value as string)
            ? new ValidationResult(false, "A value is required.")
            : ValidationResult.ValidResult;
}

internal sealed class MainViewModel : IDataErrorInfo, INotifyDataErrorInfo
{
    public const string InvalidValue = "invalid";
    public const string ThrowingValue = "throw";

    private string _throwingText = "valid";
    private string _dataErrorText = "valid";
    private string _notifyErrorText = "valid";

    public string OkText { get; set; } = "Hello";

    public string RuleText { get; set; } = "valid";

    public int IntegerValue { get; set; } = 42;

    public string ThrowingText
    {
        get => _throwingText;
        set
        {
            if (string.Equals(value, ThrowingValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The fixture setter rejected the value.");
            }

            _throwingText = value;
        }
    }

    public string DataErrorText
    {
        get => _dataErrorText;
        set => _dataErrorText = value;
    }

    public string NotifyErrorText
    {
        get => _notifyErrorText;
        set
        {
            _notifyErrorText = value;
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                    ErrorsChanged?.Invoke(
                        this,
                        new DataErrorsChangedEventArgs(nameof(NotifyErrorText)))));
        }
    }

    public string Error => string.Empty;

    public string this[string columnName] =>
        string.Equals(columnName, nameof(DataErrorText), StringComparison.Ordinal) &&
        string.Equals(DataErrorText, InvalidValue, StringComparison.Ordinal)
            ? "IDataErrorInfo rejected the value."
            : string.Empty;

    public bool HasErrors => string.Equals(NotifyErrorText, InvalidValue, StringComparison.Ordinal);

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        string.Equals(propertyName, nameof(NotifyErrorText), StringComparison.Ordinal) && HasErrors
            ? new[] { "INotifyDataErrorInfo rejected the value." }
            : Array.Empty<string>();
}
