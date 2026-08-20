using System.Reflection;
using System.Runtime.Loader;

namespace WpfToolsMcp.Agent;

internal sealed class AgentLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public AgentLoadContext(string runtimeAssemblyPath)
        : base("WpfToolsMcp.Agent", isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeAssemblyPath);
        _resolver = new AssemblyDependencyResolver(Path.GetFullPath(runtimeAssemblyPath));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        if (IsPrivateAgentAssembly(assemblyName.Name))
        {
            throw new FileNotFoundException(
                $"Packaged agent dependency '{assemblyName}' could not be resolved.");
        }

        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }

    private static bool IsPrivateAgentAssembly(string? assemblyName) =>
        assemblyName?.StartsWith("WpfToolsMcp.", StringComparison.Ordinal) is true ||
        string.Equals(assemblyName, "Snoop.Core", StringComparison.Ordinal);
}
