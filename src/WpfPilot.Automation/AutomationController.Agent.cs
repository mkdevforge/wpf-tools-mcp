using System.Diagnostics;
using System.Text;
using WpfPilot.Contracts;

namespace WpfPilot.Automation;

public sealed partial class AutomationController
{
    private readonly object _agentSync = new();
    private AgentClient? _agentClient;
    private string? _agentPipeName;
    private int? _agentPid;

    public async Task<InjectAgentResponse> InjectAgentAsync(CancellationToken cancellationToken = default)
    {
        var application = EnsureAttached();
        var automation = EnsureAutomation();

        var pid = application.ProcessId;
        using var process = Process.GetProcessById(pid);
        var pipeName = AgentPipeName.Compute(process);

        AgentClient? existingClient;
        string? existingPipeName;
        int? existingPid;
        lock (_agentSync)
        {
            existingClient = _agentClient;
            existingPipeName = _agentPipeName;
            existingPid = _agentPid;
        }

        if (existingClient is not null &&
            existingClient.IsConnected &&
            existingPipeName is not null &&
            existingPid == pid)
        {
            // Ensure the agent is still responsive
            try
            {
                _ = await existingClient.CallAsync<string>("ping", @params: null, cancellationToken);
                return new InjectAgentResponse(Injected: false, PipeName: existingPipeName);
            }
            catch
            {
                CleanupAgent();
            }
        }

        if (existingClient is not null &&
            (existingPid != pid || !existingClient.IsConnected || existingPipeName is null))
        {
            CleanupAgent();
        }

        // Connect-first: if the app was already injected (e.g. MCP server restarted), we should reconnect without re-injecting.
        var connectFirstClient = await TryConnectToAgentWithRetryAsync(
            pipeName,
            totalTimeout: TimeSpan.FromSeconds(2),
            cancellationToken);

        if (connectFirstClient is not null)
        {
            try
            {
                var pong = await connectFirstClient.CallAsync<string>("ping", @params: null, cancellationToken);
                if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unexpected agent ping response '{pong}'.");
                }
            }
            catch
            {
                await connectFirstClient.DisposeAsync();
                throw;
            }

            lock (_agentSync)
            {
                _agentClient = connectFirstClient;
                _agentPipeName = pipeName;
                _agentPid = pid;
            }

            return new InjectAgentResponse(Injected: false, PipeName: pipeName);
        }

        var assets = Phase2Assets.ResolveFromAppBase();

        var window = FindMainWindow(application, automation);
        var hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Main window handle is not available.");
        }

        var architecture = ProcessArchitectureDetector.GetProcessArchitecture(process);

        var injectResult = await SnoopInjector.InjectAsync(
            assets,
            targetPid: pid,
            targetHwnd: hwnd.ToInt64(),
            targetArchitecture: architecture,
            pipeName: pipeName,
            cancellationToken: cancellationToken);

        if (injectResult.ExitCode != 0)
        {
            var details = BuildInjectorFailureDetails(injectResult);
            throw new InvalidOperationException($"Snoop injection failed (exit code {injectResult.ExitCode}).{details}");
        }

        var client = await ConnectToAgentWithRetryAsync(pipeName, cancellationToken);
        try
        {
            var pong = await client.CallAsync<string>("ping", @params: null, cancellationToken);
            if (!string.Equals(pong, "pong", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected agent ping response '{pong}'.");
            }
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        lock (_agentSync)
        {
            _agentClient = client;
            _agentPipeName = pipeName;
            _agentPid = pid;
        }

        return new InjectAgentResponse(Injected: true, PipeName: pipeName);
    }

    public async Task<AgentPingResponse> AgentPingAsync(CancellationToken cancellationToken = default)
    {
        var client = await EnsureAgentConnectedAsync(cancellationToken);
        var pong = await client.CallAsync<string>("ping", @params: null, cancellationToken);
        return new AgentPingResponse(pong);
    }

    public async Task<GetWpfVisualTreeResponse> GetWpfVisualTreeAsync(
        long? windowHandle = null,
        int depth = 4,
        CancellationToken cancellationToken = default)
    {
        if (depth <= 0)
        {
            depth = 1;
        }

        var client = await EnsureAgentConnectedAsync(cancellationToken);

        var request = new GetWpfVisualTreeRequest(WindowHandle: windowHandle, Depth: depth);
        return await client.CallAsync<GetWpfVisualTreeResponse>("wpf/get_visual_tree", request, cancellationToken);
    }

    private async Task<AgentClient> EnsureAgentConnectedAsync(CancellationToken cancellationToken)
    {
        var application = EnsureAttached();
        var pid = application.ProcessId;

        AgentClient? client;
        lock (_agentSync)
        {
            client = _agentClient;
            if (client is not null && client.IsConnected && _agentPid == pid)
            {
                return client;
            }
        }

        _ = await InjectAgentAsync(cancellationToken);

        lock (_agentSync)
        {
            client = _agentClient;
        }

        return client ?? throw new InvalidOperationException("Agent injection succeeded, but the pipe client was not initialized.");
    }

    private static async Task<AgentClient> ConnectToAgentWithRetryAsync(string pipeName, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(3);

        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await AgentClient.ConnectAsync(pipeName, timeout: TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch
            {
                await Task.Delay(75, cancellationToken);
            }
        }

        return await AgentClient.ConnectAsync(pipeName, timeout: TimeSpan.FromSeconds(1), cancellationToken);
    }

    private static async Task<AgentClient?> TryConnectToAgentWithRetryAsync(
        string pipeName,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        if (totalTimeout <= TimeSpan.Zero)
        {
            totalTimeout = TimeSpan.FromSeconds(1);
        }

        var start = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(start) < totalTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await AgentClient.ConnectAsync(pipeName, timeout: TimeSpan.FromMilliseconds(350), cancellationToken);
            }
            catch
            {
                await Task.Delay(75, cancellationToken);
            }
        }

        return null;
    }

    private static string BuildInjectorFailureDetails(InjectionRunResult result)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            sb.AppendLine();
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(result.Stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            sb.AppendLine();
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(result.Stderr.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private void CleanupAgent()
    {
        AgentClient? client;
        lock (_agentSync)
        {
            client = _agentClient;
            _agentClient = null;
            _agentPipeName = null;
            _agentPid = null;
        }

        if (client is not null)
        {
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort
            }
        }
    }
}
