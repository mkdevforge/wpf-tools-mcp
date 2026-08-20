using System.Reflection;

namespace WpfToolsMcp.Agent;

public static class EntryPoint
{
    private static readonly Lazy<RuntimeEntry> Runtime = new(CreateRuntimeEntry);

    public static int Start(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return 1;
        }

        var result = Runtime.Value.StartMethod.Invoke(null, [pipeName]);
        return result is int exitCode ? exitCode : 1;
    }

    private static RuntimeEntry CreateRuntimeEntry()
    {
        var bootstrapPath = Assembly.GetExecutingAssembly().Location;
        var bootstrapDirectory = Path.GetDirectoryName(bootstrapPath);
        if (string.IsNullOrWhiteSpace(bootstrapDirectory))
        {
            throw new InvalidOperationException("The agent bootstrap directory is unavailable.");
        }

        var runtimePath = Path.Combine(bootstrapDirectory, "WpfToolsMcp.Agent.dll");
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException("The agent runtime assembly is missing.", runtimePath);
        }

        var loadContext = new AgentLoadContext(runtimePath);
        var runtimeAssembly = loadContext.LoadFromAssemblyPath(runtimePath);
        var runtimeType = runtimeAssembly.GetType(
            "WpfToolsMcp.Agent.AgentRuntimeEntryPoint",
            throwOnError: true,
            ignoreCase: false)
            ?? throw new TypeLoadException("The agent runtime entry point is unavailable.");
        var startMethod = runtimeType.GetMethod(
            "Start",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null)
            ?? throw new MissingMethodException(runtimeType.FullName, "Start");

        return new RuntimeEntry(loadContext, startMethod);
    }

    private sealed record RuntimeEntry(AgentLoadContext LoadContext, MethodInfo StartMethod);
}
