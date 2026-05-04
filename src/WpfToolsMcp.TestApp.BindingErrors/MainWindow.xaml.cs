namespace WpfToolsMcp.TestApp.BindingErrors;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private sealed class MainViewModel
    {
        public string OkText { get; set; } = "Hello";
    }
}

