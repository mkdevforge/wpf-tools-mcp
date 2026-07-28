using System.Windows;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.LayoutProbe;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        EmptyClipTarget.Clip = Geometry.Empty;
    }
}
