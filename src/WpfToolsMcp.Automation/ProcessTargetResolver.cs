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

internal readonly record struct ProcessExecutablePathObservation(
    string? ExecutablePath,
    string? UnavailableReason);

internal sealed record ResolvedProcessTarget(
    ProcessInstanceIdentity Identity,
    string ProcessName,
    DateTime StartTimeUtc,
    long MainWindowHandle,
    string MainWindowTitle,
    string? ExecutablePath,
    string? ExecutablePathUnavailableReason)
{
    internal ProcessCandidateInfo ToContract(int index) =>
        new(
            Index: index,
            ProcessInstanceId: Identity.Value,
            Pid: Identity.Pid,
            ProcessName: ProcessName,
            StartTimeUtc: StartTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            MainWindowHandle: MainWindowHandle,
            MainWindowTitle: MainWindowTitle,
            ExecutablePath: ExecutablePath,
            ExecutablePathUnavailableReason: ExecutablePathUnavailableReason);
}

internal static class ProcessTargetResolver
{
    private const int MaximumReturnedCandidates = 25;
    internal const int MaximumExecutablePathLength = 512;

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
            throw FailureDiagnostics.Exception(
                code: "process_not_found",
                stage: "process_discovery",
                detail: "No live process matched the requested process name.",
                retryable: true,
                recoveryActions: ["retry"]);
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
        catch (Exception ex) when (ex is ArgumentException or ProcessExitedDuringDiscoveryException)
        {
            throw FailureDiagnostics.Exception(
                code: "process_not_found",
                stage: "process_discovery",
                detail: "The requested process is not running.",
                retryable: false,
                recoveryActions: [FailureDiagnostics.Recovery.SelectProcessInstance],
                inner: ex);
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
        catch (ActionableFailureException ex) when (
            string.Equals(ex.Failure.Code, "process_not_found", StringComparison.Ordinal))
        {
            throw FailureDiagnostics.Exception(
                code: "stale_process_candidate",
                stage: "process_discovery",
                detail: "The selected process candidate is no longer running.",
                retryable: false,
                recoveryActions: ["select_process_instance"],
                inner: ex);
        }

        if (current.Identity != identity)
        {
            throw FailureDiagnostics.Exception(
                code: "stale_process_candidate",
                stage: "process_discovery",
                detail: "The selected PID now belongs to a different process instance.",
                retryable: false,
                recoveryActions: ["select_process_instance"]);
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
            throw FailureDiagnostics.Exception(
                code: "process_discovery_failed",
                stage: "process_discovery",
                detail: "Live processes could not be enumerated.",
                retryable: true,
                recoveryActions: ["retry"],
                inner: ex);
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
            var executablePath = GetExecutablePathBestEffort(process);
            return new ResolvedProcessTarget(
                Identity: identity,
                ProcessName: process.ProcessName,
                StartTimeUtc: startTimeUtc,
                MainWindowHandle: SafeGetMainWindowHandle(process),
                MainWindowTitle: Bound(SafeGetMainWindowTitle(process), 512),
                ExecutablePath: executablePath.ExecutablePath,
                ExecutablePathUnavailableReason: executablePath.UnavailableReason);
        }
        catch (ProcessExitedDuringDiscoveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProcessIntegrityLevelComparison? integrityComparison = null;
            if (ProcessIntegrityLevelInspector.TryCompareWithCurrentProcess(
                    process.Id,
                    out var measuredIntegrity))
            {
                integrityComparison = measuredIntegrity;
            }

            var classified = FailureDiagnostics.Classify(
                ex,
                FailureDiagnostics.Stages.ProcessDiscovery,
                integrityComparison);
            if (classified.Code is FailureDiagnostics.Codes.AccessDenied or
                FailureDiagnostics.Codes.ElevationMismatch)
            {
                throw new ActionableFailureException(classified, ex);
            }

            throw FailureDiagnostics.Exception(
                code: FailureDiagnostics.Codes.ProcessIdentityUnavailable,
                stage: FailureDiagnostics.Stages.ProcessDiscovery,
                detail: "A stable identity could not be established for a candidate process.",
                retryable: true,
                recoveryActions: [FailureDiagnostics.Recovery.Retry, FailureDiagnostics.Recovery.SelectProcessInstance],
                inner: ex);
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

    internal static ProcessExecutablePathObservation GetExecutablePathBestEffort(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // MainModule.FileName is the public in-process .NET source available here.
        // Command-line retrieval is deferred because Process has no equivalent public
        // contract; adding WMI or a native platform source is separate scope, not a
        // privacy restriction for this trusted local tool.
        try
        {
            var executablePath = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(executablePath)
                ? new ProcessExecutablePathObservation(
                    ExecutablePath: null,
                    UnavailableReason: "mainModuleFileNameUnavailable")
                : new ProcessExecutablePathObservation(
                    ExecutablePath: Bound(executablePath, MaximumExecutablePathLength),
                    UnavailableReason: null);
        }
        catch (Exception ex)
        {
            var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
            return new ProcessExecutablePathObservation(
                ExecutablePath: null,
                UnavailableReason: Bound($"mainModuleReadFailed:{exceptionType}", 256));
        }
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
