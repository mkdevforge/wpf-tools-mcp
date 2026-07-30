namespace WpfToolsMcp.Automation;

internal static class AgentPipeName
{
    public static string Compute(ProcessInstanceIdentity identity) =>
        $"WpfToolsMcp.Agent.{identity.Pid}.{identity.StartTimeFileTimeUtc}";
}
