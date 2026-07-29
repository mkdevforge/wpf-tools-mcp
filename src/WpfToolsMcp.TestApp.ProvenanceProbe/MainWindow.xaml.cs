using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WpfToolsMcp.TestApp.ProvenanceProbe;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DeferredResourceProbe.Tag = DeferredRealizationSentinel.RealizationCount == 0
            ? "not-realized"
            : "realized";
        MaterializeFixtureResources();
        DataContext = new ProvenanceViewModel();
        LongAnimatedProbe.Text = new string('A', 3000);

        var elementBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x48, 0xA8));
        ElementResourceProbe.Resources.Add("Provenance.ElementBrush", elementBrush);
        ElementResourceProbe.Background = elementBrush;

        var applicationBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x48, 0x52));
        Application.Current.Resources.Add("Provenance.ApplicationBrush", applicationBrush);
        ApplicationResourceProbe.Background = applicationBrush;

        var mergedBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x78, 0x8F));
        var mergedDictionary = new ResourceDictionary
        {
            ["Provenance.MergedBrush"] = mergedBrush
        };
        MergedResourceProbe.Resources.MergedDictionaries.Add(mergedDictionary);
        MergedResourceProbe.Background = mergedBrush;

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

        var stringAnimation = new StringAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromDays(1),
            RepeatBehavior = RepeatBehavior.Forever
        };
        stringAnimation.KeyFrames.Add(new DiscreteStringKeyFrame(
            "animated",
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        LongAnimatedProbe.BeginAnimation(TextBlock.TextProperty, stringAnimation);
    }

    internal void MarkDeferredResourceRealized()
    {
        DeferredResourceProbe.Tag = "realized";
    }
}

public sealed class ProvenanceViewModel
{
    public string BoundText => "Bound from the fixture view model";

    public string SecondaryText => "Secondary fixture value";
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

public sealed class LongDefaultProbe : Border
{
    public static readonly DependencyProperty LongTextProperty = DependencyProperty.Register(
        nameof(LongText),
        typeof(string),
        typeof(LongDefaultProbe),
        new FrameworkPropertyMetadata(new string('D', 3000)));

    public string LongText
    {
        get => (string)GetValue(LongTextProperty);
        set => SetValue(LongTextProperty, value);
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

public sealed class DeferredRealizationSentinel
{
    private static int _realizationCount;

    public DeferredRealizationSentinel()
    {
        Interlocked.Increment(ref _realizationCount);
        if (Application.Current?.MainWindow is MainWindow window)
        {
            window.MarkDeferredResourceRealized();
        }
    }

    public static int RealizationCount => Volatile.Read(ref _realizationCount);
}
