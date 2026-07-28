using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using WpfToolsMcp.Automation;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class ViewportSizingTests
{
    private McpTestContext _mcp = null!;
    private string _sessionId = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var serverExe = McpServerPaths.FindMcpServerExecutable();
        _mcp = await McpTestContext.StartAsync(serverExe, toolProfile: "diagnostics");

        var appExe = TestAppPaths.FindViewportProbeTestAppExecutable();
        var launch = await _mcp.CallToolAsync<LaunchAppResponse>("launch_app", new Dictionary<string, object?>
        {
            ["exePath"] = appExe,
            ["workingDirectory"] = Path.GetDirectoryName(appExe)
        });

        _sessionId = launch.SessionId;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                _ = await _mcp.CallToolAsync<CloseAppResponse>("close_session", new Dictionary<string, object?>
                {
                    ["sessionId"] = _sessionId,
                    ["force"] = true,
                    ["timeoutMs"] = 2000
                });
            }
        }
        catch
        {
        }

        await _mcp.DisposeAsync();
    }

    [Test]
    public async Task Physical_pixel_sizing_is_exact_repeatable_and_idempotent()
    {
        var first = await SetViewportAsync(640, 480, "physicalPixels");
        var middle = await SetViewportAsync(720, 520, "physicalPixels");
        var repeated = await SetViewportAsync(640, 480, "physicalPixels");
        var idempotent = await SetViewportAsync(640, 480, "physicalPixels");

        AssertExactPhysicalViewport(first, 640, 480);
        AssertExactPhysicalViewport(middle, 720, 520);
        AssertExactPhysicalViewport(repeated, 640, 480);
        AssertExactPhysicalViewport(idempotent, 640, 480);

        Assert.Multiple(() =>
        {
            Assert.That(repeated.Actual.ClientBoundsPhysicalPixels, Is.EqualTo(first.Actual.ClientBoundsPhysicalPixels));
            Assert.That(repeated.Actual.OuterBoundsPhysicalPixels, Is.EqualTo(first.Actual.OuterBoundsPhysicalPixels));
            Assert.That(repeated.Actual.FramePhysicalPixels, Is.EqualTo(first.Actual.FramePhysicalPixels));
            Assert.That(repeated.Actual.Dpi, Is.EqualTo(first.Actual.Dpi));
            Assert.That(repeated.Actual.Monitor, Is.EqualTo(first.Actual.Monitor));
            Assert.That(repeated.Actual.WindowState, Is.EqualTo(WindowState.Normal));
            Assert.That(idempotent.Updated, Is.False);
        });

        AssertNativeClientSize(idempotent.WindowHandleUsed, 640, 480);
    }

    [Test]
    public async Task Wpf_dip_sizing_matches_the_probe_logical_size_and_physical_pixels()
    {
        const double requestedWidth = 421;
        const double requestedHeight = 319;

        var response = await SetViewportAsync(requestedWidth, requestedHeight, "wpfDips");
        var expectedWidthPixels = checked((int)response.Actual.ClientSizePhysicalPixels.Width);
        var expectedHeightPixels = checked((int)response.Actual.ClientSizePhysicalPixels.Height);
        var probe = await WaitForProbeStatusAsync(
            response.WindowHandleUsed,
            expectedWidthPixels,
            expectedHeightPixels);

        Assert.Multiple(() =>
        {
            Assert.That(response.Requested.Unit, Is.EqualTo(ViewportUnit.WpfDips));
            AssertViewportSize(response.Requested.ClientSize, requestedWidth, requestedHeight);
            AssertViewportSize(
                response.Requested.ClientSizePhysicalPixels,
                probe.PhysicalWidth,
                probe.PhysicalHeight);
            AssertViewportSize(
                response.Actual.ClientSizePhysicalPixels,
                probe.PhysicalWidth,
                probe.PhysicalHeight);
            AssertViewportSize(
                response.Actual.ClientSizeWpfDips,
                probe.LogicalWidth,
                probe.LogicalHeight,
                tolerance: 0.011d);
            Assert.That(response.Actual.Dpi.WindowDpiX, Is.EqualTo(probe.DpiX));
            Assert.That(response.Actual.Dpi.WindowDpiY, Is.EqualTo(probe.DpiY));
            Assert.That(response.Actual.Dpi.MonitorDpiX, Is.GreaterThan(0));
            Assert.That(response.Actual.Dpi.MonitorDpiY, Is.GreaterThan(0));
            Assert.That(response.Adjustment.MinimumSizeConstrained, Is.False);
            Assert.That(
                response.Adjustment.Constraints.Contains(ViewportConstraint.DpiRounding),
                Is.EqualTo(!response.Adjustment.ExactMatch));
            Assert.That(response.Adjustment.ClientSizeDeltaWpfDips.Width,
                Is.EqualTo(response.Actual.ClientSizeWpfDips.Width - requestedWidth).Within(0.000_001d));
            Assert.That(response.Adjustment.ClientSizeDeltaWpfDips.Height,
                Is.EqualTo(response.Actual.ClientSizeWpfDips.Height - requestedHeight).Within(0.000_001d));
        });

        AssertNativeClientSize(response.WindowHandleUsed, expectedWidthPixels, expectedHeightPixels);
    }

    [Test]
    public async Task Minimum_size_constraint_is_reported_without_claiming_an_exact_match()
    {
        var response = await SetViewportAsync(100, 80, "physicalPixels");

        Assert.Multiple(() =>
        {
            Assert.That(response.Adjustment.ExactMatch, Is.False);
            Assert.That(response.Adjustment.MinimumSizeConstrained, Is.True);
            Assert.That(response.Adjustment.Constraints, Does.Contain(ViewportConstraint.MinimumSize));
            Assert.That(response.Actual.ClientSizePhysicalPixels.Width, Is.GreaterThan(100));
            Assert.That(response.Actual.ClientSizePhysicalPixels.Height, Is.GreaterThan(80));
            Assert.That(response.Adjustment.ClientSizeDeltaPhysicalPixels.Width, Is.GreaterThan(0));
            Assert.That(response.Adjustment.ClientSizeDeltaPhysicalPixels.Height, Is.GreaterThan(0));
            Assert.That(response.Actual.WindowState, Is.EqualTo(WindowState.Normal));
        });

        AssertNativeClientSize(
            response.WindowHandleUsed,
            checked((int)response.Actual.ClientSizePhysicalPixels.Width),
            checked((int)response.Actual.ClientSizePhysicalPixels.Height));
    }

    [Test]
    public async Task Minimum_constrained_viewport_is_repositioned_inside_the_work_area()
    {
        var baseline = await SetViewportAsync(640, 480, "physicalPixels");
        var workArea = baseline.Actual.Monitor.WorkAreaPhysicalPixels;
        var originalOuter = baseline.Actual.OuterBoundsPhysicalPixels;

        try
        {
            _ = await SetOuterPositionAsync(
                workArea.X + workArea.Width - originalOuter.Width,
                workArea.Y + workArea.Height - originalOuter.Height);

            var response = await SetViewportAsync(
                100,
                80,
                "physicalPixels",
                clampToWorkArea: true);
            var repeated = await SetViewportAsync(
                100,
                80,
                "physicalPixels",
                clampToWorkArea: true);
            var outer = response.Actual.OuterBoundsPhysicalPixels;

            Assert.Multiple(() =>
            {
                Assert.That(response.Adjustment.MinimumSizeConstrained, Is.True);
                Assert.That(response.Adjustment.Constraints, Does.Contain(ViewportConstraint.MinimumSize));
                Assert.That(response.Adjustment.Constraints, Does.Not.Contain(ViewportConstraint.ApplicationConstraint));
                Assert.That(outer.X, Is.GreaterThanOrEqualTo(workArea.X));
                Assert.That(outer.Y, Is.GreaterThanOrEqualTo(workArea.Y));
                Assert.That(outer.X + outer.Width, Is.LessThanOrEqualTo(workArea.X + workArea.Width));
                Assert.That(outer.Y + outer.Height, Is.LessThanOrEqualTo(workArea.Y + workArea.Height));
                Assert.That(repeated.Actual, Is.EqualTo(response.Actual));
                Assert.That(repeated.Adjustment.Constraints, Is.EqualTo(response.Adjustment.Constraints));
            });
        }
        finally
        {
            _ = await SetOuterPositionAsync(originalOuter.X, originalOuter.Y);
            _ = await SetViewportAsync(640, 480, "physicalPixels");
        }
    }

    [Test]
    public async Task Unreported_application_size_coercion_is_not_mislabeled_as_a_minimum()
    {
        var baseline = await SetViewportAsync(640, 480, "physicalPixels");
        SetApplicationConstraint(baseline.WindowHandleUsed, enabled: true);

        try
        {
            var response = await SetViewportAsync(640, 480, "physicalPixels");

            Assert.Multiple(() =>
            {
                Assert.That(response.Adjustment.ExactMatch, Is.False);
                Assert.That(response.Adjustment.MinimumSizeConstrained, Is.False);
                Assert.That(response.Adjustment.Constraints, Does.Contain(ViewportConstraint.ApplicationConstraint));
                Assert.That(response.Adjustment.Constraints, Does.Not.Contain(ViewportConstraint.MinimumSize));
                Assert.That(response.Actual.ClientSizePhysicalPixels.Width, Is.GreaterThan(640));
            });
        }
        finally
        {
            SetApplicationConstraint(baseline.WindowHandleUsed, enabled: false);
            _ = await SetViewportAsync(640, 480, "physicalPixels");
        }
    }

    [Test]
    public async Task Work_area_clamping_is_explicit_and_keeps_outer_bounds_visible()
    {
        var baseline = await SetViewportAsync(640, 480, "physicalPixels");
        var workArea = baseline.Actual.Monitor.WorkAreaPhysicalPixels;

        try
        {
            var response = await SetViewportAsync(
                workArea.Width,
                workArea.Height,
                "physicalPixels",
                clampToWorkArea: true);
            var outer = response.Actual.OuterBoundsPhysicalPixels;

            Assert.Multiple(() =>
            {
                Assert.That(response.Adjustment.WasClamped, Is.True);
                Assert.That(response.Adjustment.ExactMatch, Is.False);
                Assert.That(response.Adjustment.Constraints, Does.Contain(ViewportConstraint.WorkAreaClamped));
                Assert.That(outer.X, Is.GreaterThanOrEqualTo(workArea.X));
                Assert.That(outer.Y, Is.GreaterThanOrEqualTo(workArea.Y));
                Assert.That(outer.X + outer.Width, Is.LessThanOrEqualTo(workArea.X + workArea.Width));
                Assert.That(outer.Y + outer.Height, Is.LessThanOrEqualTo(workArea.Y + workArea.Height));
                Assert.That(response.Actual.ClientSizePhysicalPixels.Width, Is.LessThan(workArea.Width));
                Assert.That(response.Actual.ClientSizePhysicalPixels.Height, Is.LessThan(workArea.Height));
            });
        }
        finally
        {
            _ = await SetViewportAsync(640, 480, "physicalPixels");
        }
    }

    [Test]
    public async Task Screenshot_viewport_context_is_current_for_each_capture()
    {
        string? firstPath = null;
        string? secondPath = null;

        try
        {
            _ = await SetViewportAsync(640, 480, "physicalPixels");
            var firstScreenshot = await TakeClientScreenshotAsync();
            firstPath = firstScreenshot.Path;

            _ = await SetViewportAsync(700, 500, "physicalPixels");
            var secondScreenshot = await TakeClientScreenshotAsync();
            secondPath = secondScreenshot.Path;
            var firstViewport = firstScreenshot.Viewport
                ?? throw new AssertionException("First screenshot omitted requested viewport evidence.");
            var secondViewport = secondScreenshot.Viewport
                ?? throw new AssertionException("Second screenshot omitted requested viewport evidence.");

            Assert.Multiple(() =>
            {
                Assert.That(firstScreenshot.Width, Is.EqualTo(640));
                Assert.That(firstScreenshot.Height, Is.EqualTo(480));
                Assert.That(secondScreenshot.Width, Is.EqualTo(700));
                Assert.That(secondScreenshot.Height, Is.EqualTo(500));
                AssertViewportSize(firstViewport.ClientSizePhysicalPixels, 640, 480);
                AssertViewportSize(secondViewport.ClientSizePhysicalPixels, 700, 500);
                Assert.That(firstViewport.ClientBoundsPhysicalPixels.Width, Is.EqualTo(firstScreenshot.Width));
                Assert.That(firstViewport.ClientBoundsPhysicalPixels.Height, Is.EqualTo(firstScreenshot.Height));
                Assert.That(secondViewport.ClientBoundsPhysicalPixels.Width, Is.EqualTo(secondScreenshot.Width));
                Assert.That(secondViewport.ClientBoundsPhysicalPixels.Height, Is.EqualTo(secondScreenshot.Height));
                Assert.That(firstViewport.WindowState, Is.EqualTo(WindowState.Normal));
                Assert.That(secondViewport.WindowState, Is.EqualTo(WindowState.Normal));
                Assert.That(File.Exists(firstScreenshot.Path), Is.True);
                Assert.That(File.Exists(secondScreenshot.Path), Is.True);
            });
        }
        finally
        {
            TryDeleteFile(firstPath);
            TryDeleteFile(secondPath);
        }
    }

    private async Task<SetWindowViewportResponse> SetViewportAsync(
        double clientWidth,
        double clientHeight,
        string unit,
        bool clampToWorkArea = false)
    {
        try
        {
            return await _mcp.CallToolAsync<SetWindowViewportResponse>("set_window_viewport", new Dictionary<string, object?>
            {
                ["sessionId"] = _sessionId,
                ["clientWidth"] = clientWidth,
                ["clientHeight"] = clientHeight,
                ["unit"] = unit,
                ["clampToWorkArea"] = clampToWorkArea,
                ["ensureForeground"] = false
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"set_window_viewport failed. Server stderr:{Environment.NewLine}{string.Join(Environment.NewLine, _mcp.ServerStderrLines)}",
                ex);
        }
    }

    private async Task<TakeScreenshotResponse> TakeClientScreenshotAsync() =>
        await _mcp.CallToolAsync<TakeScreenshotResponse>("take_screenshot", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["captureMode"] = "printWindow",
            ["area"] = "client",
            ["includeViewport"] = true
        });

    private async Task<SetWindowBoundsResponse> SetOuterPositionAsync(int x, int y) =>
        await _mcp.CallToolAsync<SetWindowBoundsResponse>("set_window_bounds", new Dictionary<string, object?>
        {
            ["sessionId"] = _sessionId,
            ["x"] = x,
            ["y"] = y,
            ["ensureForeground"] = false
        });

    private static void AssertNativeClientSize(long windowHandle, int expectedWidth, int expectedHeight)
    {
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.That(previousDpiContext, Is.Not.EqualTo(IntPtr.Zero));
        try
        {
            var hwnd = new IntPtr(windowHandle);
            Assert.That(GetClientRect(hwnd, out var clientRect), Is.True);
            var topLeft = new NativePoint(clientRect.Left, clientRect.Top);
            var bottomRight = new NativePoint(clientRect.Right, clientRect.Bottom);
            Assert.Multiple(() =>
            {
                Assert.That(ClientToScreen(hwnd, ref topLeft), Is.True);
                Assert.That(ClientToScreen(hwnd, ref bottomRight), Is.True);
            });
            Assert.Multiple(() =>
            {
                Assert.That(bottomRight.X - topLeft.X, Is.EqualTo(expectedWidth));
                Assert.That(bottomRight.Y - topLeft.Y, Is.EqualTo(expectedHeight));
            });
        }
        finally
        {
            if (previousDpiContext != IntPtr.Zero)
            {
                Assert.That(SetThreadDpiAwarenessContext(previousDpiContext), Is.Not.EqualTo(IntPtr.Zero));
            }
        }
    }

    private static void SetApplicationConstraint(long windowHandle, bool enabled)
    {
        const uint WmAppConstrainViewport = 0x8013;
        _ = SendMessage(
            new IntPtr(windowHandle),
            WmAppConstrainViewport,
            enabled ? new IntPtr(1) : IntPtr.Zero,
            IntPtr.Zero);
    }

    private static async Task<ProbeStatus> WaitForProbeStatusAsync(
        long windowHandle,
        int expectedPhysicalWidth,
        int expectedPhysicalHeight)
    {
        ProbeStatus? latest = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            latest = TryReadProbeStatus(windowHandle);
            if (latest is { } status &&
                status.PhysicalWidth == expectedPhysicalWidth &&
                status.PhysicalHeight == expectedPhysicalHeight)
            {
                return status;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            $"ViewportProbe did not report {expectedPhysicalWidth}x{expectedPhysicalHeight} physical pixels. Last status: {latest}.");
        return default;
    }

    private static ProbeStatus? TryReadProbeStatus(long windowHandle)
    {
        var title = new StringBuilder(512);
        if (GetWindowText(new IntPtr(windowHandle), title, title.Capacity) <= 0)
        {
            return null;
        }

        var match = Regex.Match(
            title.ToString(),
            @"logical=(?<lw>[0-9.]+)x(?<lh>[0-9.]+); physical=(?<pw>[0-9]+)x(?<ph>[0-9]+); dpi=(?<dx>[0-9.]+)x(?<dy>[0-9.]+)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return new ProbeStatus(
            double.Parse(match.Groups["lw"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["lh"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["pw"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["ph"].Value, CultureInfo.InvariantCulture),
            checked((uint)double.Parse(match.Groups["dx"].Value, CultureInfo.InvariantCulture)),
            checked((uint)double.Parse(match.Groups["dy"].Value, CultureInfo.InvariantCulture)));
    }

    private static void AssertExactPhysicalViewport(
        SetWindowViewportResponse response,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Multiple(() =>
        {
            Assert.That(response.Requested.Unit, Is.EqualTo(ViewportUnit.PhysicalPixels));
            AssertViewportSize(response.Requested.ClientSize, expectedWidth, expectedHeight);
            AssertViewportSize(response.Requested.ClientSizePhysicalPixels, expectedWidth, expectedHeight);
            AssertViewportSize(response.Actual.ClientSizePhysicalPixels, expectedWidth, expectedHeight);
            Assert.That(response.Adjustment.ExactMatch, Is.True);
            Assert.That(response.Adjustment.WasClamped, Is.False);
            Assert.That(response.Adjustment.MinimumSizeConstrained, Is.False);
            Assert.That(response.Adjustment.Constraints, Is.Empty);
        });
    }

    private static void AssertViewportSize(
        ViewportSize actual,
        double expectedWidth,
        double expectedHeight,
        double tolerance = 0d)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Width, Is.EqualTo(expectedWidth).Within(tolerance));
            Assert.That(actual.Height, Is.EqualTo(expectedHeight).Within(tolerance));
        });
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    private readonly record struct ProbeStatus(
        double LogicalWidth,
        double LogicalHeight,
        int PhysicalWidth,
        int PhysicalHeight,
        uint DpiX,
        uint DpiY);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }
}
