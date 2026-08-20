namespace WpfToolsMcp.Agent;

public static class AgentRuntimeEntryPoint
{
    private static readonly object Sync = new();
    private static Task? _serverTask;

    public static int Start(string pipeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return 1;
            }

            lock (Sync)
            {
                if (_serverTask is null || _serverTask.IsCompleted)
                {
                    _serverTask = Task.Run(() => AgentServer.RunAsync(pipeName, CancellationToken.None));
                }
            }

            return 0;
        }
        catch
        {
            return 1;
        }
    }

}
