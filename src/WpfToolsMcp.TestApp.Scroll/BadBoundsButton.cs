using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.Scroll;

public sealed class BadBoundsButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new BadBoundsButtonAutomationPeer(this);

    private sealed class BadBoundsButtonAutomationPeer(BadBoundsButton owner) : ButtonAutomationPeer(owner)
    {
        protected override Rect GetBoundingRectangleCore()
        {
            if (Owner is not FrameworkElement element)
            {
                return base.GetBoundingRectangleCore();
            }

            var scrollViewer = FindAncestorScrollViewer(element);
            if (scrollViewer is null)
            {
                return base.GetBoundingRectangleCore();
            }

            if (!IsElementInViewport(element, scrollViewer))
            {
                return Rect.Empty;
            }

            return base.GetBoundingRectangleCore();
        }

        protected override bool IsOffscreenCore()
        {
            if (Owner is not FrameworkElement element)
            {
                return base.IsOffscreenCore();
            }

            var scrollViewer = FindAncestorScrollViewer(element);
            if (scrollViewer is null)
            {
                return base.IsOffscreenCore();
            }

            return !IsElementInViewport(element, scrollViewer);
        }

        private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
        {
            DependencyObject? current = start;
            for (var i = 0; i < 200 && current is not null; i++)
            {
                if (current is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static bool IsElementInViewport(FrameworkElement element, ScrollViewer scrollViewer)
        {
            if (!element.IsVisible)
            {
                return false;
            }

            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return false;
            }

            if (scrollViewer.ViewportWidth <= 0 || scrollViewer.ViewportHeight <= 0)
            {
                return false;
            }

            try
            {
                var transform = element.TransformToAncestor(scrollViewer);
                var bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                var viewport = new Rect(0, 0, scrollViewer.ViewportWidth, scrollViewer.ViewportHeight);
                return bounds.IntersectsWith(viewport);
            }
            catch
            {
                return false;
            }
        }
    }
}

