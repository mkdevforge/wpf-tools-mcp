using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal static class ViewportCaptureStabilityCoordinator
{
    public static async Task<StableViewportCapture<T>> CaptureAsync<T>(
        bool includeViewport,
        Func<ViewportConditions> sampleViewport,
        Func<T> capture,
        Action<T> discardCapture,
        Func<CancellationToken, Task> waitForStableViewport,
        CancellationToken cancellationToken,
        int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(sampleViewport);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(discardCapture);
        ArgumentNullException.ThrowIfNull(waitForStableViewport);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        if (!includeViewport)
        {
            return new StableViewportCapture<T>(capture(), null);
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = sampleViewport();
            var captured = capture();
            ViewportConditions after;
            try
            {
                after = sampleViewport();
            }
            catch
            {
                discardCapture(captured);
                throw;
            }

            if (before == after)
            {
                return new StableViewportCapture<T>(captured, after);
            }

            discardCapture(captured);
            if (attempt < maxAttempts)
            {
                await waitForStableViewport(cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"screenshot_viewport_unstable: viewport conditions changed during {maxAttempts} consecutive capture attempts.");
    }
}

internal readonly record struct StableViewportCapture<T>(T Capture, ViewportConditions? Viewport);
