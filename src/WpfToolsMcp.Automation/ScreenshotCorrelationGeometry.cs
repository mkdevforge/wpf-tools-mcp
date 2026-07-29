using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class ScreenshotCorrelationGeometry
{
    public static Rect MapImageRegionToScreen(
        Rect imageRegion,
        int imageWidth,
        int imageHeight,
        Rect capturedBounds)
    {
        ValidateImageGeometry(imageWidth, imageHeight, capturedBounds);
        ValidateImageRegion(imageRegion, imageWidth, imageHeight);

        var left = capturedBounds.X + ScaleFloor(imageRegion.X, capturedBounds.Width, imageWidth);
        var top = capturedBounds.Y + ScaleFloor(imageRegion.Y, capturedBounds.Height, imageHeight);
        var right = capturedBounds.X + ScaleCeiling(
            checked(imageRegion.X + imageRegion.Width),
            capturedBounds.Width,
            imageWidth);
        var bottom = capturedBounds.Y + ScaleCeiling(
            checked(imageRegion.Y + imageRegion.Height),
            capturedBounds.Height,
            imageHeight);

        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public static Rect? MapScreenRegionToImage(
        Rect screenRegion,
        int imageWidth,
        int imageHeight,
        Rect capturedBounds)
    {
        ValidateImageGeometry(imageWidth, imageHeight, capturedBounds);

        var visible = Intersect(screenRegion, capturedBounds);
        if (visible is null)
        {
            return null;
        }

        var left = ScaleFloor(visible.X - capturedBounds.X, imageWidth, capturedBounds.Width);
        var top = ScaleFloor(visible.Y - capturedBounds.Y, imageHeight, capturedBounds.Height);
        var right = ScaleCeiling(
            checked(visible.X + visible.Width - capturedBounds.X),
            imageWidth,
            capturedBounds.Width);
        var bottom = ScaleCeiling(
            checked(visible.Y + visible.Height - capturedBounds.Y),
            imageHeight,
            capturedBounds.Height);

        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, left, imageWidth);
        bottom = Math.Clamp(bottom, top, imageHeight);

        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    public static ScreenshotCorrelationPoint GetCanonicalScreenPoint(Rect screenRegion)
    {
        if (screenRegion.Width <= 0 || screenRegion.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenRegion), "Screen region must have positive dimensions.");
        }

        return new ScreenshotCorrelationPoint(
            checked(screenRegion.X + screenRegion.Width / 2),
            checked(screenRegion.Y + screenRegion.Height / 2));
    }

    public static Rect? Intersect(Rect first, Rect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min((long)first.X + first.Width, (long)second.X + second.Width);
        var bottom = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height);

        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new Rect(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }

    public static bool ContainsPoint(Rect bounds, int x, int y) =>
        bounds.Width > 0 &&
        bounds.Height > 0 &&
        x >= bounds.X &&
        x < (long)bounds.X + bounds.Width &&
        y >= bounds.Y &&
        y < (long)bounds.Y + bounds.Height;

    private static void ValidateImageGeometry(int imageWidth, int imageHeight, Rect capturedBounds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageHeight, 1);

        if (capturedBounds.Width <= 0 || capturedBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capturedBounds), "Captured bounds must have positive dimensions.");
        }
    }

    private static void ValidateImageRegion(Rect imageRegion, int imageWidth, int imageHeight)
    {
        if (imageRegion.X < 0 || imageRegion.Y < 0 || imageRegion.Width <= 0 || imageRegion.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageRegion),
                "Screenshot correlation coordinates and dimensions must be positive and inside the captured image.");
        }

        var right = (long)imageRegion.X + imageRegion.Width;
        var bottom = (long)imageRegion.Y + imageRegion.Height;
        if (right > imageWidth || bottom > imageHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageRegion),
                $"Screenshot correlation region {imageRegion} exceeds image bounds 0,0,{imageWidth},{imageHeight}.");
        }
    }

    private static int ScaleFloor(int value, int numerator, int denominator) =>
        checked((int)Math.Floor((double)value * numerator / denominator));

    private static int ScaleCeiling(int value, int numerator, int denominator) =>
        checked((int)Math.Ceiling((double)value * numerator / denominator));
}
