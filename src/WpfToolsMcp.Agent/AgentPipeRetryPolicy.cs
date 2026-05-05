using System.IO;

namespace WpfToolsMcp.Agent;

internal static class AgentPipeRetryPolicy
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(1);

    public static bool CanRetry(Exception exception) =>
        exception is IOException;

    public static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    public static TimeSpan NextDelay(TimeSpan current)
    {
        var nextMilliseconds = current.TotalMilliseconds * 2;
        return TimeSpan.FromMilliseconds(Math.Min(nextMilliseconds, MaxDelay.TotalMilliseconds));
    }
}
