using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfToolsMcp.Automation;

internal static class SnoopInjector
{
    public static async Task<InjectionRunResult> InjectAsync(
        Phase2Assets assets,
        int targetPid,
        long targetHwnd,
        Architecture targetArchitecture,
        string pipeName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var injectorExe = assets.GetInjectorLauncherPath(targetArchitecture);
        var injectorDll = assets.GetGenericInjectorPath(targetArchitecture);

        if (!File.Exists(injectorExe))
        {
            throw new FileNotFoundException(
                $"Snoop injector launcher not found: '{injectorExe}'. Run 'scripts/build-snoop.ps1' and rebuild WpfToolsMcp.McpServer.",
                injectorExe);
        }

        if (!File.Exists(injectorDll))
        {
            if (targetArchitecture == Architecture.Arm64)
            {
                throw new NotSupportedException(
                    $"ARM64 target injection is not supported because '{Path.GetFileName(injectorDll)}' is not available. " +
                    "Snoop.GenericInjector currently builds x86/x64 only.");
            }

            throw new FileNotFoundException(
                $"Snoop generic injector not found: '{injectorDll}'. Run 'scripts/build-snoop.ps1' and rebuild WpfToolsMcp.McpServer.",
                injectorDll);
        }

        var targetHwndInt = unchecked((int)targetHwnd);
        var args = new StringBuilder();
        args.Append("-t ").Append(targetPid).Append(' ');
        args.Append("-h ").Append(targetHwndInt).Append(' ');
        args.Append("-a ").Append('"').Append(assets.AgentDllPath).Append('"').Append(' ');
        args.Append("-c ").Append('"').Append("WpfToolsMcp.Agent.EntryPoint").Append('"').Append(' ');
        args.Append("-m ").Append('"').Append("Start").Append('"').Append(' ');
        args.Append("-s ").Append('"').Append(pipeName).Append('"').Append(' ');
        args.Append("-v");

        var startInfo = new ProcessStartInfo
        {
            FileName = injectorExe,
            Arguments = args.ToString(),
            WorkingDirectory = assets.SnoopDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var workspace = InjectorLaunchWorkspace.Create();
        workspace.ApplyTo(startInfo);

        return await InjectorProcessRunner.RunAsync(
            startInfo,
            InjectorProcessRunner.GetConfiguredTimeout(),
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record InjectionRunResult(int ExitCode, string Stdout, string Stderr);
