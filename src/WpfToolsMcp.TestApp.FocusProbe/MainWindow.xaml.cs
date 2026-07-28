using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfToolsMcp.TestApp.FocusProbe;

public partial class MainWindow : Window
{
    private int _activationCount;
    private int _buttonClickCount;
    private int _deactivationCount;
    private int _physicalClickCount;
    private string _keyboardText = string.Empty;
    private bool _selectAllKeyboardText;

    public MainWindow()
    {
        InitializeComponent();

        ProbeListBox.ItemsSource = new[] { "Alpha", "Beta", "Gamma" };
        ProbeListBox.SelectedIndex = -1;

        Activated += OnActivated;
        Deactivated += OnDeactivated;

        UpdateActivationStatus();
        UpdateButtonStatus();
        UpdateKeyboardFallbackStatus();
        UpdatePhysicalFallbackStatus();
        UpdateSelectionStatus();
        ReadyStatus.Text = $"Ready: {Environment.ProcessId}";
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _activationCount++;
        UpdateActivationStatus();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _deactivationCount++;
        UpdateActivationStatus();
    }

    private void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        _buttonClickCount++;
        UpdateButtonStatus();
    }

    private void PhysicalFallbackTarget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _physicalClickCount++;
        UpdatePhysicalFallbackStatus();
    }

    private void KeyboardFallbackTarget_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _ = KeyboardFallbackTarget.Focus();

    private void KeyboardFallbackTarget_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _selectAllKeyboardText = true;
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectAllKeyboardText || _keyboardText.Length > 0)
            {
                _keyboardText = string.Empty;
                _selectAllKeyboardText = false;
                UpdateKeyboardFallbackStatus();
            }

            e.Handled = true;
        }
    }

    private void KeyboardFallbackTarget_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (_selectAllKeyboardText)
        {
            _keyboardText = string.Empty;
            _selectAllKeyboardText = false;
        }

        _keyboardText += e.Text;
        UpdateKeyboardFallbackStatus();
        e.Handled = true;
    }

    private void ProbeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionStatus();

    private void UpdateActivationStatus()
    {
        ActivationCountStatus.Text = $"Activated: {_activationCount}";
        DeactivationCountStatus.Text = $"Deactivated: {_deactivationCount}";
        Title = $"WPF Tools MCP FocusProbe TestApp ({Environment.ProcessId}) [A:{_activationCount} D:{_deactivationCount}]";
    }

    private void UpdateButtonStatus() => ButtonStatus.Text = $"Semantic invokes: {_buttonClickCount}";

    private void UpdateKeyboardFallbackStatus() =>
        KeyboardFallbackStatus.Text = _keyboardText.Length == 0
            ? "Keyboard text: (empty)"
            : $"Keyboard text: {_keyboardText}";

    private void UpdatePhysicalFallbackStatus() => PhysicalFallbackStatus.Text = $"Physical clicks: {_physicalClickCount}";

    private void UpdateSelectionStatus()
    {
        var selected = ProbeListBox.SelectedItem as string;
        SelectionStatus.Text = selected is null ? "Selected: (none)" : $"Selected: {selected}";
    }
}
