using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WpfToolsMcp.TestApp.ObservationProbe;

public partial class MainWindow : Window
{
    private static readonly TimeSpan OrderedStartDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan OrderedTransitionInterval = TimeSpan.FromMilliseconds(30);

    private static readonly ObservationState[] OrderedStates =
    [
        new("queued", 1, "warming"),
        new("degraded", 2, "retrying"),
        new("ready", 3, "stable")
    ];

    private readonly ObservationProbeOptions _options;
    private readonly ObservationViewModel _viewModel;
    private readonly DispatcherTimer _orderedTimer;
    private readonly DispatcherTimer _delayedRemoveTimer;
    private readonly object _markerSync = new();
    private Thread? _secondaryThread;
    private Dispatcher? _secondaryDispatcher;
    private ObservationViewModel? _secondaryViewModel;
    private int _orderedStateIndex;

    internal MainWindow(ObservationProbeOptions options)
    {
        _options = options;
        _viewModel = new ObservationViewModel(Dispatcher);
        _orderedTimer = new DispatcherTimer(
            OrderedTransitionInterval,
            DispatcherPriority.Normal,
            ApplyNextOrderedState,
            Dispatcher);
        _orderedTimer.Stop();
        _delayedRemoveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Normal,
            RemoveTargetAfterDelay,
            Dispatcher);
        _delayedRemoveTimer.Stop();

        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AppendMarker("started");

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        AppendMarker("closing");
        if (_options.VetoClose)
        {
            e.Cancel = true;
            AppendMarker("close-vetoed");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _orderedTimer.Stop();
        _delayedRemoveTimer.Stop();
        var secondaryDispatcher = Volatile.Read(ref _secondaryDispatcher);
        if (secondaryDispatcher is not null &&
            !secondaryDispatcher.HasShutdownStarted &&
            !secondaryDispatcher.HasShutdownFinished)
        {
            secondaryDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        AppendMarker("closed");
    }

    private void RunOrdered_Click(object sender, RoutedEventArgs e)
    {
        if (_orderedTimer.IsEnabled)
        {
            return;
        }

        _orderedStateIndex = 0;
        _orderedTimer.Interval = OrderedStartDelay;
        RunOrderedButton.IsEnabled = false;
        _orderedTimer.Start();
    }

    private void ApplyNextOrderedState(object? sender, EventArgs e)
    {
        var state = OrderedStates[_orderedStateIndex++];
        _viewModel.Apply(state.Phase, state.Count, state.NestedMode);

        if (_orderedStateIndex == 1)
        {
            _orderedTimer.Interval = OrderedTransitionInterval;
        }

        if (_orderedStateIndex < OrderedStates.Length)
        {
            return;
        }

        _orderedTimer.Stop();
        RunOrderedButton.IsEnabled = true;
        AppendMarker("ordered-complete");
    }

    private void RunCoalesced_Click(object sender, RoutedEventArgs e)
    {
        for (var index = 1; index <= 4; index++)
        {
            _viewModel.Phase = $"c{index}";
        }

        AppendMarker("coalesced-complete");
    }

    private void RunDropBurst_Click(object sender, RoutedEventArgs e)
    {
        for (var index = 1; index <= 6; index++)
        {
            _viewModel.Phase = $"drop-{index}";
            _viewModel.Count = index;
            _viewModel.Nested.Mode = $"drop-mode-{index}";
        }

        AppendMarker("drop-complete");
    }

    private void SetLarge_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetLargeValue();
        AppendMarker("large-complete");
    }

