using System.Diagnostics;
using System.Globalization;
using WpfToolsMcp.Contracts;

namespace WpfToolsMcp.Automation;

internal readonly record struct ProcessInstanceIdentity(int Pid, long StartTimeFileTimeUtc)
{
    internal string Value => $"{Pid}:{StartTimeFileTimeUtc}";

    internal bool Matches(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            return process.Id == Pid &&
                   !process.HasExited &&
                   process.StartTime.ToUniversalTime().ToFileTimeUtc() == StartTimeFileTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParse(string value, out ProcessInstanceIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
               pid > 0 &&
               long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime) &&
               startTime > 0 &&
               Assign(pid, startTime, out identity);

        static bool Assign(int pid, long startTime, out ProcessInstanceIdentity parsed)
        {
            parsed = new ProcessInstanceIdentity(pid, startTime);
            return true;
        }
    }
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
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return identity.Matches(process);
        }
        catch
        {
            return false;
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
        catch (InvalidOperationException)
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
            catch (InvalidOperationException)
            {
                // The process exited or its stable identity could not be read during discovery.
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
                throw new InvalidOperationException($"Process {process.Id} exited during discovery.");
            }

            var startTimeUtc = process.StartTime.ToUniversalTime();
            var identity = new ProcessInstanceIdentity(process.Id, startTimeUtc.ToFileTimeUtc());
            return new ResolvedProcessTarget(
                Identity: identity,
                ProcessName: process.ProcessName,
                StartTimeUtc: startTimeUtc,
                MainWindowHandle: process.MainWindowHandle.ToInt64(),
                MainWindowTitle: process.MainWindowTitle ?? string.Empty);
        }
        catch (InvalidOperationException)
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
}
