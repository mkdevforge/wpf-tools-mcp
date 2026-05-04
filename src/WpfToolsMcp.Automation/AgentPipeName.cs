using System.Diagnostics;

namespace WpfToolsMcp.Automation;

internal static class AgentPipeName
{
    public static string Compute(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var pid = process.Id;

        try
        {
            var startTimeUtc = process.StartTime.ToUniversalTime();
            var startStamp = startTimeUtc.ToFileTimeUtc();
            return $"WpfToolsMcp.Agent.{pid}.{startStamp}";
        }
        catch
        {
            return $"WpfToolsMcp.Agent.{pid}";
        }
    }
}
