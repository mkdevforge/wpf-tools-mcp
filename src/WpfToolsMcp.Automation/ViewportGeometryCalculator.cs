using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class ViewportGeometryCalculator
{
    private const double DpiBaseline = 96d;

    public static int DipsToPhysicalPixels(double dips, uint dpi)
    {
        if (!double.IsFinite(dips) || dips <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dips), dips, "Viewport dimensions must be finite and greater than zero.");
        }

        if (dpi == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be greater than zero.");
        }

        var pixels = Math.Round(dips * dpi / DpiBaseline, MidpointRounding.AwayFromZero);
        if (pixels is < 1 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(dips), dips, "Viewport dimension is outside the supported pixel range.");
        }

        return checked((int)pixels);
    }

    public static double PhysicalPixelsToDips(int pixels, uint dpi)
    {
        if (pixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "Pixel dimensions cannot be negative.");
        }

        if (dpi == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be greater than zero.");
        }

        return pixels * DpiBaseline / dpi;
    }

    public static int ScalePixelsBetweenDpi(int pixels, uint sourceDpi, uint targetDpi)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels, 1);
        ArgumentOutOfRangeException.ThrowIfZero(sourceDpi);
        ArgumentOutOfRangeException.ThrowIfZero(targetDpi);

        var scaled = Math.Round((double)pixels * targetDpi / sourceDpi, MidpointRounding.AwayFromZero);
        if (scaled is < 1 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "Scaled pixel dimension is outside the supported range.");
        }

        return checked((int)scaled);
    }

    public static ViewportSize ExpandClientToOuter(
        int clientWidth,
        int clientHeight,
        ViewportFrameInsets frame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clientWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(clientHeight, 1);

        return new ViewportSize(
            checked(clientWidth + frame.Left + frame.Right),
            checked(clientHeight + frame.Top + frame.Bottom));
    }

    public static ViewportSize CorrectOuterSize(
        int outerWidth,
        int outerHeight,
        int targetClientWidth,
        int targetClientHeight,
        int actualClientWidth,
        int actualClientHeight) =>
        new(
            Math.Max(1, checked(outerWidth + targetClientWidth - actualClientWidth)),
            Math.Max(1, checked(outerHeight + targetClientHeight - actualClientHeight)));

    public static Rect ClampOuterPosition(Rect outerBounds, Rect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return outerBounds;
        }

        var x = outerBounds.Width >= workArea.Width
            ? workArea.X
            : Math.Clamp(outerBounds.X, workArea.X, workArea.X + workArea.Width - outerBounds.Width);
        var y = outerBounds.Height >= workArea.Height
            ? workArea.Y
            : Math.Clamp(outerBounds.Y, workArea.Y, workArea.Y + workArea.Height - outerBounds.Height);

        return outerBounds with { X = x, Y = y };
    }

    public static PixelDimensions ClampClientSizeToWorkArea(
        PixelDimensions target,
        ViewportFrameInsets frame,
        Rect workArea,
        out bool wasClamped)
    {
        wasClamped = false;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return target;
        }

        var maximumWidth = Math.Max(1, workArea.Width - frame.Left - frame.Right);
        var maximumHeight = Math.Max(1, workArea.Height - frame.Top - frame.Bottom);
        var clamped = new PixelDimensions(
            Math.Min(target.Width, maximumWidth),
            Math.Min(target.Height, maximumHeight));
        wasClamped = clamped != target;
        return clamped;
    }

    public static bool OuterSizeExceedsWorkArea(Rect outerBounds, Rect workArea) =>
        workArea.Width > 0 &&
        workArea.Height > 0 &&
        (outerBounds.Width > workArea.Width || outerBounds.Height > workArea.Height);

    public static bool NearlyEqual(double left, double right, double tolerance = 0.000_001d) =>
        Math.Abs(left - right) <= tolerance;
}

internal readonly record struct PixelDimensions(int Width, int Height);
