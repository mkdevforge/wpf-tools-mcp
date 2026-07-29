using System.Diagnostics;
using System.Globalization;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal readonly record struct ProcessInstanceIdentity(int Pid, long StartTimeFileTimeUtc)
{
    internal string Value => $"{Pid}:{StartTimeFileTimeUtc}";

    internal static bool TryParse(string value, out ProcessInstanceIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            pid <= 0 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime) ||
            startTime <= 0)
        {
            return false;
        }

        identity = new ProcessInstanceIdentity(pid, startTime);
        return true;
    }
}

internal enum ProcessInstanceState
{
    Current,
    ExitedOrReused,
    Unavailable
}

internal sealed record ResolvedProcessTarget(
    ProcessInstanceIdentity Identity,
    string ProcessName,
    DateTime StartTimeUtc,
    long MainWindowHandle,
    string MainWindowTitle)
{
    internal ProcessCandidateInfo ToContract(int index) =>
        new(
            Index: index,
            ProcessInstanceId: Identity.Value,
            Pid: Identity.Pid,
            ProcessName: ProcessName,
            StartTimeUtc: StartTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            MainWindowHandle: MainWindowHandle,
            MainWindowTitle: MainWindowTitle);
}

internal static class ProcessTargetResolver
{
    private const int MaximumReturnedCandidates = 25;

    internal static ResolvedProcessTarget Resolve(AttachToAppRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var selectors = (request.Pid is not null ? 1 : 0) +
                        (!string.IsNullOrWhiteSpace(request.ProcessName) ? 1 : 0) +
                        (!string.IsNullOrWhiteSpace(request.ProcessInstanceId) ? 1 : 0);
        if (selectors != 1)
        {
            throw new ArgumentException(
                "Provide exactly one target selector: pid, processName, or processInstanceId.");
        }

        if (request.Pid is int pid)
        {
            return ResolveByPid(pid);
        }

        if (!string.IsNullOrWhiteSpace(request.ProcessInstanceId))
        {
            return ResolveByInstanceId(request.ProcessInstanceId);
        }

        return ResolveByName(request.ProcessName!);
    }

    internal static ResolvedProcessTarget ResolveByName(string processName)
    {
        var normalizedName = NormalizeProcessName(processName);
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException("processName must not be empty.", nameof(processName));
        }

        var candidates = GetCandidatesByName(normalizedName);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"process_not_found: no live process matches '{normalizedName}'. Start the application or retry with pid.");
        }

        if (candidates.Count > 1)
        {
            var returned = candidates.Take(MaximumReturnedCandidates).ToArray();
            throw new ProcessSelectionAmbiguityException(
                new ProcessSelectionAmbiguity(
                    Code: "ambiguous_process",
                    RequestedProcessName: normalizedName,
                    DiscoveredCandidates: candidates.Count,
                    ReturnedCandidates: returned.Length,
                    Truncated: returned.Length < candidates.Count,
                    TruncatedReason: returned.Length < candidates.Count ? "maxCandidates" : null,
                    Candidates: returned.Select((candidate, index) => candidate.ToContract(index)).ToArray(),
                    Recovery: "Retry attach_to_app with one candidate processInstanceId (preferred) or pid."));
        }

        return candidates[0];
    }

    internal static ResolvedProcessTarget ResolveByPid(int pid)
    {
        if (pid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pid), "pid must be greater than zero.");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return CreateTarget(process);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"process_not_found: process {pid} is not running. Discover the replacement process and retry.");
        }
    }

    internal static bool IsCurrent(ProcessInstanceIdentity identity)
        => Observe(identity) == ProcessInstanceState.Current;

    internal static ProcessInstanceState Observe(ProcessInstanceIdentity identity)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(identity.Pid);
        }
        catch (ArgumentException)
        {
            return ProcessInstanceState.ExitedOrReused;
        }
        catch
        {
            return ProcessInstanceState.Unavailable;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return ProcessInstanceState.ExitedOrReused;
                }

                var currentStartTime = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                return currentStartTime == identity.StartTimeFileTimeUtc
                    ? ProcessInstanceState.Current
                    : ProcessInstanceState.ExitedOrReused;
            }
            catch
            {
                return ProcessInstanceState.Unavailable;
            }
        }
    }

    private static ResolvedProcessTarget ResolveByInstanceId(string processInstanceId)
    {
        if (!ProcessInstanceIdentity.TryParse(processInstanceId, out var identity))
        {
            throw new ArgumentException(
                "processInstanceId is invalid. Use the opaque value returned in an ambiguous_process candidate.",
                nameof(processInstanceId));
        }

        ResolvedProcessTarget current;
        try
        {
            current = ResolveByPid(identity.Pid);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith("process_not_found:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"stale_process_candidate: '{processInstanceId}' is no longer running. Discover candidates again.");
        }

        if (current.Identity != identity)
        {
            throw new InvalidOperationException(
                $"stale_process_candidate: pid {identity.Pid} now belongs to a different process instance. " +
                "Discover candidates again; no fallback target was selected.");
        }

        return current;
    }

    private static IReadOnlyList<ResolvedProcessTarget> GetCandidatesByName(string normalizedName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(normalizedName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"process_discovery_failed: unable to enumerate processes named '{normalizedName}'.",
                ex);
        }

        var candidates = new List<ResolvedProcessTarget>(processes.Length);
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    candidates.Add(CreateTarget(process));
                }
            }
            catch (ProcessExitedDuringDiscoveryException)
            {
                // A process that exits during enumeration is not a live candidate.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.StartTimeUtc)
            .ThenByDescending(candidate => candidate.Identity.Pid)
            .ToArray();
    }

    private static ResolvedProcessTarget CreateTarget(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new ProcessExitedDuringDiscoveryException();
            }

            var startTimeUtc = process.StartTime.ToUniversalTime();
            var identity = new ProcessInstanceIdentity(process.Id, startTimeUtc.ToFileTimeUtc());
            return new ResolvedProcessTarget(
                Identity: identity,
                ProcessName: process.ProcessName,
                StartTimeUtc: startTimeUtc,
                MainWindowHandle: SafeGetMainWindowHandle(process),
                MainWindowTitle: Bound(SafeGetMainWindowTitle(process), 512));
        }
        catch (ProcessExitedDuringDiscoveryException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                $"process_identity_unavailable: unable to establish a stable identity for process {process.Id}.");
        }
    }

    internal static string NormalizeProcessName(string processName)
    {
        var fileName = Path.GetFileName(processName.Trim());
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private static string Bound(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static long SafeGetMainWindowHandle(Process process)
    {
        try
        {
            return process.MainWindowHandle.ToInt64();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeGetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class ProcessExitedDuringDiscoveryException : Exception
    {
    }
}
