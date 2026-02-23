using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using ContractRect = WpfPilot.Contracts.Rect;

namespace WpfPilot.Automation;

internal static class HighlightOverlay
{
    private static readonly object Sync = new();
    private static OverlayHost? _host;

    public static void Hide()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        OverlayHost? host;
        lock (Sync)
        {
            host = _host;
        }

        host?.TryHide();
    }

    public static async Task<HighlightOverlayResult> ShowAsync(
        ContractRect bounds,
        string color,
        int thickness,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HighlightOverlayResult(false, "not_windows");
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new HighlightOverlayResult(false, "no_bounds");
        }

        thickness = Math.Clamp(thickness, 1, 20);
        durationMs = Math.Clamp(durationMs, 1, 30_000);

        OverlayHost host;
        lock (Sync)
        {
            _host ??= new OverlayHost();
            host = _host;
        }

        try
        {
            return await host.ShowAsync(bounds, color, thickness, durationMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                if (ReferenceEquals(_host, host))
                {
                    _host = null;
                }
            }

            return new HighlightOverlayResult(false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private sealed class OverlayHost
    {
        private readonly TaskCompletionSource<IntPtr> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ConcurrentDictionary<int, TaskCompletionSource<HighlightOverlayResult>> _pending = new();

        private int _requestId;

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

        public bool TryHide()
        {
            try
            {
                if (!_ready.Task.IsCompletedSuccessfully)
                {
                    return false;
                }

                var hwnd = _ready.Task.Result;
                if (hwnd == IntPtr.Zero)
                {
                    return false;
                }

                _ = SendMessage(hwnd, WM_APP_HIDE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<HighlightOverlayResult> ShowAsync(
            ContractRect bounds,
            string color,
            int thickness,
            int durationMs,
            CancellationToken cancellationToken)
        {
            IntPtr hwnd;
            try
            {
                hwnd = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return new HighlightOverlayResult(false, "overlay_not_ready");
            }

            if (hwnd == IntPtr.Zero)
            {
                return new HighlightOverlayResult(false, "overlay_window_missing");
            }

            var id = Interlocked.Increment(ref _requestId);
            var tcs = new TaskCompletionSource<HighlightOverlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
            {
                return new HighlightOverlayResult(false, "overlay_request_collision");
            }

            var payload = new ShowPayload(id, bounds, color, thickness, durationMs);
            var handle = GCHandle.Alloc(payload, GCHandleType.Normal);
            try
            {
                if (!PostMessage(hwnd, WM_APP_SHOW, IntPtr.Zero, GCHandle.ToIntPtr(handle)))
                {
                    var error = Marshal.GetLastWin32Error();
                    _pending.TryRemove(id, out _);
                    handle.Free();
                    return new HighlightOverlayResult(false, $"PostMessage_failed {error}");
                }
            }
            catch
            {
                _pending.TryRemove(id, out _);
                if (handle.IsAllocated)
                {
                    handle.Free();
                }

                return new HighlightOverlayResult(false, "PostMessage_exception");
            }

            try
            {
                return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _pending.TryRemove(id, out _);
                throw;
            }
        }

        private void ThreadMain()
        {
            try
            {
                TryEnablePerMonitorDpiAwareness();

                var className = "WpfPilotHighlightOverlay_" + GetStableRandomSuffix();
                var windowClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = WndProc,
                    hInstance = GetModuleHandle(null),
                    lpszClassName = className
                };

                var atom = RegisterClassEx(ref windowClass);
                if (atom == 0)
                {
                    throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
                }

                var hwnd = CreateWindowEx(
                    WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                    className,
                    "WpfPilot Highlight Overlay",
                    WS_POPUP,
                    0,
                    0,
                    1,
                    1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    windowClass.hInstance,
                    IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                }

                var state = new WindowState(hwnd, _pending);
                _ = SetWindowLongPtr(hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(GCHandle.Alloc(state)));

                _ready.TrySetResult(hwnd);

                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) != 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
            }
        }

        private static string GetStableRandomSuffix()
        {
            // Avoid class name collisions across multiple server instances.
            var bytes = RandomNumberGenerator.GetBytes(4);
            return BitConverter.ToString(bytes).Replace("-", "", StringComparison.Ordinal);
        }
    }

    private sealed record ShowPayload(int RequestId, ContractRect Bounds, string Color, int Thickness, int DurationMs);

    internal sealed record HighlightOverlayResult(bool Shown, string? Error = null);

    private sealed class WindowState
    {
        private const uint TimerId = 1;

        private readonly IntPtr _hwnd;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<HighlightOverlayResult>> _pending;
        private readonly object _sync = new();

        private IntPtr _hdcMem = IntPtr.Zero;
        private IntPtr _hBitmap = IntPtr.Zero;
        private IntPtr _hOldBitmap = IntPtr.Zero;
        private IntPtr _bits = IntPtr.Zero;
        private int _width;
        private int _height;

        public WindowState(
            IntPtr hwnd,
            ConcurrentDictionary<int, TaskCompletionSource<HighlightOverlayResult>> pending)
        {
            _hwnd = hwnd;
            _pending = pending;
        }

        public void HandleShow(ShowPayload payload)
        {
            var result = ShowInternal(payload);
            if (_pending.TryRemove(payload.RequestId, out var tcs))
            {
                tcs.TrySetResult(result);
            }
        }

        public void HandleTimer()
        {
            _ = KillTimer(_hwnd, TimerId);
            _ = ShowWindow(_hwnd, SW_HIDE);
        }

        private unsafe HighlightOverlayResult ShowInternal(ShowPayload payload)
        {
            try
            {
                var bounds = payload.Bounds;
                var width = Math.Max(1, bounds.Width);
                var height = Math.Max(1, bounds.Height);
                var thickness = Math.Clamp(payload.Thickness, 1, 20);
                thickness = Math.Min(thickness, Math.Max(1, Math.Min(width / 2, height / 2)));

                if (!TryParseColor(payload.Color, out var color))
                {
                    color = new Color32(0xFF, 0x3B, 0x82, 0xF6);
                }

                lock (_sync)
                {
                    EnsureSurface(width, height);

                    // Fill background with transparent pixels.
                    new Span<byte>((void*)_bits, checked(_width * _height * 4)).Clear();

                    var a = color.A;
                    var pr = (byte)(color.R * a / 255);
                    var pg = (byte)(color.G * a / 255);
                    var pb = (byte)(color.B * a / 255);
                    var pixel = (uint)(pb | ((uint)pg << 8) | ((uint)pr << 16) | ((uint)a << 24));

                    var stridePixels = _width;
                    var basePtr = (uint*)_bits;

                    for (var y = 0; y < _height; y++)
                    {
                        var row = basePtr + (y * stridePixels);
                        var inTop = y < thickness;
                        var inBottom = y >= _height - thickness;

                        if (inTop || inBottom)
                        {
                            for (var x = 0; x < _width; x++)
                            {
                                row[x] = pixel;
                            }

                            continue;
                        }

                        for (var x = 0; x < thickness; x++)
                        {
                            row[x] = pixel;
                        }

                        for (var x = _width - thickness; x < _width; x++)
                        {
                            row[x] = pixel;
                        }
                    }
                }

                var screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    return new HighlightOverlayResult(false, $"GetDC_failed {Marshal.GetLastWin32Error()}");
                }

                try
                {
                    var dst = new POINT(bounds.X, bounds.Y);
                    var size = new SIZE(width, height);
                    var src = new POINT(0, 0);
                    var blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = AC_SRC_ALPHA
                    };

                    if (!UpdateLayeredWindow(
                            _hwnd,
                            screenDc,
                            ref dst,
                            ref size,
                            _hdcMem,
                            ref src,
                            0,
                            ref blend,
                            ULW_ALPHA))
                    {
                        return new HighlightOverlayResult(false, $"UpdateLayeredWindow_failed {Marshal.GetLastWin32Error()}");
                    }

                    _ = ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                    _ = SetTimer(_hwnd, TimerId, (uint)Math.Clamp(payload.DurationMs, 1, 30_000), IntPtr.Zero);
                    return new HighlightOverlayResult(true);
                }
                finally
                {
                    _ = ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            catch (Exception ex)
            {
                return new HighlightOverlayResult(false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void EnsureSurface(int width, int height)
        {
            if (_hdcMem != IntPtr.Zero && _width == width && _height == height)
            {
                return;
            }

            CleanupSurface();

            _width = width;
            _height = height;

            _hdcMem = CreateCompatibleDC(IntPtr.Zero);
            if (_hdcMem == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateCompatibleDC failed: {Marshal.GetLastWin32Error()}");
            }

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            _hBitmap = CreateDIBSection(_hdcMem, ref bmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateDIBSection failed: {Marshal.GetLastWin32Error()}");
            }

            _hOldBitmap = SelectObject(_hdcMem, _hBitmap);
        }

        private void CleanupSurface()
        {
            try
            {
                if (_hdcMem != IntPtr.Zero && _hOldBitmap != IntPtr.Zero)
                {
                    _ = SelectObject(_hdcMem, _hOldBitmap);
                }
            }
            catch
            {
            }

            if (_hBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(_hBitmap);
            }

            if (_hdcMem != IntPtr.Zero)
            {
                _ = DeleteDC(_hdcMem);
            }

            _hdcMem = IntPtr.Zero;
            _hBitmap = IntPtr.Zero;
            _hOldBitmap = IntPtr.Zero;
            _bits = IntPtr.Zero;
            _width = 0;
            _height = 0;
        }

        private static bool TryParseColor(string value, out Color32 color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (!trimmed.StartsWith('#'))
            {
                return false;
            }

            var hex = trimmed.AsSpan(1);
            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            {
                color = new Color32(0xFF, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
                return true;
            }

            if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            {
                color = new Color32((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                return true;
            }

            return false;
        }

        private readonly record struct Color32(byte A, byte R, byte G, byte B);
    }

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var state = GetState(hwnd);
            switch (msg)
            {
                case WM_APP_SHOW:
                    if (state is null)
                    {
                        break;
                    }

                    var handlePtr = lParam;
                    if (handlePtr == IntPtr.Zero)
                    {
                        break;
                    }

                    var gch = GCHandle.FromIntPtr(handlePtr);
                    try
                    {
                        if (gch.Target is ShowPayload payload)
                        {
                            state.HandleShow(payload);
                        }
                    }
                    finally
                    {
                        if (gch.IsAllocated)
                        {
                            gch.Free();
                        }
                    }

                    return IntPtr.Zero;

                case WM_APP_HIDE:
                    state?.HandleTimer();
                    return IntPtr.Zero;

                case WM_TIMER:
                    state?.HandleTimer();
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch
        {
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static WindowState? GetState(IntPtr hwnd)
    {
        try
        {
            var ptr = GetWindowLongPtr(hwnd, GWLP_USERDATA);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            var gch = GCHandle.FromIntPtr(ptr);
            return gch.Target as WindowState;
        }
        catch
        {
            return null;
        }
    }

    private static void TryEnablePerMonitorDpiAwareness()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
        }
    }

    private const int GWLP_USERDATA = -21;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_SHOW = WM_APP + 1;
    private const uint WM_APP_HIDE = WM_APP + 2;

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x00000002;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;

        public POINT(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate WndProc = WndProcImpl;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        [In] ref BITMAPINFO pbmi,
        uint iUsage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint dwOffset);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
