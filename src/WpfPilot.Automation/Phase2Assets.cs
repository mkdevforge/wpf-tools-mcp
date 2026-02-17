using System.Runtime.InteropServices;

namespace WpfPilot.Automation;

internal sealed class Phase2Assets
{
    private Phase2Assets(string agentDir, string snoopDir)
    {
        AgentDir = agentDir;
        SnoopDir = snoopDir;
        AgentDllPath = Path.Combine(AgentDir, "WpfPilot.Agent.dll");
    }

    public string AgentDir { get; }
    public string AgentDllPath { get; }
    public string SnoopDir { get; }

    public string GetInjectorLauncherPath(Architecture architecture)
    {
        var arch = architecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            _ => throw new NotSupportedException($"Unsupported architecture: {architecture}")
        };

        return Path.Combine(SnoopDir, $"Snoop.InjectorLauncher.{arch}.exe");
    }

    public string GetGenericInjectorPath(Architecture architecture)
    {
        var arch = architecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            _ => throw new NotSupportedException($"Unsupported architecture: {architecture}")
        };

        return Path.Combine(SnoopDir, $"Snoop.GenericInjector.{arch}.dll");
    }

    public static Phase2Assets ResolveFromAppBase()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            throw new InvalidOperationException("AppContext.BaseDirectory is not available.");
        }

        var agentDir = Path.Combine(baseDir, "agent");
        var snoopDir = Path.Combine(baseDir, "snoop");

        if (!Directory.Exists(agentDir))
        {
            throw new DirectoryNotFoundException(
                $"Phase 2 agent payload directory not found: '{agentDir}'. Build WpfPilot.McpServer to generate it.");
        }

        if (!Directory.Exists(snoopDir))
        {
            throw new DirectoryNotFoundException(
                $"Phase 2 Snoop payload directory not found: '{snoopDir}'. Build WpfPilot.McpServer to generate it.");
        }

        var assets = new Phase2Assets(agentDir, snoopDir);
        if (!File.Exists(assets.AgentDllPath))
        {
            throw new FileNotFoundException(
                $"Phase 2 agent assembly not found: '{assets.AgentDllPath}'. Build WpfPilot.McpServer to generate it.",
                assets.AgentDllPath);
        }

        return assets;
    }
}

