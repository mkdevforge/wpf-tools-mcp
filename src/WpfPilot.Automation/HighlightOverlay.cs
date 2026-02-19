using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ContractRect = WpfPilot.Contracts.Rect;

namespace WpfPilot.Automation;

internal static class HighlightOverlay
{
    private static readonly object Sync = new();
    private static OverlayHost? _host;

    public static Task<bool> ShowAsync(
        ContractRect bounds,
        string color,
        int thickness,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Task.FromResult(false);
        }

        thickness = Math.Clamp(thickness, 1, 20);
        durationMs = Math.Clamp(durationMs, 1, 30_000);

        OverlayHost host;
        lock (Sync)
        {
            _host ??= new OverlayHost();
            host = _host;
        }

        return host.ShowAsync(bounds, color, thickness, durationMs, cancellationToken);
    }

    private sealed class OverlayHost
    {
        private readonly TaskCompletionSource<(Dispatcher Dispatcher, OverlayWindow Window)> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OverlayHost()
        {
            var thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "WpfPilot.HighlightOverlay"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void ThreadMain()
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var window = new OverlayWindow();
                window.Show();
                window.Hide();
                _ready.TrySetResult((dispatcher, window));
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
            }
        }

        public async Task<bool> ShowAsync(
            ContractRect bounds,
            string color,
            int thickness,
            int durationMs,
            CancellationToken cancellationToken)
        {
            var (dispatcher, window) = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    window.ShowHighlight(bounds, color, thickness, durationMs);
                    tcs.TrySetResult(true);
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            }, DispatcherPriority.Send, cancellationToken);

            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private sealed class OverlayWindow : Window
    {
        private readonly Border _border;
        private readonly DispatcherTimer _hideTimer;
        private IntPtr _hwnd;

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;

            _border = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.DeepSkyBlue,
                BorderThickness = new Thickness(3),
                SnapsToDevicePixels = true
            };

            Content = _border;

            _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                try
                {
                    Hide();
                }
                catch
                {
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                _ = SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle);
            }
            catch
            {
            }
        }

        public void ShowHighlight(ContractRect bounds, string color, int thickness, int durationMs)
        {
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = new WindowInteropHelper(this).Handle;
            }

            _border.BorderBrush = new SolidColorBrush(ParseColor(color));
            _border.BorderThickness = new Thickness(thickness);

            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    _ = SetWindowPos(
                        _hwnd,
                        HWND_TOPMOST,
                        bounds.X,
                        bounds.Y,
                        Math.Max(1, bounds.Width),
                        Math.Max(1, bounds.Height),
                        SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }
            catch
            {
            }

            try
            {
                if (!IsVisible)
                {
                    Show();
                }
            }
            catch
            {
            }

            _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 1, 30_000));
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Colors.DeepSkyBlue;
            }

            try
            {
                var converted = ColorConverter.ConvertFromString(value.Trim());
                if (converted is Color color)
                {
                    return color;
                }
            }
            catch
            {
            }

            return Colors.DeepSkyBlue;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
