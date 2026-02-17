using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace WpfPilot.Agent;

public static class EntryPoint
{
    private static readonly object Sync = new();
    private static Task? _serverTask;
    private static bool _assemblyResolutionConfigured;

    public static int Start(string pipeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return 1;
            }

            EnsureAssemblyResolutionConfigured();

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

    private static void EnsureAssemblyResolutionConfigured()
    {
        lock (Sync)
        {
            if (_assemblyResolutionConfigured)
            {
                return;
            }

            _assemblyResolutionConfigured = true;
        }

        var agentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(agentDir))
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(agentDir, $"{name.Name}.dll");
            if (!File.Exists(candidate))
            {
                return null;
            }

            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            catch
            {
                return null;
            }
        };
    }
}
