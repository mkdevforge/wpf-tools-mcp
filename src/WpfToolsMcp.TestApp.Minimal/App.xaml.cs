using System.IO;
using System.Runtime.Loader;
using System.Windows;

namespace WpfToolsMcp.TestApp.Minimal;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LoadOptionalConflictFixture();
        base.OnStartup(e);
    }

    private static void LoadOptionalConflictFixture()
    {
        var assemblyPath = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_CONFLICT_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return;
        }

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        var markerPath = Environment.GetEnvironmentVariable("WPF_TOOLS_MCP_CONFLICT_MARKER");
        if (!string.IsNullOrWhiteSpace(markerPath))
        {
            File.WriteAllText(markerPath, assembly.FullName);
        }
    }
}