    private void SetSamePrefix_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetSamePrefixValue();
        AppendMarker("same-prefix-complete");
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        RemoveTarget("target-removed");
    }

    private void RemoveTargetDelayed_Click(object sender, RoutedEventArgs e)
    {
        if (_delayedRemoveTimer.IsEnabled || !RootPanel.Children.Contains(ObservationTarget))
        {
            return;
        }

        AppendMarker("target-remove-scheduled");
        _delayedRemoveTimer.Start();
    }

    private void RemoveTargetAfterDelay(object? sender, EventArgs e)
    {
        _delayedRemoveTimer.Stop();
        RemoveTarget("target-removed-delayed");
    }

    private void RemoveTarget(string marker)
    {
        if (RootPanel.Children.Contains(ObservationTarget))
        {
            RootPanel.Children.Remove(ObservationTarget);
        }

        AppendMarker(marker);
    }

    private void OpenSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_secondaryThread is { IsAlive: true })
        {
            return;
        }

        var thread = new Thread(RunSecondaryWindow)
        {
            IsBackground = true,
            Name = "ObservationProbe.SecondaryDispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        _secondaryThread = thread;
        thread.Start();
    }

    private void ChangeSecondary_Click(object sender, RoutedEventArgs e)
    {
        var dispatcher = Volatile.Read(ref _secondaryDispatcher);
        var viewModel = Volatile.Read(ref _secondaryViewModel);
        if (dispatcher is null || viewModel is null ||
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            viewModel.Phase = "secondary-changed";
            AppendMarker("secondary-change-complete");
        }, DispatcherPriority.Normal);
    }

    private void RunSecondaryWindow()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Volatile.Write(ref _secondaryDispatcher, dispatcher);
        var viewModel = new ObservationViewModel(dispatcher);
        Volatile.Write(ref _secondaryViewModel, viewModel);
        var target = new TextBox
        {
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(20)
        };
        AutomationProperties.SetAutomationId(target, "Observation_SecondaryTarget");
        AutomationProperties.SetName(target, "Secondary observation target");
        target.SetBinding(
            TextBox.TextProperty,
            new Binding(nameof(ObservationViewModel.Phase)) { Mode = BindingMode.OneWay });
        var window = new Window
        {
            Title = "WPF Tools MCP ObservationProbe Secondary",
            Width = 420,
            Height = 180,
            Left = 840,
            Top = 160,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = target,
            DataContext = viewModel
        };
        AutomationProperties.SetAutomationId(window, "Observation_SecondaryWindow");
        window.SourceInitialized += (_, _) =>
            AppendMarker($"secondary-hwnd:{new WindowInteropHelper(window).Handle.ToInt64()}");
        window.Loaded += (_, _) => AppendMarker("secondary-started");
        window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        window.Show();
        Dispatcher.Run();
        Volatile.Write(ref _secondaryViewModel, null);
        Volatile.Write(ref _secondaryDispatcher, null);
    }

    private void AppendMarker(string marker)
    {
        var directory = Path.GetDirectoryName(_options.MarkerPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_markerSync)
        {
            File.AppendAllText(_options.MarkerPath, marker + Environment.NewLine, Encoding.UTF8);
        }
    }

    private sealed record ObservationState(string Phase, int Count, string NestedMode);
}

internal sealed class ObservationViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private string _phase = "idle";
    private int _count;
    private string _largeValue = new('I', 4096);
    private string _samePrefixValue = new string('S', 64) + "-one";

    public ObservationViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Nested = new ObservationNestedViewModel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

    public int Count
    {
        get => _count;
        set => SetField(ref _count, value);
    }

    public ObservationNestedViewModel Nested { get; }

    public string LargeValue
    {
        get => _largeValue;
        private set => SetField(ref _largeValue, value);
    }

    public string SamePrefixValue
    {
        get => _samePrefixValue;
        private set => SetField(ref _samePrefixValue, value);
    }

    public string DispatcherGuardedValue => _dispatcher.CheckAccess()
        ? "dispatcher-only"
        : throw new InvalidOperationException("DispatcherGuardedValue must be read on the owning Dispatcher.");

    public bool TargetIsEnabled => true;

    public double TargetWidth => 280;

    public void Apply(string phase, int count, string nestedMode)
    {
        Phase = phase;
        Count = count;
        Nested.Mode = nestedMode;
    }

    public void SetLargeValue() => LargeValue = new string('L', 4096);

    public void SetSamePrefixValue() => SamePrefixValue = new string('S', 64) + "-two";

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class ObservationNestedViewModel : INotifyPropertyChanged
{
    private string _mode = "cold";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Mode
    {
        get => _mode;
        set
        {
            if (StringComparer.Ordinal.Equals(_mode, value))
            {
                return;
            }

            _mode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
        }
    }
}
