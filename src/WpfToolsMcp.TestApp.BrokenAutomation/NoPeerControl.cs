using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.BrokenAutomation;

public sealed class NoPeerControl : FrameworkElement
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Brushes.LightCoral, new Pen(Brushes.DarkRed, 1), rect);

        var dpi = VisualTreeHelper.GetDpi(this);
        var text = new FormattedText(
            "NoPeerControl (not in UIA)",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            Brushes.Black,
            dpi.PixelsPerDip);

        drawingContext.DrawText(text, new Point(8, 8));
    }

    protected override Size MeasureOverride(Size availableSize) => new(320, 120);
}

