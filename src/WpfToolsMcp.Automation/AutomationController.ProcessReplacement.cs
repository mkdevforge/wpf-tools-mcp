namespace WpfToolsMcp.Automation;

internal sealed record ProcessReplacementIdentitySnapshot(
    IReadOnlyList<string> ElementIds,
    IReadOnlyList<long> WindowHandles);

public sealed partial class AutomationController
{
    private const int MaximumRetiredElementIds = 200_000;
    private const int MaximumRetiredWindowHandles = 8192;

    private int _replacementRetirementStarted;
    private readonly object _processReplacementIdentitySync = new();
    private readonly HashSet<string> _retiredElementIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredElementIdOrder = new();
    private readonly HashSet<long> _retiredWindowHandles = new();
    private readonly Queue<long> _retiredWindowHandleOrder = new();
    private readonly HashSet<long> _observedWindowHandles = new();
    private readonly Queue<long> _observedWindowHandleOrder = new();

    internal bool IsProcessReplacementRetirementStarted =>
        Volatile.Read(ref _replacementRetirementStarted) != 0;

    internal async Task<ProcessReplacementIdentitySnapshot> BeginProcessReplacementRetirementAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (Interlocked.CompareExchange(ref _replacementRetirementStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("Process replacement retirement is already in progress.");
        }

        var lockTaken = false;
        try
        {
            await _toolMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

            var current = _elementHandles.SnapshotIdentities();
            lock (_processReplacementIdentitySync)
            {
                return new ProcessReplacementIdentitySnapshot(
                    _retiredElementIdOrder
                        .Concat(current.ElementIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    _retiredWindowHandleOrder
                        .Concat(_observedWindowHandleOrder)
                        .Concat(current.WindowHandles)
                        .Distinct()
                        .ToArray());
            }
        }
        catch
        {
            Volatile.Write(ref _replacementRetirementStarted, 0);
            throw;
        }
        finally
        {
            if (lockTaken)
            {
                _toolMutex.Release();
            }
        }
    }

    internal void CancelProcessReplacementRetirement()
    {
        if (Volatile.Read(ref _disposeStarted) == 0)
        {
            Volatile.Write(ref _replacementRetirementStarted, 0);
        }
    }

    internal void ImportRetiredProcessIdentities(ProcessReplacementIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_processReplacementIdentitySync)
        {
            foreach (var elementId in snapshot.ElementIds)
            {
                AddBoundedIdentity(
                    elementId,
                    _retiredElementIds,
                    _retiredElementIdOrder,
                    MaximumRetiredElementIds);
            }

            foreach (var windowHandle in snapshot.WindowHandles)
            {
                AddBoundedIdentity(
                    windowHandle,
                    _retiredWindowHandles,
                    _retiredWindowHandleOrder,
                    MaximumRetiredWindowHandles);
            }
        }
    }

    private void ThrowIfUnavailableForToolCall()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (Volatile.Read(ref _replacementRetirementStarted) != 0)
        {
            throw new InvalidOperationException(
                "session_replacement_in_progress: the attached process is being replaced. Retry with the successor session.");
        }
    }

    private static void AddBoundedIdentity<T>(
        T value,
        HashSet<T> values,
        Queue<T> order,
        int capacity)
        where T : notnull
    {
        if (!values.Add(value))
        {
            return;
        }

        order.Enqueue(value);
        while (order.Count > capacity)
        {
            values.Remove(order.Dequeue());
        }
    }

    private void ObserveWindowHandle(long windowHandle)
    {
        if (windowHandle == 0)
        {
            return;
        }

        lock (_processReplacementIdentitySync)
        {
            AddBoundedIdentity(
                windowHandle,
                _observedWindowHandles,
                _observedWindowHandleOrder,
                MaximumRetiredWindowHandles);
        }
    }

    internal void TrackOrRejectExternalWindowHandle(long windowHandle)
    {
        if (windowHandle == 0)
        {
            return;
        }

        var belongsToCurrentProcess = IsWindowOwnedByCurrentProcessInstance(windowHandle);
        if (IsRetiredWindowHandle(windowHandle) && !belongsToCurrentProcess)
        {
            throw CreateStaleWindowException(windowHandle);
        }

        if (belongsToCurrentProcess)
        {
            ObserveWindowHandle(windowHandle);
        }
    }

    private bool IsWindowOwnedByCurrentProcessInstance(long windowHandle)
    {
        if (_processIdentity is not ProcessInstanceIdentity identity ||
            ProcessTargetResolver.Observe(identity) != ProcessInstanceState.Current)
        {
            return false;
        }

        try
        {
            var hwnd = new IntPtr(windowHandle);
            if (!IsWindow(hwnd))
            {
                return false;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            return processId == (uint)identity.Pid;
        }
        catch
        {
            return false;
        }
    }

    private bool IsRetiredElementId(string elementId)
    {
        lock (_processReplacementIdentitySync)
        {
            return _retiredElementIds.Contains(elementId);
        }
    }

    private bool IsRetiredWindowHandle(long windowHandle)
    {
        lock (_processReplacementIdentitySync)
        {
            return _retiredWindowHandles.Contains(windowHandle);
        }
    }
}
