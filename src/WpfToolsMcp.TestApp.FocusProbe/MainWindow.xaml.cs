using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfToolsMcp.TestApp.FocusProbe;

public partial class MainWindow : Window
{
    private const int MaximumReportedKeyboardEvents = 12;
    private int _activationCount;
    private int _buttonClickCount;
    private int _deactivationCount;
    private int _physicalClickCount;
    private readonly List<string> _keyboardEvents = [];
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
        UpdateKeyboardEventStatus();
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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        _keyboardEvents.Add(FormatKeyStroke(key, Keyboard.Modifiers));
        if (_keyboardEvents.Count > MaximumReportedKeyboardEvents)
        {
            _keyboardEvents.RemoveAt(0);
        }

        UpdateKeyboardEventStatus();

        if (key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _selectAllKeyboardText = true;
            e.Handled = true;
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            if (_selectAllKeyboardText || _keyboardText.Length > 0)
            {
                _keyboardText = string.Empty;
                _selectAllKeyboardText = false;
                UpdateKeyboardFallbackStatus();
            }

            e.Handled = true;
            return;
        }

        if (key is Key.Enter or Key.Escape or Key.Tab or
            Key.Left or Key.Up or Key.Right or Key.Down)
        {
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

    private void NavigationRedirect_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _ = NavigationDestination.Focus();

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

    private void UpdateKeyboardEventStatus() =>
        KeyboardEventStatus.Text = _keyboardEvents.Count == 0
            ? "Keys: (none)"
            : $"Keys: {string.Join(",", _keyboardEvents)}";

    private static string FormatKeyStroke(Key key, ModifierKeys modifiers)
    {
        var names = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            names.Add("Control");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            names.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            names.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            names.Add("Windows");
        }

        names.Add(key switch
        {
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Escape => "Escape",
            Key.Left => "ArrowLeft",
            Key.Up => "ArrowUp",
            Key.Right => "ArrowRight",
            Key.Down => "ArrowDown",
            Key.PageDown => "PageDown",
            Key.D0 => "Digit0",
            Key.D1 => "Digit1",
            Key.D2 => "Digit2",
            Key.D3 => "Digit3",
            Key.D4 => "Digit4",
            Key.D5 => "Digit5",
            Key.D6 => "Digit6",
            Key.D7 => "Digit7",
            Key.D8 => "Digit8",
            Key.D9 => "Digit9",
            _ => key.ToString()
        });
        return string.Join("+", names);
    }

    private void UpdatePhysicalFallbackStatus() => PhysicalFallbackStatus.Text = $"Physical clicks: {_physicalClickCount}";

    private void UpdateSelectionStatus()
    {
        var selected = ProbeListBox.SelectedItem as string;
        SelectionStatus.Text = selected is null ? "Selected: (none)" : $"Selected: {selected}";
    }
}
