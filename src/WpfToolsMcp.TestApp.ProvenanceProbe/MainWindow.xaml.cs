using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace WpfToolsMcp.TestApp.ProvenanceProbe;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        MaterializeFixtureResources();
        DataContext = new ProvenanceViewModel();
        var unsafeResourceKey = new UnsafeResourceKey();
        Resources.Add(unsafeResourceKey, System.Windows.Media.Brushes.MediumPurple);
        UnsafeDynamicResourceProbe.SetResourceReference(Border.BackgroundProperty, unsafeResourceKey);
        ThrowingValueProbe.Payload = new ThrowingDisplayValue();
        for (var i = 0; i < 512; i++)
        {
            LargeResourceProbe.Resources.Add($"Provenance.Large.{i:D3}", i);
        }

        Loaded += OnLoaded;
    }

    private void MaterializeFixtureResources()
    {
        // Keep the ambiguity fixture deterministic without making the inspector realize resources.
        foreach (var key in new object[]
                 {
                     "Provenance.StaticBrush",
                     "Provenance.DynamicBrush",
                     "Provenance.AmbiguousWidthA",
                     "Provenance.AmbiguousWidthB",
                     "Provenance.Converter",
                     "Provenance.BaseTextStyle",
                     "Provenance.ExplicitTextStyle",
                     typeof(Button),
                     "Provenance.ButtonTemplate"
                 })
        {
            _ = Resources[key];
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimatedProbe.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.25, 0.25, TimeSpan.FromDays(1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }
}

public sealed class ProvenanceViewModel
{
    public string BoundText => "Bound from the fixture view model";
}

public sealed class ProvenanceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CoercedProbe : Border
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(CoercedProbe),
        new FrameworkPropertyMetadata(10d, propertyChangedCallback: null, CoerceLevel));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static object CoerceLevel(DependencyObject dependencyObject, object baseValue) =>
        Math.Clamp((double)baseValue, 0d, 100d);
}

public sealed class ThemeProbe : Control
{
    static ThemeProbe()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ThemeProbe),
            new FrameworkPropertyMetadata(typeof(ThemeProbe)));
    }
}

public sealed class ThrowingValueProbe : Border
{
    public static readonly DependencyProperty PayloadProperty = DependencyProperty.Register(
        nameof(Payload),
        typeof(object),
        typeof(ThrowingValueProbe));

    public object? Payload
    {
        get => GetValue(PayloadProperty);
        set => SetValue(PayloadProperty, value);
    }
}

public sealed class ThrowingDisplayValue
{
    public override string ToString() =>
        throw new InvalidOperationException("Provenance must not invoke application-defined ToString().");

    public override bool Equals(object? obj) =>
        throw new InvalidOperationException("Provenance must not invoke application-defined Equals().");

    public override int GetHashCode() => 1;
}

public sealed class UnsafeResourceKey
{
    public override string ToString() =>
        throw new InvalidOperationException("Provenance must not invoke application-defined ToString().");
}
