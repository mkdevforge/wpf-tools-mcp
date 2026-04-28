using System.Runtime.InteropServices;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public static class DisplayDiagnostics
{
    public static ListDisplaysResponse ListDisplays()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ListDisplaysResponse(
                VirtualScreen: new Rect(0, 0, 0, 0),
                Displays: Array.Empty<DisplayInfo>());
        }

        var displays = new List<DisplayInfo>();
        var handle = GCHandle.Alloc(displays);
        var ok = false;
        try
        {
            ok = EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                static (hMonitor, _, _, lParam) =>
                {
                    var gch = GCHandle.FromIntPtr(lParam);
                    var list = (List<DisplayInfo>)gch.Target!;

                    var info = new MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<MONITORINFOEX>()
                    };

                    if (!GetMonitorInfo(hMonitor, ref info))
                    {
                        return true;
                    }

                    var bounds = new Rect(
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top);

                    Rect? workArea = null;
                    if (info.rcWork.Right > info.rcWork.Left && info.rcWork.Bottom > info.rcWork.Top)
                    {
                        workArea = new Rect(
                            info.rcWork.Left,
                            info.rcWork.Top,
                            info.rcWork.Right - info.rcWork.Left,
                            info.rcWork.Bottom - info.rcWork.Top);
                    }

                    double? dpiScaleX = null;
                    double? dpiScaleY = null;
                    if (TryGetMonitorDpi(hMonitor, out var dpiX, out var dpiY))
                    {
                        dpiScaleX = dpiX / 96d;
                        dpiScaleY = dpiY / 96d;
                    }

                    var deviceName = info.szDevice?.TrimEnd('\0') ?? string.Empty;
                    var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

                    list.Add(new DisplayInfo(deviceName, bounds, isPrimary, workArea, dpiScaleX, dpiScaleY));
                    return true;
                },
                GCHandle.ToIntPtr(handle));
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }

        if (!ok)
        {
            displays.Clear();
        }

        // Sort: primary first, then left-to-right, top-to-bottom.
        displays.Sort(static (a, b) =>
        {
            var primary = b.IsPrimary.CompareTo(a.IsPrimary);
            if (primary != 0)
            {
                return primary;
            }

            var ax = a.Bounds.X.CompareTo(b.Bounds.X);
            if (ax != 0)
            {
                return ax;
            }

            return a.Bounds.Y.CompareTo(b.Bounds.Y);
        });

        var virtualScreen = GetVirtualScreenBounds();
        return new ListDisplaysResponse(virtualScreen, displays);
    }

    public static Rect GetVirtualScreenBounds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Rect(0, 0, 0, 0);
        }

        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var cx = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var cy = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new Rect(x, y, cx, cy);
    }

    public static Rect ClampBoundsToVirtualScreen(Rect desired, Rect virtualScreen, out bool wasClamped)
    {
        wasClamped = false;
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            return desired;
        }

        var width = Math.Max(1, desired.Width);
        var height = Math.Max(1, desired.Height);

        var clampedWidth = Math.Min(width, virtualScreen.Width);
        var clampedHeight = Math.Min(height, virtualScreen.Height);
        wasClamped |= clampedWidth != desired.Width || clampedHeight != desired.Height;

        var maxX = virtualScreen.X + virtualScreen.Width - clampedWidth;
        var maxY = virtualScreen.Y + virtualScreen.Height - clampedHeight;

        var clampedX = Math.Clamp(desired.X, virtualScreen.X, maxX);
        var clampedY = Math.Clamp(desired.Y, virtualScreen.Y, maxY);
        wasClamped |= clampedX != desired.X || clampedY != desired.Y;

        return new Rect(clampedX, clampedY, clampedWidth, clampedHeight);
    }

    private static bool TryGetMonitorDpi(IntPtr hMonitor, out uint dpiX, out uint dpiY)
    {
        dpiX = 0;
        dpiY = 0;

        try
        {
            // shcore.dll is available on Windows 8.1+; if not present, we just omit DPI.
            var hr = GetDpiForMonitor(hMonitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            return hr == 0 && dpiX > 0 && dpiY > 0;
        }
        catch
        {
            return false;
        }
    }

    private const int MONITORINFOF_PRIMARY = 0x00000001;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
