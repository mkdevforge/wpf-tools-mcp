using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfToolsMcp.TestApp.ViewportProbe;

public partial class MainWindow : Window
{
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_APP_CONSTRAIN_VIEWPORT = 0x8013;
    private const uint SWP_NOSIZE = 0x0001;
    private const int ConstrainedOuterWidth = 900;

    private bool _constrainViewport;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
            UpdateViewportStatus();
        };
        SizeChanged += (_, _) => UpdateViewportStatus();
        LayoutUpdated += (_, _) => UpdateViewportStatus();
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WM_APP_CONSTRAIN_VIEWPORT)
        {
            _constrainViewport = wParam != IntPtr.Zero;
            handled = true;
            return IntPtr.Zero;
        }

        if (message == WM_WINDOWPOSCHANGING && _constrainViewport && lParam != IntPtr.Zero)
        {
            var windowPosition = Marshal.PtrToStructure<NativeWindowPosition>(lParam);
            if ((windowPosition.Flags & SWP_NOSIZE) == 0 && windowPosition.Width < ConstrainedOuterWidth)
            {
                windowPosition.Width = ConstrainedOuterWidth;
                Marshal.StructureToPtr(windowPosition, lParam, fDeleteOld: false);
            }
        }

        return IntPtr.Zero;
    }

    private void UpdateViewportStatus()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !GetClientRect(handle, out var clientRect))
        {
            return;
        }

        var clientTopLeft = new NativePoint(clientRect.Left, clientRect.Top);
        var clientBottomRight = new NativePoint(clientRect.Right, clientRect.Bottom);
        if (!ClientToScreen(handle, ref clientTopLeft) ||
            !ClientToScreen(handle, ref clientBottomRight) ||
            !LogicalToPhysicalPointForPerMonitorDpi(handle, ref clientTopLeft) ||
            !LogicalToPhysicalPointForPerMonitorDpi(handle, ref clientBottomRight))
        {
            return;
        }

        var physicalWidth = clientBottomRight.X - clientTopLeft.X;
        var physicalHeight = clientBottomRight.Y - clientTopLeft.Y;
        if (physicalWidth <= 0 || physicalHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(ViewportRoot);
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "logical={0:F2}x{1:F2}; physical={2}x{3}; dpi={4:F0}x{5:F0}",
            ViewportRoot.ActualWidth,
            ViewportRoot.ActualHeight,
            physicalWidth,
            physicalHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY);

        if (!string.Equals(ViewportStatus.Text, text, StringComparison.Ordinal))
        {
            ViewportStatus.Text = text;
        }

        var title = $"WPF Tools MCP ViewportProbe TestApp | {text}";
        if (!string.Equals(Title, title, StringComparison.Ordinal))
        {
            Title = title;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "LogicalToPhysicalPointForPerMonitorDPI", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDpi(IntPtr windowHandle, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public IntPtr WindowHandle;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }
}
