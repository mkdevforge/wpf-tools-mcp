using System.Globalization;
using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfToolsMcp.Agent;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[Category("Wpf")]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class ValidationDiagnosticsWpfTests
{
    [Test]
    public void Current_state_catches_application_error_content_and_message_failures()
    {
        const string targetName = "ValidationThrowingContentTarget";
        var ownerId = $"validation-throwing-content-{Guid.NewGuid():N}";
        var target = CreateBoundTextBox(targetName, nameof(ValidationModel.First));
        var window = CreateWindow(target);
        var throwingContent = new ThrowingStringifier();
        var throwingMessage = new ThrowingMessageException();
        var wrapperException = new AggregateException(throwingMessage);

        try
        {
            ShowAndLayout(window);
            var expression = RequireTextBinding(target);
            Validation.MarkInvalid(
                expression,
                new ValidationError(new FixtureValidationRule(), expression, throwingContent, wrapperException));
            PumpValidation(window.Dispatcher);

            var response = Inspect(window, ownerId, maxValueLength: 80);
            var error = response.Errors.Single();
            var bindingInfo = WpfVisualTreeInspector.GetBindingInfo(
                ownerId,
                new GetBindingInfoRequest(
                    WindowHandle: GetWindowHandle(window),
                    Locator: new ElementLocator(XPath: error.Element.XPath)),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(response.BackendUsed, Is.EqualTo(InspectionBackend.Wpf));
                Assert.That(response.ReturnedErrors, Is.EqualTo(1));
                Assert.That(response.DiscoveredErrors, Is.EqualTo(1));
                Assert.That(response.ScanComplete, Is.True);
                Assert.That(response.Truncated, Is.False);
                Assert.That(response.DepthUsed, Is.EqualTo(10));
                Assert.That(error.Element.ElementId, Is.Null);
                Assert.That(error.Element.ElementIdWpf, Is.Null);
                Assert.That(error.ErrorIndex, Is.Zero);
                Assert.That(error.Source.Kind, Is.EqualTo(ValidationSourceKind.ValidationRule));
                Assert.That(error.Source.Evidence.Kind, Is.EqualTo(ProvenanceEvidenceKind.Exact));
                Assert.That(error.Binding.Kind, Is.EqualTo(ValidationBindingKind.Binding));
                Assert.That(error.Binding.TargetProperty, Is.EqualTo("Text"));
                Assert.That(error.Binding.Path, Is.EqualTo(nameof(ValidationModel.First)));
                Assert.That(error.Binding.Truncated, Is.False);
                Assert.That(error.Content.Type, Does.EndWith(nameof(ThrowingStringifier)));
                Assert.That(error.Content.Value, Is.Null);
                Assert.That(
                    error.Content.UnavailableReason,
                    Is.EqualTo("value_to_string_failed:System.InvalidOperationException"));
                Assert.That(error.Exception!.Type, Is.EqualTo(typeof(AggregateException).FullName));
                Assert.That(error.Exception.Message, Is.Null);
                Assert.That(
                    error.Exception.MessageUnavailableReason,
                    Is.EqualTo("message_getter_failed:System.InvalidOperationException"));
                Assert.That(error.Visual.HasError, Is.True);
                Assert.That(
                    error.Visual.AdornerState,
                    Is.AnyOf(ValidationAdornerState.Active, ValidationAdornerState.NotObserved));
                Assert.That(
                    bindingInfo.Bindings.Single(binding => binding.TargetProperty == "Text").ErrorMessage,
                    Does.EndWith(nameof(ThrowingStringifier)));
                Assert.That(throwingContent.ToStringCalls, Is.EqualTo(2));
                Assert.That(throwingMessage.MessageCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Current_state_reports_application_error_content_and_message_best_effort()
    {
        const string targetName = "ValidationCustomContentTarget";
        var ownerId = $"validation-custom-content-{Guid.NewGuid():N}";
        var target = CreateBoundTextBox(targetName, nameof(ValidationModel.First));
        var window = CreateWindow(target);
        var content = new DisplayValue("Application validation content");
        var exception = new CustomMessageException("Application validation exception");

        try
        {
            ShowAndLayout(window);
            var expression = RequireTextBinding(target);
            Validation.MarkInvalid(
                expression,
                new ValidationError(new FixtureValidationRule(), expression, content, exception));
            PumpValidation(window.Dispatcher);

            var response = Inspect(window, ownerId, maxValueLength: 80);
            var error = response.Errors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(error.Content.Type, Does.EndWith(nameof(DisplayValue)));
                Assert.That(error.Content.Value, Is.EqualTo("Application validation content"));
                Assert.That(error.Content.Truncated, Is.False);
                Assert.That(error.Content.UnavailableReason, Is.Null);
                Assert.That(error.Exception!.Type, Is.EqualTo(typeof(CustomMessageException).FullName));
                Assert.That(error.Exception.Message, Is.EqualTo("Application validation exception"));
                Assert.That(error.Exception.MessageTruncated, Is.False);
                Assert.That(error.Exception.MessageUnavailableReason, Is.Null);
                Assert.That(content.ToStringCalls, Is.EqualTo(1));
                Assert.That(exception.MessageCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Delayed_notify_data_errors_appear_and_clear_from_current_state()
    {
        var ownerId = $"validation-async-notify-{Guid.NewGuid():N}";
        var model = new AsyncNotifyValidationModel();
        var target = new TextBox { Name = "ValidationAsyncNotifyTarget" };
        target.SetBinding(
            TextBox.TextProperty,
            new Binding(nameof(AsyncNotifyValidationModel.Value))
            {
                Source = model,
                ValidatesOnNotifyDataErrors = true
            });
        var window = CreateWindow(target);

        try
        {
            ShowAndLayout(window);

            model.SetHasErrorAsync(window.Dispatcher, hasError: true);
            PumpDelayedValidation(window.Dispatcher);
            var withError = Inspect(window, ownerId);

            model.SetHasErrorAsync(window.Dispatcher, hasError: false);
            PumpDelayedValidation(window.Dispatcher);
            var afterClear = Inspect(window, ownerId);

            Assert.Multiple(() =>
            {
                Assert.That(withError.ReturnedErrors, Is.EqualTo(1));
                Assert.That(
                    withError.Errors.Single().Source.Kind,
                    Is.EqualTo(ValidationSourceKind.NotifyDataError));
                Assert.That(
                    withError.Errors.Single().Content.Value,
                    Is.EqualTo(AsyncNotifyValidationModel.ErrorMessage));
                Assert.That(afterClear.ReturnedErrors, Is.Zero);
                Assert.That(model.ErrorsChangedCount, Is.EqualTo(2));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Source_scope_visibility_and_error_budget_follow_current_state()
    {
        var ownerId = $"validation-current-state-{Guid.NewGuid():N}";
        var first = CreateBoundTextBox("ValidationDataErrorTarget", nameof(ValidationModel.First));
        var second = CreateBoundTextBox("ValidationNotifyErrorTarget", nameof(ValidationModel.Second));
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            var firstExpression = RequireTextBinding(first);
            var secondExpression = RequireTextBinding(second);
            Validation.MarkInvalid(
                firstExpression,
                new ValidationError(
                    new DataErrorValidationRule(),
                    firstExpression,
                    "data error",
                    null));
            Validation.MarkInvalid(
                secondExpression,
                new ValidationError(
                    new NotifyDataErrorValidationRule(),
                    secondExpression,
                    "notify error",
                    null));
            PumpValidation(window.Dispatcher);

            var bounded = Inspect(window, ownerId, maxErrors: 1);
            var full = Inspect(window, ownerId);
            var secondXPath = full.Errors.Single(error =>
                error.Element.Name == "ValidationNotifyErrorTarget").Element.XPath;
            var subtree = WpfVisualTreeInspector.GetValidationErrors(
                ownerId,
                CreateRequest(window, rootXPath: secondXPath, depth: 1),
                CancellationToken.None);

            second.Visibility = Visibility.Collapsed;
            window.UpdateLayout();
            var visibleOnly = WpfVisualTreeInspector.GetValidationErrors(
                ownerId,
                CreateRequest(window, visibleOnly: true),
                CancellationToken.None);

            Validation.ClearInvalid(firstExpression);
            var afterClear = Inspect(window, ownerId);
            Validation.MarkInvalid(
                firstExpression,
                new ValidationError(
                    new DataErrorValidationRule(),
                    firstExpression,
                    "data error reapplied",
                    null));
            var afterReapply = Inspect(window, ownerId);

            Assert.Multiple(() =>
            {
                Assert.That(bounded.ReturnedErrors, Is.EqualTo(1));
                Assert.That(bounded.DiscoveredErrors, Is.EqualTo(2));
                Assert.That(bounded.ScanComplete, Is.True);
                Assert.That(bounded.TruncatedReasons, Does.Contain("maxErrors"));
                Assert.That(
                    full.Errors.Select(error => error.Source.Kind),
                    Is.EquivalentTo(new[]
                    {
                        ValidationSourceKind.DataError,
                        ValidationSourceKind.NotifyDataError
                    }));
                Assert.That(subtree.ReturnedErrors, Is.EqualTo(1));
                Assert.That(subtree.RootXPath, Is.EqualTo(secondXPath));
                Assert.That(visibleOnly.ReturnedErrors, Is.EqualTo(1));
                Assert.That(
                    visibleOnly.Errors.Single().Element.Name,
                    Is.EqualTo("ValidationDataErrorTarget"));
                Assert.That(afterClear.DiscoveredErrors, Is.EqualTo(1));
                Assert.That(afterReapply.DiscoveredErrors, Is.EqualTo(2));
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    [Test]
    public void Node_depth_content_and_binding_metadata_budgets_are_explicit()
    {
        var ownerId = $"validation-budgets-{Guid.NewGuid():N}";
        var longPath = new string('P', 600);
        var target = CreateBoundTextBox("ValidationBudgetTarget", longPath);
        var panel = new StackPanel();
        panel.Children.Add(target);
        var window = CreateWindow(panel);

        try
        {
            ShowAndLayout(window);
            var expression = RequireTextBinding(target);
            Validation.MarkInvalid(
                expression,
                new ValidationError(
                    new ExceptionValidationRule(),
                    expression,
                    new string('v', 600),
                    new InvalidOperationException(new string('e', 600))));
            PumpValidation(window.Dispatcher);

            var bounded = WpfVisualTreeInspector.GetValidationErrors(
                ownerId,
                CreateRequest(window, depth: 1000, maxValueLength: 1),
                CancellationToken.None);
            var nodeLimited = WpfVisualTreeInspector.GetValidationErrors(
                ownerId,
                CreateRequest(window, maxNodes: 1),
                CancellationToken.None);
            var error = bounded.Errors.Single();

            Assert.Multiple(() =>
            {
                Assert.That(bounded.DepthUsed, Is.EqualTo(100));
                Assert.That(bounded.ScanComplete, Is.False);
                Assert.That(bounded.TruncatedReasons, Does.Contain("maxDepth"));
                Assert.That(error.Binding.Path, Has.Length.EqualTo(1));
                Assert.That(error.Binding.Truncated, Is.True);
                Assert.That(error.Content.Value, Has.Length.EqualTo(1));
                Assert.That(error.Content.Truncated, Is.True);
                Assert.That(error.Exception!.Message, Has.Length.EqualTo(1));
                Assert.That(error.Exception.MessageTruncated, Is.True);
                Assert.That(nodeLimited.ScannedNodes, Is.EqualTo(1));
                Assert.That(nodeLimited.DiscoveredErrors, Is.Zero);
                Assert.That(nodeLimited.ScanComplete, Is.False);
                Assert.That(nodeLimited.TruncatedReasons, Does.Contain("maxNodes"));
                Assert.That(nodeLimited.ReturnedWarnings, Is.Zero);
                Assert.That(nodeLimited.DiscoveredWarnings, Is.Zero);
                Assert.That(nodeLimited.WarningsTruncated, Is.False);
            });
        }
        finally
        {
            CloseAndRelease(window, ownerId);
        }
    }

    private static TextBox CreateBoundTextBox(string name, string path)
    {
        var target = new TextBox { Name = name, Width = 160, Height = 28 };
        AutomationProperties.SetAutomationId(target, name);
        target.SetBinding(
            TextBox.TextProperty,
            new Binding(path)
            {
                Source = new ValidationModel(),
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.Explicit
            });
        return target;
    }

    private static Window CreateWindow(UIElement content) =>
        new()
        {
            Title = "Validation diagnostics WPF unit test",
            Width = 300,
            Height = 180,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10_000,
            Top = -10_000,
            Content = content
        };

    private static void ShowAndLayout(Window window)
    {
        window.Show();
        window.UpdateLayout();
        PumpValidation(window.Dispatcher);
    }

    private static void PumpValidation(Dispatcher dispatcher)
    {
        dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void PumpDelayedValidation(Dispatcher dispatcher)
    {
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        PumpValidation(dispatcher);
    }

    private static BindingExpression RequireTextBinding(TextBox textBox) =>
        textBox.GetBindingExpression(TextBox.TextProperty)
        ?? throw new AssertionException("Expected an active Text binding.");

    private static GetValidationErrorsResponse Inspect(
        Window window,
        string ownerId,
        int maxErrors = 100,
        int maxValueLength = 500) =>
        WpfVisualTreeInspector.GetValidationErrors(
            ownerId,
            CreateRequest(window, maxErrors: maxErrors, maxValueLength: maxValueLength),
            CancellationToken.None);

    private static GetValidationErrorsRequest CreateRequest(
        Window window,
        string? rootXPath = null,
        int depth = 10,
        bool visibleOnly = false,
        int maxErrors = 100,
        int maxNodes = 2000,
        int maxValueLength = 500) =>
        new(
            WindowHandle: GetWindowHandle(window),
            RootXPath: rootXPath,
            Depth: depth,
            VisibleOnly: visibleOnly,
            MaxErrors: maxErrors,
            MaxNodes: maxNodes,
            MaxValueLength: maxValueLength);

    private static long GetWindowHandle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle.ToInt64();
        Assert.That(handle, Is.Not.Zero);
        return handle;
    }

    private static void CloseAndRelease(Window window, string ownerId)
    {
        WpfVisualTreeInspector.ReleaseOwnerResources(ownerId);
        if (window.IsVisible)
        {
            window.Close();
        }
    }

    private sealed class ValidationModel
    {
        public string First { get; set; } = "first";

        public string Second { get; set; } = "second";
    }

    private sealed class FixtureValidationRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
            ValidationResult.ValidResult;
    }

    private sealed class ThrowingStringifier
    {
        public int ToStringCalls { get; private set; }

        public override string ToString()
        {
            ToStringCalls++;
            throw new InvalidOperationException("Application validation content formatting failed.");
        }
    }

    private sealed class DisplayValue(string text)
    {
        public int ToStringCalls { get; private set; }

        public override string ToString()
        {
            ToStringCalls++;
            return text;
        }
    }

    private sealed class CustomMessageException(string message) : Exception
    {
        public int MessageCalls { get; private set; }

        public override string Message
        {
            get
            {
                MessageCalls++;
                return message;
            }
        }
    }

    private sealed class ThrowingMessageException : Exception
    {
        public int MessageCalls { get; private set; }

        public override string Message
        {
            get
            {
                MessageCalls++;
                throw new InvalidOperationException("Application validation exception message failed.");
            }
        }
    }

    private sealed class AsyncNotifyValidationModel : System.ComponentModel.INotifyDataErrorInfo
    {
        public const string ErrorMessage = "Delayed notification rejected the value.";

        private bool _hasError;

        public string Value { get; set; } = "value";

        public bool HasErrors => _hasError;

        public int ErrorsChangedCount { get; private set; }

        public event EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;

        public IEnumerable GetErrors(string? propertyName) =>
            _hasError && string.Equals(propertyName, nameof(Value), StringComparison.Ordinal)
                ? new[] { ErrorMessage }
                : Array.Empty<string>();

        public void SetHasErrorAsync(Dispatcher dispatcher, bool hasError)
        {
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _hasError = hasError;
                    ErrorsChangedCount++;
                    ErrorsChanged?.Invoke(
                        this,
                        new System.ComponentModel.DataErrorsChangedEventArgs(nameof(Value)));
                }));
        }
    }
}
