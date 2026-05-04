using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfToolsMcp.TestApp.CustomControls;

public partial class ClickableCard : UserControl
{
    public static readonly RoutedEvent ClickedEvent = EventManager.RegisterRoutedEvent(
        nameof(Clicked),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(ClickableCard));

    public ClickableCard() => InitializeComponent();

    public event RoutedEventHandler Clicked
    {
        add => AddHandler(ClickedEvent, value);
        remove => RemoveHandler(ClickedEvent, value);
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ClickedEvent, this));
    }
}

